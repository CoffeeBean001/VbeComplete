// （如 CodePane.GetSelection 的四个 ByRef Long），不做通用 COM 框架。

using System;
using System.Runtime.InteropServices;

namespace VbeComplete
{
    internal static class ComCall
    {
        private const ushort VT_BYREF = 0x4000;
        private const ushort VT_I4 = 0x0003;
        private const ushort DISPATCH_METHOD = 0x01;

        // x64 上 VARIANT 是 24 字节；本项目只发布 win-x64，位数不对就整体放弃。
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct VARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr byref;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPPARAMS
        {
            public IntPtr rgvarg;
            public IntPtr rgdispidNamedArgs;
            public int cArgs;
            public int cNamedArgs;
        }

        [ComImport]
        [Guid("00020400-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDispatchRaw
        {
            [PreserveSig] int GetTypeInfoCount(out uint pctinfo);
            [PreserveSig] int GetTypeInfo(uint iTInfo, int lcid, out IntPtr ppTInfo);
            [PreserveSig] int GetIDsOfNames(ref Guid riid,
                [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] rgszNames,
                uint cNames, int lcid,
                [MarshalAs(UnmanagedType.LPArray)] int[] rgDispId);
            [PreserveSig] int Invoke(int dispIdMember, ref Guid riid, int lcid, ushort wFlags,
                ref DISPPARAMS pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);
        }

        private static readonly bool _ok = Environment.Is64BitProcess;

        // 名字 → dispId 的解析要跨进程问一次，每轮轮询调两次就是两次白跑；
        // 参数缓冲每次都 Alloc/Free 也纯属浪费。两样都按"目标对象 + 方法名"缓存。
        // 只在 UI 线程调用（钩子线程绝不碰 COM），故不加锁。
        private const int NArgs = 4;
        private static readonly int VarSize = Marshal.SizeOf(typeof(VARIANT));

        private static WeakReference _idTarget;
        private static string _idMethod;
        private static int _idValue = -1;
        private static IntPtr _argBuf, _intBuf;

        private static bool TryId(IDispatchRaw disp, object target, string method, out int id)
        {
            var t = _idTarget == null ? null : _idTarget.Target;
            if (t != null && ReferenceEquals(t, target) && string.Equals(method, _idMethod, StringComparison.Ordinal))
            {
                id = _idValue;
                return true;
            }
            int[] ids = new int[1];
            Guid iid = Guid.Empty;
            int hr = disp.GetIDsOfNames(ref iid, new[] { method }, 1, 0, ids);
            if (hr != 0 || ids[0] < 0) { id = 0; return false; }
            _idTarget = new WeakReference(target);
            _idMethod = method;
            _idValue = ids[0];
            id = ids[0];
            return true;
        }

        // 四个 ByRef Long 的参数摆法只跟参数个数有关，造一次就够。
        private static bool EnsureBufs()
        {
            if (_argBuf != IntPtr.Zero) return true;
            IntPtr args = Marshal.AllocHGlobal(VarSize * NArgs);
            IntPtr ints = Marshal.AllocHGlobal(sizeof(int) * NArgs);
            if (args == IntPtr.Zero || ints == IntPtr.Zero)
            {
                if (args != IntPtr.Zero) Marshal.FreeHGlobal(args);
                if (ints != IntPtr.Zero) Marshal.FreeHGlobal(ints);
                return false;
            }
            for (int i = 0; i < VarSize * NArgs; i++) Marshal.WriteByte(args, i, 0);
            // rgvarg 是【逆序】的：最后一个参数在最前面
            for (int i = 0; i < NArgs; i++)
            {
                IntPtr slot = IntPtr.Add(args, i * VarSize);
                Marshal.WriteInt16(slot, 0, unchecked((short)(VT_BYREF | VT_I4)));
                Marshal.WriteIntPtr(slot, 8, IntPtr.Add(ints, (NArgs - 1 - i) * sizeof(int)));
            }
            _argBuf = args;
            _intBuf = ints;
            return true;
        }

        // 调一个"四个 ByRef Long 出参"的方法，参数按源码顺序给出，内部按 COM 规矩逆序摆放。
        public static bool CallFourOut(object target, string method,
            out int a, out int b, out int c, out int d)
        {
            a = b = c = d = 0;
            if (!_ok || target == null || string.IsNullOrEmpty(method)) return false;
            if (!EnsureBufs()) return false;

            IntPtr pDisp = IntPtr.Zero;
            IDispatchRaw disp = null;
            try
            {
                pDisp = Marshal.GetIDispatchForObject(target);
                disp = (IDispatchRaw)Marshal.GetObjectForIUnknown(pDisp);
                if (disp == null) return false;

                int id;
                if (!TryId(disp, target, method, out id)) return false;

                for (int i = 0; i < NArgs; i++) Marshal.WriteInt32(_intBuf, i * sizeof(int), 0);

                DISPPARAMS dp = new DISPPARAMS
                {
                    rgvarg = _argBuf,
                    rgdispidNamedArgs = IntPtr.Zero,
                    cArgs = NArgs,
                    cNamedArgs = 0,
                };
                Guid iid = Guid.Empty;
                int hr = disp.Invoke(id, ref iid, 0, DISPATCH_METHOD, ref dp,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (hr != 0) return false;

                a = Marshal.ReadInt32(_intBuf, 0);
                b = Marshal.ReadInt32(_intBuf, 1 * sizeof(int));
                c = Marshal.ReadInt32(_intBuf, 2 * sizeof(int));
                d = Marshal.ReadInt32(_intBuf, 3 * sizeof(int));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                // GetObjectForIUnknown 又 AddRef 了一次，必须配对释放：
                if (disp != null) { try { Marshal.ReleaseComObject(disp); } catch { } }
                if (pDisp != IntPtr.Zero) Marshal.Release(pDisp);
            }
        }
    }
}

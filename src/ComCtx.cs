// COM 接入层：拿到运行中的 VBE 并缓存（全走 IDispatch 后期绑定，不绑 Office 版本）。
// 只有 UI 线程（STA）能调；宿主不在就退避、绝不反复试，缓存代理每次用前探活。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VbeComplete
{
    internal static class ComCtx
    {
        // oleaut32!GetActiveObject（.NET 5+ 已从 Marshal 上删除）
        [DllImport("oleaut32.dll", PreserveSig = false)]
        private static extern void GetActiveObject(ref Guid rclsid, IntPtr pvReserved,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

        [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
        private static extern int CLSIDFromProgID(string progId, out Guid pclsid);

        // 按 ProgID 取正在运行的实例；没有实例返回 null（绝不启动新实例）。
        public static object GetActive(string progId)
        {
            Guid clsid;
            if (CLSIDFromProgID(progId, out clsid) != 0) return null;
            try
            {
                object o;
                GetActiveObject(ref clsid, IntPtr.Zero, out o);
                return o;
            }
            catch (Exception)
            {
                // MK_E_UNAVAILABLE(0x800401E3) = 宿主没在跑，这是正常情况，不是错误
                return null;
            }
        }

        // 故按前台进程名排一条候选链，先试 KET。
        private static string ForegroundProcess()
        {
            try { return Native.ProcessNameOfPid(Native.PidOf(Native.GetForegroundWindow())) ?? ""; }
            catch { return ""; }
        }

        private static bool IsWpsProcess(string proc) =>
            string.Equals(proc, "wps", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(proc, "et", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(proc, "wpp", StringComparison.OrdinalIgnoreCase);

        private static bool IsOfficeProcess(string proc) =>
            string.Equals(proc, "EXCEL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(proc, "WINWORD", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(proc, "POWERPNT", StringComparison.OrdinalIgnoreCase);

        private static bool IsHostProcess(string proc) => IsWpsProcess(proc) || IsOfficeProcess(proc);

        // 比 Application.Hwnd 的进程号最准。
        private static bool HostPidMatches(object app, uint fgPid)
        {
            if (fgPid == 0) return true;
            try
            {
                long hw = Convert.ToInt64(((dynamic)app).Hwnd);
                if (hw == 0) return true;
                return Native.PidOf(new IntPtr(hw)) == fgPid;
            }
            catch
            {
                return true;
            }
        }

        public static List<string> HostCandidates() => HostCandidates(ForegroundProcess());

        public static List<string> HostCandidates(string proc)
        {
            var list = new List<string>();
            void Add(string id)
            {
                if (!string.IsNullOrEmpty(id) && !list.Contains(id)) list.Add(id);
            }

            if (IsWpsProcess(proc))
            {
                Add("KET.Application");      // WPS 表格
                Add("KWPS.Application");     // WPS 文字
                Add("KWPP.Application");     // WPS 演示
                Add("Excel.Application");    // WPS 表格同时也挂成 Excel.Application
                Add("Word.Application");
                Add("PowerPoint.Application");
                return list;
            }

            // 非 WPS：上次成功的排前面（装多个 Office 时别来回跳）
            if (!string.IsNullOrEmpty(_lastOk)) Add(_lastOk);
            if (string.Equals(proc, "EXCEL", StringComparison.OrdinalIgnoreCase)) Add("Excel.Application");
            else if (string.Equals(proc, "WINWORD", StringComparison.OrdinalIgnoreCase)) Add("Word.Application");
            else if (string.Equals(proc, "POWERPNT", StringComparison.OrdinalIgnoreCase)) Add("PowerPoint.Application");
            Add("Excel.Application");
            Add("Word.Application");
            Add("PowerPoint.Application");
            Add("KET.Application");
            Add("KWPS.Application");
            Add("KWPP.Application");
            return list;
        }

        private static string _lastOk = "";

        public static void NoteHost(string progid) => _lastOk = progid;

        private static object _app;
        private static object _vbe;
        private static DateTime _lastVerify = DateTime.MinValue;

        // 失败退避：同一个前台进程 2 秒内不重试。键必须是前台进程名而不是时间，
        private static string _failKey;
        private static long _failAt;
        private const long FailBackoffMs = 2000;

        // VBE 有实例、却拿不到 VBE 对象（最常见原因：AccessVBOM=0）。
        public static bool VbeAccessDenied { get; private set; }

        // WPS 的引用不能常驻：持有 Application 引用时用户关 WPS 退不掉，
        // 故每轮用完就 FinalReleaseComObject，代价只是重新 GetActiveObject。
        private static bool _wpsHost;
        private static int _depth;
        private static long _lastReleaseAt;

        public static bool IsWpsHost => _wpsHost;

        public static void Begin() => _depth++;

        // 结束一次 COM 使用期。typing=true 时不释放（每 80ms 重建连接会盖过快照保鲜期），
        public static void End(bool typing = false)
        {
            if (_depth > 0) _depth--;
            if (_depth > 0 || !_wpsHost) return;
            if (typing && Environment.TickCount64 - _lastReleaseAt < 2000) return;
            Release();
        }

        // 释放缓存的对象（WPS 上每轮结束都要调）。
        public static void Release()
        {
            object vbe = _vbe, app = _app;
            _vbe = null;
            _app = null;
            _lastVerify = DateTime.MinValue;
            _lastReleaseAt = Environment.TickCount64;
            try { if (vbe != null && Marshal.IsComObject(vbe)) Marshal.FinalReleaseComObject(vbe); } catch { }
            try { if (app != null && Marshal.IsComObject(app)) Marshal.FinalReleaseComObject(app); } catch { }
        }

        private static bool IsWpsProgId(string progid) =>
            progid.StartsWith("KET.", StringComparison.OrdinalIgnoreCase) ||
            progid.StartsWith("KWPS.", StringComparison.OrdinalIgnoreCase) ||
            progid.StartsWith("KWPP.", StringComparison.OrdinalIgnoreCase);

        // 丢弃缓存（VBE 关掉了 / 报错之后必须调）。
        public static void Reset() => Release();

        // 拿 VBE 对象，返回 null 表示现在拿不到，调用方必须安静退出。
        public static dynamic Vbe(bool verify = true)
        {
            if (_vbe != null)
            {
                if (!verify) return _vbe;
                if ((DateTime.UtcNow - _lastVerify).TotalMilliseconds < 200) return _vbe;
                _lastVerify = DateTime.UtcNow;
                try
                {
                    object cp = ((dynamic)_vbe).ActiveCodePane;
                    if (cp != null) return _vbe;
                }
                catch { }
                Reset();
            }

            string key = ForegroundProcess();
            uint fgPid = Native.PidOf(Native.GetForegroundWindow());
            bool fgIsHost = IsHostProcess(key);
            long now = Environment.TickCount64;
            if (key == _failKey && now - _failAt < FailBackoffMs) return null;

            bool anyHost = false;
            string lastProgid = "";
            Exception lastErr = null;

            foreach (var progid in HostCandidates(key))
            {
                object app = GetActive(progid);
                if (app == null) continue;
                if (fgIsHost && !HostPidMatches(app, fgPid)) continue;
                anyHost = true;
                try
                {
                    dynamic dapp = app;
                    dynamic vbe = dapp.VBE;
                    if (ReferenceEquals(vbe, null)) { lastProgid = progid; continue; }

                    _app = app;
                    _vbe = vbe;
                    _lastVerify = DateTime.UtcNow;
                    _failKey = null;
                    VbeAccessDenied = false;
                    _wpsHost = IsWpsProgId(progid);
                    NoteHost(progid);
                    return _vbe;
                }
                catch (Exception e)
                {
                    // 有实例、但 VBE 拿不到：多半是"信任对 VBA 工程对象模型的访问"没开
                    lastErr = e;
                    lastProgid = progid;
                }
            }

            if (anyHost && lastErr != null && Native.VbeFrameExists())
            {
                // VBE 窗口在、VBE 对象却拿不到 ⇒ 就是 AccessVBOM 没开；VBE 没打开不算错误。
                VbeAccessDenied = true;
            }

            _failKey = key;
            _failAt = now;
            return null;
        }
    }
}

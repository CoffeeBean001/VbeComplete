// Win32 层：全局键盘钩子、按键注入、窗口/进程探测。
// 钩子回调里绝不允许任何 COM / 文件 IO —— 一次阻塞就会让整机键盘卡住（钩子超时会被系统摘掉）。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VbeComplete
{
    internal static class Native
    {
        // ---- 虚拟键码（只列本项目用到的） ----
        public const int VK_BACK = 0x08;
        public const int VK_TAB = 0x09;
        public const int VK_RETURN = 0x0D;
        public const int VK_ESCAPE = 0x1B;
        public const int VK_SPACE = 0x20;
        public const int VK_PRIOR = 0x21;
        public const int VK_NEXT = 0x22;
        public const int VK_END = 0x23;
        public const int VK_HOME = 0x24;
        public const int VK_LEFT = 0x25;
        public const int VK_UP = 0x26;
        public const int VK_RIGHT = 0x27;
        public const int VK_DOWN = 0x28;
        public const int VK_DELETE = 0x2E;
        public const int VK_OEM_2 = 0xBF;   // /
        public const int VK_DIVIDE = 0x6F;  // 小键盘 /
        public const int VK_S = 0x53;       // S —— 只用来认 Ctrl+S（保存会重排当前行）

        // '/' 键：主键盘 VK_OEM_2 与小键盘除号 VK_DIVIDE 都算，判定只此一处。
        public static bool IsSlashKey(int vk) => vk == VK_OEM_2 || vk == VK_DIVIDE;
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;    // Alt
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;

        // ---- 钩子 ----
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

        // KBDLLHOOKSTRUCT.flags
        public const uint LLKHF_INJECTED = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
            IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        // ---- 系统定时器精度 ----
        // Timer.Interval 只保证"至少这么久"（系统粒度 ~15.6ms）。
        // timeBeginPeriod 用完必须配对 timeEndPeriod：它会抬高整机时钟频率、费电。
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        public static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        public static extern uint TimeEndPeriod(uint uMilliseconds);

        // ---- 消息循环 ----
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        public const uint WM_QUIT = 0x0012;

        // ---- 按键状态 ----
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        public static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        // ---- 窗口 / 进程 ----
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        public const uint SWP_NOACTIVATE = 0x0010;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        // ---- 按键注入 ----
        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL, wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public static bool SendUnicodeChar(char ch)
        {
            INPUT[] seq = new INPUT[2];
            seq[0].type = INPUT_KEYBOARD;
            seq[0].u.ki.wVk = 0;
            seq[0].u.ki.wScan = ch;
            seq[0].u.ki.dwFlags = KEYEVENTF_UNICODE;
            seq[1] = seq[0];
            seq[1].u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            return SendInput(2, seq, Marshal.SizeOf(typeof(INPUT))) == 2;
        }

        public static bool SendVk(int vk, bool ctrl = false, bool shift = false, bool alt = false)
        {
            // 物理按住的修饰键不要合成：命令失败时用户的手还按在 Ctrl 上，我们再注入一对
            // Ctrl down/up 会把逻辑键状态清掉，之后 Ctrl+C/V/Z 全失灵，直到用户松手重按。
            ctrl = ctrl && !Down(VK_CONTROL);
            shift = shift && !Down(VK_SHIFT);
            alt = alt && !Down(VK_MENU);

            int n = 0;
            INPUT[] seq = new INPUT[8];

            if (ctrl) { seq[n++] = VkInput(VK_CONTROL, false); }
            if (shift) { seq[n++] = VkInput(VK_SHIFT, false); }
            if (alt) { seq[n++] = VkInput(VK_MENU, false); }
            seq[n++] = VkInput(vk, false);
            seq[n++] = VkInput(vk, true);
            if (alt) { seq[n++] = VkInput(VK_MENU, true); }
            if (shift) { seq[n++] = VkInput(VK_SHIFT, true); }
            if (ctrl) { seq[n++] = VkInput(VK_CONTROL, true); }

            INPUT[] exact = new INPUT[n];
            Array.Copy(seq, exact, n);
            return SendInput((uint)n, exact, Marshal.SizeOf(typeof(INPUT))) == (uint)n;
        }

        private static INPUT VkInput(int vk, bool up)
        {
            INPUT it = new INPUT();
            it.type = INPUT_KEYBOARD;
            it.u.ki.wVk = (ushort)vk;
            it.u.ki.wScan = 0;
            it.u.ki.dwFlags = up ? KEYEVENTF_KEYUP : 0;
            // 方向键 / Delete 等需要 EXTENDED 位，否则某些应用收不到
            if (vk == VK_UP || vk == VK_DOWN || vk == VK_LEFT || vk == VK_RIGHT ||
                vk == VK_DELETE || vk == VK_HOME || vk == VK_END ||
                vk == VK_PRIOR || vk == VK_NEXT)
            {
                it.u.ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
            }
            return it;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr hWnd);

        // ---- VBE 自带弹窗（成员列表 / 参数信息 / 模态框）----
        // 判据是"类名 + 可见"：
        //   · 隐藏的同名窗口到处都有 —— VBE 平时就把成员列表窗藏起来复用，藏着的绝不算生效；
        //   · 不能按进程过滤 —— 这两个类名只有 VBE 自己在用，而"是哪个 VBE"很难判准
        //     （Excel 和 WPS 各带一个，FindWindow 只给 z 序里的第一个）。
        private static readonly string[] VbePopupClasses = { "NameListWndClass", "PopupTipWndClass" };

        // 宿主（Office / WPS）弹出的标准对话框 / 消息框：模态时我们的候选窗必须让路。
        private const string DialogClass = "#32770";

        // 一次枚举的结果：整体替换（引用赋值是原子的），免得钩子线程读到半更新的数据。
        private sealed class PopupScan
        {
            public RECT[] Rects = Array.Empty<RECT>();
            public bool Visible;      // 有可见的 VBE 提示窗
            public long Stamp;        // 扫描时刻（毫秒），用来判缓存新鲜度
        }

        private static PopupScan _scan;

        // 按键之后让缓存失效：VBE 的成员列表正是"敲一个点"弹出来的，
        // 缓存留着旧结果就会让位晚半拍（最多 100ms）。
        public static void InvalidatePopupScan() => Volatile.Write(ref _scan, null);

        // 扫描结果缓存 100ms：枚举顶层窗口约 0.65ms（实测 595 个顶层窗口），
        // 每轮都做不可取，太久又会让位不及时。
        private static PopupScan ScanVbePopups()
        {
            PopupScan cur = Volatile.Read(ref _scan);
            long now = Environment.TickCount64;
            if (cur != null && now - cur.Stamp < 100) return cur;

            var found = new List<RECT>(4);
            bool visible = false;
            try
            {
                EnumWindows((h, p) =>
                {
                    try
                    {
                        string cls = ClassNameOf(h);
                        if (Array.IndexOf(VbePopupClasses, cls) < 0) return true;   // 继续枚举
                        if (!IsWindowVisible(h)) return true;
                        // 故意不按进程过滤：这两个类名只有 VBE 自己在用，
                        // 而"是哪个 VBE"很难判准（Excel / WPS 各一个，FindWindow 只给第一个）。
                        visible = true;
                        RECT r;
                        if (GetWindowRect(h, out r) &&
                            r.Right - r.Left > 0 && r.Bottom - r.Top > 0)
                            found.Add(r);
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            var next = new PopupScan
            {
                Rects = found.ToArray(),
                Visible = visible,
                Stamp = now,
            };
            Volatile.Write(ref _scan, next);
            return next;
        }

        // VBE 自带弹窗是不是正在生效：成员列表（Auto List Members）/ 参数信息（Quick Info）/ 模态对话框。
        public static bool VbeOwnPopupVisible()
        {
            try
            {
                // 成员列表 / 参数信息：这两类窗口不抢焦点（打字仍然进代码窗），只能按类名 + 可见来认。
                if (ScanVbePopups().Visible) return true;

                // 模态框：前台窗口就是它自己。
                // 不按进程判：前台是标准对话框时 VBE 代码窗一定已经失焦，候选窗本来就不该留着。
                IntPtr fg = GetForegroundWindow();
                return fg != IntPtr.Zero && ClassNameOf(fg) == DialogClass;
            }
            catch { return false; }
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        // VBE 主窗口在不在。WPS 上 VBE 没开时 Application.VBE 直接 E_FAIL，别误报成"被禁止访问"。
        public static bool VbeFrameExists() =>
            FindWindow("wndclass_desked_gsk", null) != IntPtr.Zero;

        // WPS 主窗（OpusApp）是否已关/被隐藏：此时 VBE 顶层窗口还在，只看 VbeFrameExists 会继续调 COM 卡死。
        public static bool WpsMainWindowGone()
        {
            IntPtr h = FindWindow("OpusApp", null);
            return h == IntPtr.Zero || !IsWindowVisible(h);
        }

        // 当前可见的 VBE 提示窗屏幕矩形；探测失败返回空数组（宁可重叠，也不乱挪窗）。
        public static RECT[] VbePopupRects() => ScanVbePopups().Rects;

        public static bool Overlap(RECT a, RECT b) =>
            !(a.Right <= b.Left || a.Left >= b.Right || a.Bottom <= b.Top || a.Top >= b.Bottom);

        // ---- 线程焦点 / 插入符矩形 ----
        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO pgui);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        public static IntPtr FocusedHwndOf(uint tid)
        {
            if (tid == 0) return IntPtr.Zero;
            var gti = new GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            if (!GetGUIThreadInfo(tid, ref gti)) return IntPtr.Zero;
            return gti.hwndFocus;
        }

        public static IntPtr FocusedVbeWindow()
        {
            IntPtr frame = FindWindow("wndclass_desked_gsk", null);
            if (frame == IntPtr.Zero) return IntPtr.Zero;
            uint pid;
            uint tid = GetWindowThreadProcessId(frame, out pid);
            return FocusedHwndOf(tid);
        }

        // ---- 输入法组字状态 ----
        // 有组字串 = 回车是上屏键，绝不能抢；没有才放心接管。
        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetContext(IntPtr hWnd);

        [DllImport("imm32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
        private static extern int ImmGetCompositionStringW(IntPtr hIMC, int dwIndex,
            byte[] lpBuf, int dwBufLen);

        private const int GCS_COMPSTR = 0x0008;

        public static bool ImeComposing(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                IntPtr himc = ImmGetContext(hwnd);
                if (himc == IntPtr.Zero) return false;
                try { return ImmGetCompositionStringW(himc, GCS_COMPSTR, null, 0) > 0; }
                finally { ImmReleaseContext(hwnd, himc); }
            }
            catch { return false; }
        }

        // 插入符屏幕矩形 (x, top, bottom)：VBE 虽自绘，但仍维护真 caret（GetGUIThreadInfo.rcCaret）。
        public static bool CaretRect(out int x, out int top, out int bottom)
        {
            x = top = bottom = 0;
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            uint pid;
            uint tid = GetWindowThreadProcessId(fg, out pid);
            if (tid == 0) return false;

            var gti = new GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            if (!GetGUIThreadInfo(tid, ref gti)) return false;
            if (gti.hwndCaret == IntPtr.Zero) return false;

            POINT p1 = new POINT { X = gti.rcCaret.Left, Y = gti.rcCaret.Top };
            POINT p2 = new POINT { X = gti.rcCaret.Left, Y = gti.rcCaret.Bottom };
            if (!ClientToScreen(gti.hwndCaret, ref p1)) return false;
            if (!ClientToScreen(gti.hwndCaret, ref p2)) return false;
            x = p1.X; top = p1.Y; bottom = p2.Y;
            return true;
        }

        public static string ClassNameOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(256);
            int n = GetClassName(hwnd, sb, sb.Capacity);
            return n > 0 ? sb.ToString() : "";
        }

        public static uint PidOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 0;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            return pid;
        }

        public static string ProcessNameOfPid(uint pid)
        {
            if (pid == 0) return "";
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
                    return p.ProcessName;
            }
            catch { return ""; }
        }

        // ---- 输入法转换模式（中文标点档下 Shift+9 敲的是全角括号）----
        // 跨进程拿不到 ImmGetContext，只能 ImmGetDefaultIMEWnd + SendMessageTimeout。
        // 必须带 SMTO_ABORTIFHUNG：目标卡死要立刻返回，钩子线程被钉住就会被系统摘掉。
        private const uint WM_IME_CONTROL = 0x0283;
        private const int IMC_GETCONVERSIONMODE = 0x0001;
        private const int IME_CMODE_NATIVE = 0x0001;
        private const int IME_CMODE_FULLSHAPE = 0x0008;
        private const int IME_CMODE_SYMBOL = 0x0400;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        public static int ImeConversionMode(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return -1;
                IntPtr ime = ImmGetDefaultIMEWnd(hwnd);
                if (ime == IntPtr.Zero) return -1;
                IntPtr res;
                IntPtr got = SendMessageTimeout(ime, WM_IME_CONTROL,
                    (IntPtr)IMC_GETCONVERSIONMODE, IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out res);
                if (got == IntPtr.Zero) return -1;
                return (int)(res.ToInt64() & 0xFFFF);
            }
            catch { return -1; }
        }

        public static bool ImeFullwidthPunct(IntPtr hwnd)
        {
            int m = ImeConversionMode(hwnd);
            if (m < 0) return false;
            return (m & IME_CMODE_NATIVE) != 0 &&
                   (m & (IME_CMODE_SYMBOL | IME_CMODE_FULLSHAPE)) != 0;
        }

        // 需要自动配对的符号返回半角字符，否则 '\0'。
        // 必须用 MapVirtualKey(vk, 2) 问系统，别写死映射，也别用 mode 0（返回的是扫描码）。
        public static char PairCharForKey(int vk, bool shift)
        {
            if (!shift) return '\0';
            if (vk >= 0x60 && vk <= 0x69) return '\0';      // 小键盘不管
            try
            {
                uint baseCh = MapVirtualKey((uint)vk, 2) & 0xFFFF;
                if (baseCh == 0) return '\0';
                char ch = (char)baseCh;
                if (ch == '9') return '(';
                if (ch == '0') return ')';                 // 右半边已在右边时"跨过去"
                if (ch == '\'' || ch == '"') return '"';
                return '\0';
            }
            catch { return '\0'; }
        }

        public static char PairCharForIme(char half, IntPtr hwnd)
        {
            if (half == '(') return ImeFullwidthPunct(hwnd) ? '（' : half;
            // 右括号必须一起映射，否则中文标点档下自动配出的 `（` 会配上半角 `)`。
            if (half == ')') return ImeFullwidthPunct(hwnd) ? '）' : half;
            if (half == '"') return ImeFullwidthPunct(hwnd) ? '“' : half;
            return half;
        }

        // ---- GDI 文字 ----
        // TextRenderer 每调一次都会 GetHdc/ReleaseHdc 一次，候选窗一帧几十段文本就是几十次，
        // 单帧 1.7ms 全耗在这上面。这两条是最小面：自己量宽、自己画字。
        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; }

        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] public static extern int SetTextColor(IntPtr hdc, int color);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool TextOutW(IntPtr hdc, int x, int y, string s, int c);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetTextExtentPoint32W(IntPtr hdc, string s, int c, out SIZE sz);

        public const int TRANSPARENT = 1;

        public static int ColorRef(System.Drawing.Color c) => c.R | (c.G << 8) | (c.B << 16);

    }
}

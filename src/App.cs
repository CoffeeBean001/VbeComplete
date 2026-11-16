// 主流程：全局钩子 + 自适应轮询 + 按键路由。
// 钩子线程只读内存状态（一次 COM 都不许碰），真动作投递到 UI 线程；吃了没做成必须原样还回去。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VbeComplete
{
    internal sealed class App : ApplicationContext
    {
        private const string CodePaneClass = "VbaWindow";
        private const string VbeFrameClass = "wndclass_desked_gsk";
        private static readonly string[] VbePopupClasses = { "NameListWndClass", "PopupTipWndClass" };

        private readonly VbeBackend _be = new VbeBackend();
        private readonly Engine _eng;
        private readonly PopupForm _popup = new PopupForm();
        private readonly Control _disp = new Control();

        private System.Windows.Forms.Timer _timer;
        private NotifyIcon _tray;
        private EventWaitHandle _showEvent;
        private Thread _showThread;

        // ---- 跨线程共享的状态（钩子线程只读，UI 线程写） ----
        private volatile bool _codePaneFocused;
        private volatile bool _popupVisible;
        private bool _focusWarmed;
        private long _warmAt;
        private volatile bool _running = true;
        private volatile Snapshot _hookSnap;
        private long _popupBeat;
        // 按了"会写字"的键但文档没变 = 输入法可能正在组字（超时 2 秒就当 0）。
        private volatile int _pendingKeys;
        private long _pendingAt;
        private long _hookSnapTicks;
        // 片段窗 HWND：必须由 UI 线程在 Show 前存好，钩子线程只能读这个 IntPtr，碰 .Handle 会跨线程建窗。
        private volatile IntPtr _snippetHwnd;
        private long _lastKeyTs;
        // 最近一次"能改空白"的按键时刻：用户自己在敲空格/退格时绝不能搬光标。
        private long _lastBlankTs;
        // 最近一次"会写字"的按键时刻：用来区分"文本没变、光标前进"是打字竞态还是鼠标移动。
        private long _lastTextTs;
        // 最近一次 Ctrl+S 的时刻：VBE 保存会顺手重排当前行空白，这是最硬的前兆。
        private long _lastSaveTs;
        // Ctrl+S 那一刻的"保存前那一行 + 光标列"：保存期间轮询快照可能被清空，重排判定要靠它。
        private Snapshot _saveSnap;
        private long _saveSnapTs;
        // 最近一次"会写字"的时刻：后端据此把全工程作用域重算推迟到手停（单次 ~23ms，不能撞按键）。
        internal static long LastTypingTs
        {
            get => Volatile.Read(ref _lastTypingTs);
            set => Volatile.Write(ref _lastTypingTs, value);
        }
        private static long _lastTypingTs;

        private void NoteSave()
        {
            long now = Environment.TickCount64;
            _lastSaveTs = now;
            _saveSnapTs = now;
            var s = _hookSnap;
            if (s != null) _saveSnap = s;
        }

        internal void RunOnUi(Action a) { try { _disp.Invoke(a); } catch { } }

        // ---- 钩子 ----
        private Thread _hookThread;
        private IntPtr _hookHandle = IntPtr.Zero;
        private uint _hookThreadId;
        private Native.LowLevelKeyboardProc _hookProc;   // 必须保持引用，否则被 GC 回收
        // 已吞 keydown 的键 -> 时刻：必须带时标并清过期条目，否则丢一个 keyup 会让修饰键粘住。
        private readonly Dictionary<int, long> _swallowed = new Dictionary<int, long>();
        private const long SwallowPairMs = 1500;

        // ---- 轮询状态 ----
        private Snapshot _lastSnap;
        // 上次 Trigger 的位置（行+显示列）：快照非原子时同一键会被看到两次，问过就不再重复算。
        private int _trigLine = -1;
        private int _trigCaret = -1;
        private string _lastModName = "";
        private bool _deniedShown;
        private int _shutdownDone;

        private SnippetStore _snippetStore;
        private SnippetForm _snippetForm;

        // "刚按过键"的窗口：这期间的文本变化才算打字，粘贴/拖拽/别的程序改代码只收窗不弹。
        private const long KeyTriggerWindowMs = 1200;
        // 最近这么久没按过空白键，才认定空白变化是宿主干的（打字时轮询 50ms，不可能漏）。
        private const long NoBlankKeyMs = 300;
        // 刚按过 Ctrl+S 的认账窗口。
        private const long SaveWindowMs = 1200;
        // 快照非原子（VBE 先落文本后挪光标）：这段时间"文本没变、光标前进"按打字竞态处理。
        private const long TypingRaceMs = 250;

        public App()
        {
            // 强制建句柄：跨线程 BeginInvoke 需要一个真窗口
            var _ = _disp.Handle;

            _be.SendVk = (vk, ctrl, shift) => Native.SendVk(vk, ctrl, shift);

            _eng = new Engine { Backend = _be };
            _eng.OnShow = (items, sel, top) =>
            {
                // VBE 自己弹着窗（成员列表 / 参数信息 / 模态框）：候选窗必须让路，不能抢。
                if (Native.VbeOwnPopupVisible())
                {
                    _eng.Hide();
                    return;
                }
                _popup.SetData(items, sel, top, _eng.ViewRows);
                ShowPopupAtCaret();
                // 镜像标志必须跟着真实可见性走：无条件置 true 会让 ↑↓/PgUp/Esc 被永久吞掉。
                _popupVisible = _popup.Visible;
                _popupBeat = Environment.TickCount64;
                if (!_popupVisible) _eng.Hide();
            };
            _eng.OnHide = () =>
            {
                _popupVisible = false;
                _popup.ResetPlacement();
                if (_popup.Visible) _popup.Hide();
            };
            _eng.OnMove = () =>
            {
                _popup.UpdateView(_eng.SelectedIndex, _eng.TopIndex);
            };

            _popup.RowClicked += i => { _eng.SetSelected(i); _eng.Accept(); };
            _popup.Wheel += lines => _eng.ScrollBy(lines);
            _popup.ScrollTo += top =>
            {
                _eng.SetTop(top);
                _popup.UpdateView(_eng.SelectedIndex, _eng.TopIndex);
            };
        }

        public void Start()
        {
            // 开机自启必须在 SetupTray 之前：菜单勾选状态是建菜单时读注册表的，顺序反了会不一致。
            try { AutoStart.Sync(); } catch { }
            SetupTray();
            StartHook();
            StartShowListener();
            _timer = new System.Windows.Forms.Timer { Interval = 200 };
            _timer.Tick += (s, e) => PollOnce();
            _timer.Start();


            // 图标记录要等 Windows 注册后才建出来，太早找不到，所以延后 2.5 秒。
            var pin = new System.Windows.Forms.Timer { Interval = 2500 };
            pin.Tick += (s, e) =>
            {
                pin.Stop();
                pin.Dispose();
                try { TrayPin.EnsureVisible(); } catch { }
            };
            pin.Start();

            var warm = new System.Windows.Forms.Timer { Interval = 1200 };
            warm.Tick += (s, e) =>
            {
                warm.Stop();
                warm.Dispose();
                try { _popup.WarmUp(); } catch { }
                try { _eng.WarmScopes(); } catch { }
            };
            warm.Start();

            // 首次运行提示托盘位置：没有主窗口，不说的话用户会以为没启动。
            string first = Path.Combine(Settings.Home, "firstrun.txt");
            if (!File.Exists(first))
            {
                ShowBalloon("VbeComplete 已启动",
                    "没有主窗口：图标在右下角托盘里（若看不到，点一下任务栏的 ^ ，把它拖到外面即可常驻）。\r\n"
                    + "右键那个图标可以开关「开机自动启动」或退出。");
                try { File.WriteAllText(first, "1"); } catch { }
            }
        }

        private void ShowBalloon(string title, string text)
        {
            try
            {
                if (_tray == null) return;
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(5000);
            }
            catch { }
        }

        // 监听第二个实例的提示事件（互斥体已存在时 Program 会 Set）。
        private void StartShowListener()
        {
            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
                _showThread = new Thread(() =>
                {
                    while (_running)
                    {
                        try
                        {
                            if (!_showEvent.WaitOne(500)) continue;
                            if (!_running) return;
                            // 必须回 UI 线程碰 NotifyIcon
                            if (_disp.InvokeRequired)
                                _disp.BeginInvoke((Action)(() => ShowBalloon(
                                    "VbeComplete 已经在运行",
                                    "不用再启动一次。图标在右下角托盘的溢出区里，右键可以退出。")));
                            else
                                ShowBalloon("VbeComplete 已经在运行",
                                    "不用再启动一次。图标在右下角托盘的溢出区里，右键可以退出。");
                        }
                        catch { return; }
                    }
                })
                { IsBackground = true, Name = "show-listener" };
                _showThread.Start();
            }
            catch { }
        }

        // 托盘图标：没有它，用户只能去任务管理器杀进程。
        private void SetupTray()
        {
            try
            {
                var menu = new ContextMenuStrip();
                var title = new ToolStripMenuItem("VbeComplete · VBA 补全") { Enabled = false };
                menu.Items.Add(title);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("代码片段…", null, (s, e) =>
                {
                    try { ToggleSnippets(); }
                    catch { }
                });

                // 勾选状态按注册表真实情况显示，免得"勾着但其实早被清理软件删了"。
                var miAutoStart = new ToolStripMenuItem("开机自动启动")
                {
                    CheckOnClick = false,
                    Checked = AutoStart.IsRegistered(out _),
                    ToolTipText = "随 Windows 登录自动运行（写 HKCU 的 Run 键，不需要管理员）",
                };
                miAutoStart.Click += (s, e) =>
                {
                    bool want = !AutoStart.IsRegistered(out _);
                    bool ok = AutoStart.SetEnabled(want);
                    miAutoStart.Checked = AutoStart.IsRegistered(out _);

                    if (!ok)
                        ShowBalloon("开机自启没设置成功",
                            "写注册表被拒绝了（常见于安全软件拦截）。\r\n原因：" +
                            (string.IsNullOrEmpty(AutoStart.LastError) ? "未知" : AutoStart.LastError));
                };
                menu.Items.Add(miAutoStart);
                menu.Opening += (s, e) => miAutoStart.Checked = AutoStart.IsRegistered(out _);
                menu.Items.Add("退出", null, (s, e) => Shutdown());

                _tray = new NotifyIcon
                {
                    // 必须用自己的图标：SystemIcons.Application 在托盘里跟十几个程序一样，用户找不到。
                    Icon = IconAssets.Tray(),
                    Text = "VbeComplete · VBA 补全",
                    Visible = true,
                    ContextMenuStrip = menu,
                };
                // 双击托盘 = 打开片段窗（和菜单里那一项同一个动作）。
                _tray.DoubleClick += (s, e) =>
                {
                    try { ToggleSnippets(); } catch { }
                };
            }
            catch { }
        }

        // ---- 钩子线程 ----

        private void StartHook()
        {
            _hookThread = new Thread(HookLoop)
            {
                IsBackground = true,
                Name = "kbd-hook",
            };
            _hookThread.Start();
        }

        private void HookLoop()
        {
            _hookThreadId = Native.GetCurrentThreadId();
            _hookProc = HookProc;
            IntPtr mod = Native.GetModuleHandle(null);
            _hookHandle = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc, mod, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                return;
            }


            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            Native.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        // 钩子回调唯一入口：托管异常一旦穿过 CallNextHookEx，CLR 会 FailFast 直接杀进程。
        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try { return HookProcCore(nCode, wParam, lParam); }
            catch (Exception)
            {
                return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }
        }

        private IntPtr HookProcCore(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            bool isDown = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
            bool isUp = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
            if (!isDown && !isUp) return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            var kb = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                lParam, typeof(Native.KBDLLHOOKSTRUCT));

            // 注入的键（我们自己发的空格/括号/回车）不算"用户按键"：否则会自己触发自己
            if ((kb.flags & Native.LLKHF_INJECTED) != 0)
                return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            int vk = (int)kb.vkCode;

            // ---- keyup：跟被吞掉的 keydown 配对的也一起吞，否则 VBE 会收到半截按键 ----
            if (isUp)
            {
                lock (_swallowed)
                {
                    if (_swallowed.TryGetValue(vk, out long at))
                    {
                        _swallowed.Remove(vk);
                        if (Environment.TickCount64 - at <= SwallowPairMs) return (IntPtr)1;
                    }
                }
                return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            // 时标必须打在这里：放到别的回调里就晚了，那时文本可能已经变了。
            _lastKeyTs = Environment.TickCount64;
            _keyHandled = false;

            // 按键 ⇒ VBE 的弹窗状态可能刚变（敲一个点就弹出成员列表），扫描缓存必须作废。
            Native.InvalidatePopupScan();

            bool ctrl = Native.Down(Native.VK_CONTROL);
            bool shift = Native.Down(Native.VK_SHIFT);
            bool alt = Native.Down(Native.VK_MENU);
            bool win = Native.Down(Native.VK_LWIN) || Native.Down(Native.VK_RWIN);

            // 不在 VBE 代码窗（或片段窗占着焦点）：一个键都不碰。
            // 片段窗闸门必须看实时前台窗口（_codePaneFocused 是轮询算的，会滞后几十上百毫秒）。
            bool snipActive = SnippetFormActive();
            if (!_codePaneFocused || snipActive)
            {
                return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            if (win) return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            // 只记"能改空白"的键：敲 `1` 不算改空白，不能拿它挡住宿主重排后的光标搬家。
            if (vk == Native.VK_SPACE || vk == Native.VK_TAB ||
                vk == Native.VK_BACK || vk == Native.VK_DELETE)
            {
                _lastBlankTs = Environment.TickCount64;
                _lastTextTs = Environment.TickCount64;
            }
            if (ctrl && vk == Native.VK_S) NoteSave();

            // 那是输入法正在组字（回车=上屏、退格=删拼音，抢了就会出错）；文档一变计数清零。
            if (Editing.IsTextKey(vk) && !ctrl && !alt)
            {
                _lastTextTs = Environment.TickCount64;
                LastTypingTs = Environment.TickCount64;
                int n = _pendingKeys;
                if (n < 99) _pendingKeys = n + 1;
                _pendingAt = Environment.TickCount64;
                // 打完字立刻叫醒轮询，别等下一跳（否则候选窗慢半拍）。
                RequestFastPoll();
            }
            else if ((vk == Native.VK_BACK || vk == Native.VK_DELETE) && !ctrl && !alt)
            {
                LastTypingTs = Environment.TickCount64;
                RequestFastPoll();
            }

            // ---- 1. 候选窗可见时的导航键 ----
            if (PopupAlive && !ctrl && !alt)
            {
                // VBE 自带弹窗生效时，这套键归它 —— 候选窗必须让路，不能把键吞掉。
                // 只拦这一组键：Ctrl+/ 之类的快捷键跟 VBE 的弹窗不冲突，别误伤。
                if (Native.VbeOwnPopupVisible())
                {
                    Post(() => { if (_popupVisible) _eng.Hide(); });
                    return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                }

                if (vk == Native.VK_DOWN && !shift)
                {
                    Post(() => _eng.Move(1));
                    return Swallow(vk);
                }
                if (vk == Native.VK_UP && !shift)
                {
                    Post(() => _eng.Move(-1));
                    return Swallow(vk);
                }
                // Shift+↑/↓：跳出候选列表并移光标一行（VBE 原生是扩选，只能我们自己落光标）。
                if ((vk == Native.VK_UP || vk == Native.VK_DOWN) && shift)
                {
                    int delta = vk == Native.VK_UP ? -1 : 1;
                    Post(() =>
                    {
                        _eng.Hide();
                        if (!_be.MoveCaretLine(delta)) Native.SendVk(vk, false, true);
                    });
                    return Swallow(vk);
                }
                if (vk == Native.VK_NEXT)
                {
                    Post(() => _eng.Move(_eng.PageSize));
                    return Swallow(vk);
                }
                if (vk == Native.VK_PRIOR)
                {
                    Post(() => _eng.Move(-_eng.PageSize));
                    return Swallow(vk);
                }
                // 轮询比的是去尾空白文本，`Set ` 与 `Set` 一样，所以这一步必须由钩子当场收。
                if (vk == Native.VK_SPACE || Rules.EndsWordKey(vk, shift))
                {
                    Post(() => _eng.Hide());
                    return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                }
                if (vk == Native.VK_TAB && !shift)
                {
                    Post(() => { if (!_eng.Accept()) Native.SendVk(Native.VK_TAB); });
                    return Swallow(vk);
                }
                if (vk == Native.VK_RETURN && !shift)
                {
                    // 回车只收列表、不确认候选（确认只用 Tab）；也不要 return，否则会吞掉紧接着的补 End If。
                    Post(() => _eng.Hide());
                }
                if (vk == Native.VK_ESCAPE)
                {
                    Post(() => _eng.Hide());
                    return Swallow(vk);
                }
                if (vk == Native.VK_LEFT || vk == Native.VK_RIGHT)
                {
                    Post(() => _eng.Hide());
                    return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                }
            }

            // ---- 2. 手动唤出（Ctrl+Space） ----
            if (ctrl && !shift && !alt && vk == Native.VK_SPACE)
            {
                Post(() =>
                {
                    var snap = _be.Snapshot();
                    if (snap != null) _eng.Trigger(true, snap);
                });
                return Swallow(vk);
            }

            // ---- 3. Ctrl+Q：片段窗开关（纯界面动作，排在改写文本的分支之前） ----
            if (ctrl && !shift && !alt && vk == 'Q')
            {
                Post(ToggleSnippets);
                return Swallow(vk);
            }

            // ---- 4. 会改写文本的组合键：做成了才算吃掉，失败就把键原样还回去 ----
            if (ctrl && !alt && Native.IsSlashKey(vk))
            {
                Post(() =>
                {
                    // 失败时别补发裸 `/`：SendVk 见 Ctrl 被物理按住会只发字符，等于往代码里插斜杠。
                    Commands.ToggleComment(_be);
                });
                return Swallow(vk);
            }
            // ⚠️ Shift+D / Shift+K 绝不能接管 —— 那是输入大写字母
            if (ctrl && !shift && !alt && vk == 'D')
                return SwallowAndRun('D', true, false, () => Commands.DuplicateLine(_be),
                    replayOnFail: false);
            if (ctrl && !shift && !alt && vk == 'K')
                return SwallowAndRun('K', true, false, () => Commands.DeleteLine(_be),
                    replayOnFail: false);
            if (ctrl && shift && !alt && vk == Native.VK_RETURN)
                return SwallowAndRun(Native.VK_RETURN, true, true, () => Commands.InsertLineAbove(_be),
                    replayOnFail: false);
            // ---- 4c. Ctrl+↑/↓：过程内跳到最后一个 Dim 行行尾 / 收尾行上方 ----
            if (ctrl && !shift && !alt && (vk == Native.VK_UP || vk == Native.VK_DOWN))
            {
                int dir = vk == Native.VK_UP ? -1 : 1;
                return SwallowAndRun(vk, true, false, () => Commands.CtrlProcJump(_be, dir));
            }

            // ---- 5. Shift+Enter：下方新起一行；必须排在回车自动缩进之前 ----
            if (shift && !ctrl && !alt && vk == Native.VK_RETURN)
                return SwallowAndRun(Native.VK_RETURN, false, true,
                    () => _be.NewLineBelow(false, true, true));

            // ---- 6. 回车自动缩进：只在整行注释 / 块结构开头 + 光标行尾时接管，其余放行 ----
            if (!ctrl && !shift && !alt && vk == Native.VK_RETURN)
            {
                string lineText;
                int caretDisp;
                bool fresh = FreshHookSnap(out lineText, out caretDisp);
                if (fresh && Editing.EnterIndentWanted(lineText, caretDisp))
                    return SwallowAndRun(Native.VK_RETURN, false, false,
                        () => _be.NewLineBelow(true, true, true));

                // 快照落后一两格时也接管：不猜结论，由 NewLineBelow 用真 COM 数据重判；组字时绝不接管。
                if (!Native.ImeComposing(Native.FocusedVbeWindow()) &&
                    Editing.LooksLikeBlockHeaderPrefix(lineText) &&
                    caretDisp + 16 >= Rules.DispCol(lineText, (lineText ?? "").Length))
                    return SwallowAndRun(Native.VK_RETURN, false, false,
                        () => _be.NewLineBelow(true, true, true));
            }

            // ---- 7. 退格：删整行空白 / 删一对空括号 ----
            if (!ctrl && !shift && !alt && vk == Native.VK_BACK)
            {
                string lineText;
                int caretDisp;
                if (FreshHookSnap(out lineText, out caretDisp))
                {
                    bool blankLine = Editing.BlankLineDeleteWanted(lineText, caretDisp);
                    int left, right;
                    bool pair = !blankLine &&
                                Editing.PairDeleteSpan(lineText, caretDisp, out left, out right);
                    if (blankLine)
                        return SwallowAndRun(Native.VK_BACK, false, false, () => _be.DeleteBlankLineHere());
                    if (pair)
                        return SwallowAndRun(Native.VK_BACK, false, false, () => _be.DeletePairHere());
                }
            }

            // ---- 8. 自动配对括号 / 引号（含全角） ----
            if (!ctrl && !alt)
            {
                char half = Native.PairCharForKey(vk, shift);
                if (half != '\0')
                {
                    // 到这里才问输入法状态（要发一次窗口消息），别每次按键都问。
                    char ch = Native.PairCharForIme(half, Native.GetForegroundWindow());
                    Post(() =>
                    {
                        _eng.Hide();
                        if (!_be.InsertPair(ch)) Native.SendUnicodeChar(ch);
                    });
                    return Swallow(vk);
                }
            }

            return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private IntPtr Swallow(int vk)
        {
            long now = Environment.TickCount64;
            lock (_swallowed)
            {
                if (_swallowed.Count > 0)
                {
                    List<int> dead = null;
                    foreach (var kv in _swallowed)
                        if (now - kv.Value > SwallowPairMs) (dead ??= new List<int>()).Add(kv.Key);
                    if (dead != null) foreach (int k in dead) _swallowed.Remove(k);
                }
                _swallowed[vk] = now;
            }
            return (IntPtr)1;
        }

        // 吞键 → UI 线程干事 → 干不成再原样还回去（全项目这条铁律的唯一落地点）。
        // 字母键与回车必须 replayOnFail=false：SendVk 见 Ctrl 按住会发出裸字符，比不做更糟。
        private IntPtr SwallowAndRun(int vk, bool restoreCtrl, bool restoreShift,
            Func<bool> work, bool hidePopup = true, bool replayOnFail = true)
        {
            Post(() =>
            {
                if (hidePopup) _eng.Hide();
                if (work()) return;
                if (!replayOnFail)
                {
                    return;
                }
                Native.SendVk(vk, restoreCtrl, restoreShift);
            });
            return Swallow(vk);
        }

        // 钩子线程用：纯 Win32 判断前台是不是片段窗，一次 COM 都不碰。
        private bool SnippetFormActive()
        {
            IntPtr fg = _snippetHwnd;
            if (fg == IntPtr.Zero) return false;
            IntPtr h = Native.GetForegroundWindow();
            for (int i = 0; i < 8 && h != IntPtr.Zero; i++)
            {
                if (h == fg) return true;
                IntPtr p = Native.GetParent(h);
                if (p == h) break;
                h = p;
            }
            return false;
        }

        // 钩子线程用：拿轮询留下的快照；过期或可能正在组字时返回 false，宁可这次不接管。
        private bool FreshHookSnap(out string lineText, out int caretDisp)
        {
            lineText = "";
            caretDisp = 0;
            var s = _hookSnap;
            if (s == null) return false;
            // 800ms 上限是"轮询卡住 / 刚切过焦点"的兜底。
            if (Environment.TickCount64 - _hookSnapTicks > 800) return false;
            // 出参先填上（即便最后返回 false）：回车"快照落后"那档还要用这行文本。
            lineText = s.Text ?? "";
            caretDisp = s.CaretCol;
            // 计数超过 2 秒还挂着说明那次变化没采到，必须清掉，否则回车/退格接管会被永久挡死。
            if (_pendingKeys > 0 && Environment.TickCount64 - _pendingAt > 2000) _pendingKeys = 0;
            if (_pendingKeys > 0) return false;
            return caretDisp > 0;
        }

        // Ctrl+Q：开关片段窗；开窗位置取开窗【之前】的光标。
        private void ToggleSnippets()
        {
            try
            {
                if (_snippetForm != null && _snippetForm.Visible)
                {
                    _snippetForm.Close_();
                    return;
                }
                if (_snippetForm == null)
                {
                    _snippetStore = new SnippetStore();
                    _snippetForm = new SnippetForm(_snippetStore, code => _be.InsertSnippet(code));
                    _snippetForm.OnClosedCallback = () =>
                    {
                        _snippetHwnd = IntPtr.Zero;
                        try { _be.FocusVbe(); } catch { }
                    };
                    _snippetForm.FormClosed += (s, e) => _snippetHwnd = IntPtr.Zero;
                }
                _eng.Hide();

                // 位置必须在窗子显示之前取：一显示前台就变了，插入符矩形就没了。
                int x, top, bottom;
                bool has = Native.CaretRect(out x, out top, out bottom);
                // 句柄在这里（UI 线程）逼出来并存给钩子线程；还没建好时是 0，判据退化成不拦。
                _ = _snippetForm.Handle;
                _snippetHwnd = _snippetForm.Handle;
                _snippetForm.OpenAtCaret(has, x, top, bottom);
            }
            catch (Exception)
            {
                _snippetHwnd = IntPtr.Zero;
            }
        }

        // 看心跳不只看标志：UI 线程被 COM 卡住时标志仍为 true，3 秒没心跳就当窗口没了。
        private bool PopupAlive =>
            _popupVisible && Environment.TickCount64 - _popupBeat < 3000;

        private void Post(Action a)
        {
            try
            {
                _disp.BeginInvoke((Action)(() =>
                {
                    // 每个键盘动作也是一次 COM 使用期：WPS 上用完必须释放。
                    ComCtx.Begin();
                    try { a(); }
                    catch { }
                    finally { ComCtx.End(); }
                }));
            }
            catch { }
        }

        // ---- 轮询 ----

        // 纯 Win32：焦点是不是在 VBE 代码窗格里（0.02ms，不碰 COM）。
        private static bool InVbeCodePane()
        {
            // 宿主不在前台的直接判否：一次 COM 都不发（切到别的程序时空闲开销归零）。
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || !ForegroundIsVbe(fg)) return false;

            IntPtr frame = Native.FindWindow(VbeFrameClass, null);
            if (frame == IntPtr.Zero) return false;
            uint pid;
            uint tid = Native.GetWindowThreadProcessId(frame, out pid);

            IntPtr focus = Native.FocusedHwndOf(tid);
            if (focus == IntPtr.Zero) return false;

            string cls = Native.ClassNameOf(focus);
            foreach (var p in VbePopupClasses)
                if (string.Equals(cls, p, StringComparison.OrdinalIgnoreCase)) return true;

            for (IntPtr h = focus; h != IntPtr.Zero; )
            {
                if (string.Equals(Native.ClassNameOf(h), CodePaneClass, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (h == frame) break;
                IntPtr parent = Native.GetParent(h);
                if (parent == h) break;
                h = parent;
            }
            // 焦点不在代码窗，但前台确实是 VBE：成员列表这类弹窗场景，照样算"在里面"。
            return string.Equals(Native.ClassNameOf(fg), VbeFrameClass, StringComparison.OrdinalIgnoreCase);
        }

        // 前台窗口是否属于 VBE（自身类名是 VBE 主窗，或祖先链里有它）。
        // 不比进程号：Excel / WPS 各带一个 VBE 时，FindWindow 只会给其中一个。
        private static bool ForegroundIsVbe(IntPtr fg)
        {
            for (IntPtr h = fg; h != IntPtr.Zero; )
            {
                if (string.Equals(Native.ClassNameOf(h), VbeFrameClass, StringComparison.OrdinalIgnoreCase))
                    return true;
                IntPtr parent = Native.GetParent(h);
                if (parent == h) break;
                h = parent;
            }
            return false;
        }

        private void PollOnce()
        {
            if (!_running) return;

            // WPS 上用完即放：整轮轮询允许缓存，结束一定释放（见 ComCtx.End）。
            ComCtx.Begin();
            bool typingNow = false;
            try
            {
                bool focused = InVbeCodePane();
                _codePaneFocused = focused;
                // VBE 自带弹窗（成员列表 / 参数信息 / 模态框）优先：候选窗一律让路。
                // 只在候选窗开着时才查 —— 闲时不做这个枚举（一次要扫 600 个顶层窗口）。
                if (_popupVisible && Native.VbeOwnPopupVisible())
                {
                    _eng.Hide();
                }
                // 刚切回代码窗时预热一次全工程作用域（19ms）；必须限速，否则切窗抖动会挤掉轮询。
                if (focused && !_focusWarmed &&
                    Environment.TickCount64 - _warmAt > 10000)
                {
                    _focusWarmed = true;
                    _warmAt = Environment.TickCount64;
                    try { _eng.WarmScopes(); } catch { }
                }
                else if (!focused) _focusWarmed = false;
                if (_popupVisible) _popupBeat = Environment.TickCount64;

                // WPS 正在退出（主窗已消失）：一次 COM 调用都不能发。
                if (ComCtx.IsWpsHost && Native.WpsMainWindowGone())
                {
                    ComCtx.Release();
                    if (_popupVisible) _eng.Hide();
                    _lastSnap = null;
                    _hookSnap = null;
                    _lastModName = "";
                    SetInterval(250);
                    return;
                }

                if (!focused)
                {
                    if (_popupVisible) _eng.Hide();
                    _lastSnap = null;
                    _hookSnap = null;
                    _lastModName = "";
                    _pendingKeys = 0;
                    SetInterval(250);
                    return;
                }

                // 刚按过键就别吃快照缓存：宿主可能已改过这行，而缓存键恰好命中有旧值。
                bool keyWindow = Environment.TickCount64 - _lastKeyTs <= KeyTriggerWindowMs;
                if (keyWindow)
                    _be.InvalidateSnapshot();

                // 没在打字时行文本不可能变，快照里那次"再读一次选区"的自洽复核可以省掉。
                typingNow = Environment.TickCount64 - _lastKeyTs < 900;
                Snapshot snap = _be.Snapshot(true, typingNow || keyWindow);
                if (snap == null)
                {
                    if (_popupVisible) _eng.Hide();
                    _hookSnap = null;
                    ExplainVbeDenied();
                    SetInterval(150);
                    return;
                }
                _deniedShown = false;

                // 三档：打字中抢速度；候选窗开着要盯点击；纯闲置时拉长间隔，把往返次数降下来。
                // 上限卡在 600ms —— 钩子那边的回车/退格要 800ms 以内的新鲜快照。
                SetInterval(typingNow
                    ? (ComCtx.IsWpsHost ? 80 : 50)
                    : _popupVisible
                        ? (ComCtx.IsWpsHost ? 300 : 150)
                        : (ComCtx.IsWpsHost ? 600 : 400));

                // 给钩子线程留一份"最近的行 + 光标列"，回车缩进/退格判据全靠它。
                _hookSnapTicks = Environment.TickCount64;
                _hookSnap = snap;

                bool changed = _lastSnap == null
                    || snap.Line != _lastSnap.Line
                    || !string.Equals(Rules.Rstrip(snap.Text), Rules.Rstrip(_lastSnap.Text), StringComparison.Ordinal)
                    || !string.Equals(snap.Module, _lastModName, StringComparison.OrdinalIgnoreCase);

                if (changed)
                {
                    _pendingKeys = 0;
                    Snapshot prev = _lastSnap;

                    // 保存期间轮询快照可能被清空或已跑到新行，用 Ctrl+S 前的快照当旧行锚。
                    if (_saveSnap != null &&
                        Environment.TickCount64 - _saveSnapTs <= SaveWindowMs &&
                        Rules.SameCodeIgnoringSpace(snap.Text, _saveSnap.Text) &&
                        Rules.SpaceCount(snap.Text) > Rules.SpaceCount(_saveSnap.Text) &&
                        (prev == null ||
                         !Rules.SameCodeIgnoringSpace(snap.Text, prev.Text) ||
                         Rules.SpaceCount(snap.Text) <= Rules.SpaceCount(prev.Text)))
                    {
                        prev = _saveSnap;
                    }

                    bool modSwitched = !string.Equals(snap.Module, _lastModName, StringComparison.OrdinalIgnoreCase)
                                       && _lastSnap != null;

                    // 行号变了说明光标是被搬过去的，不是在打字（真打字行号不会变）。
                    bool lineJumped = _lastSnap != null && snap.Line != _lastSnap.Line;
                    int prevLine = _lastSnap == null ? 0 : _lastSnap.Line;

                    _lastSnap = snap;
                    _lastModName = snap.Module;
                    _keyHandled = true;

                    // 判据：刚按过 Ctrl+S，或空白数变了且最近没按过空白键（人手一次只动一个空格）。
                    if (prev != null && !modSwitched && !lineJumped &&
                        Rules.SameCodeIgnoringSpace(snap.Text, prev.Text))
                    {
                        if (_popupVisible) _eng.Hide();
                        long nowTs = Environment.TickCount64;
                        int dSpace = Rules.SpaceCount(snap.Text) - Rules.SpaceCount(prev.Text);
                        bool saveRecent = nowTs - _lastSaveTs <= SaveWindowMs;
                        bool blankKeyRecent = nowTs - _lastBlankTs <= NoBlankKeyMs;
                        bool hostRespaced = saveRecent || (dSpace != 0 && !blankKeyRecent);
                        if (hostRespaced)
                        {
                            // 锚要用 VBE 保留的原选区列号（snap.CaretCol），不是上一轮快照的列，否则中文会落后两格。
                            int oldDisp = Rules.RespaceOldDisp(prev.Text ?? "", prev.CaretCol, snap.CaretCol);
                            int oldCaret = Rules.CharColFromDisp(prev.Text ?? "", oldDisp);
                            int want = Rules.RemapCaretOverSpaces(prev.Text ?? "", oldCaret, snap.Text ?? "");
                            int now = Rules.CharColFromDisp(snap.Text ?? "", snap.CaretCol);
                            if (want > 0 && want != now && _be.SetCaret(snap.Line, want))
                            {
                                _saveSnap = null;

                                var fixedSnap = _be.Snapshot();
                                if (fixedSnap != null)
                                {
                                    _lastSnap = fixedSnap;
                                    _hookSnap = fixedSnap;
                                }
                            }
                        }
                        return;
                    }

                    if (!modSwitched)
                    {
                        // 文本变了不等于用户在打字：粘贴、拖拽、别的程序改代码都只收窗不弹。
                        bool keyTriggered = _pendingKeys > 0
                                            || Environment.TickCount64 - _lastKeyTs <= KeyTriggerWindowMs;
                        if (!Rules.AutoTriggerWanted(keyTriggered, lineJumped))
                        {
                            if (_popupVisible) _eng.Hide();
                        }
                        else if (Native.VbeOwnPopupVisible())
                        {
                            // VBE 自己弹着窗（成员列表 / 参数信息）：不跟它抢，候选窗收掉。
                            if (_popupVisible) _eng.Hide();
                        }
                        else
                        {
                            // 别先 Hide 再 Trigger：Hide 会真的收窗，用户每敲一个字都看到候选窗闪一下。
                            _trigLine = snap.Line;
                            _trigCaret = snap.CaretCol;
                            _eng.Trigger(false, snap);
                        }
                    }
                }
                else
                {
                    // 例外：刚敲过字、光标只前进 1~4 格 —— 那是快照非原子的打字竞态，不能收。
                    int step = _lastSnap == null ? 0 : snap.CaretCol - _lastSnap.CaretCol;
                    bool typingRace = Environment.TickCount64 - _lastTextTs <= TypingRaceMs &&
                                      step >= 1 && step <= 4;
                    if (typingRace)
                    {
                        if (_popupVisible)
                        {
                            // 这一跳可能正是终结词的那个字符：快照非原子的另一半露出来，窗口会一直挂着。
                            int colNow = Rules.CharColFromDisp(snap.Text ?? "", snap.CaretCol);
                            if (Rules.WordBefore(snap.Text ?? "", colNow, out _).Length == 0)
                            {
                                _eng.Hide();
                            }
                        }
                        else if (!Native.VbeOwnPopupVisible() &&
                                 _eng.LastTriggerEmpty &&
                                 (snap.Line != _trigLine || snap.CaretCol != _trigCaret))
                        {
                            // LastTriggerEmpty 这条不能少，否则点一下变量就弹窗。
                            _trigLine = snap.Line;
                            _trigCaret = snap.CaretCol;
                            _eng.Trigger(false, snap);
                        }
                    }
                    else if (_popupVisible &&
                             (_lastSnap == null || _lastSnap.CaretCol != snap.CaretCol))
                    {
                        _eng.Hide();
                    }
                    _lastSnap = snap;
                }
            }
            catch { }
            finally
            {
                ComCtx.End(typingNow);
            }
        }

        private void SetInterval(int ms)
        {
            if (_timer == null) return;
            if (_timer.Interval != ms) _timer.Interval = ms;
        }

        // ---- 打字即时唤醒 ----
        private int _fastLeft;
        private volatile bool _wakePosted;
        private volatile bool _keyHandled;
        private bool _hiRes;
        private int _wakeGen;

        // 钩子线程调：只置标志 + BeginInvoke，绝不碰任何 WinForms 对象（跨线程建窗必炸）。
        private void RequestFastPoll()
        {
            if (_wakePosted) return;
            _wakePosted = true;
            Post(WakePoll);
        }

        // 不能用 WinForms Timer 补跳（最小 10ms，叠上时钟粒度后实际 15.6ms 以上）。
        private async void WakePoll()
        {
            _wakePosted = false;
            if (!_running) return;
            int gen = ++_wakeGen;
            try
            {
                PollOnce();
                if (!_running) { EndHiRes(); return; }
                // 这一键已经判完了 ⇒ 一跳都不用补。
                if (_keyHandled || Environment.TickCount64 - _lastKeyTs >= 150)
                {
                    EndHiRes();
                    return;
                }
                BeginHiRes();
                for (_fastLeft = 12; _fastLeft > 0 && _running; _fastLeft--)
                {
                    await Task.Delay(1);
                    if (!_running || gen != _wakeGen) return;
                    PollOnce();
                    if (_keyHandled || Environment.TickCount64 - _lastKeyTs >= 150) break;
                }
            }
            catch { }
            finally
            {
                if (gen == _wakeGen) EndHiRes();     // 只由最新那一代还原精度
            }
        }

        private void BeginHiRes()
        {
            if (_hiRes) return;
            try { Native.TimeBeginPeriod(1); _hiRes = true; } catch { }
        }

        private void EndHiRes()
        {
            if (!_hiRes) return;
            _hiRes = false;
            try { Native.TimeEndPeriod(1); } catch { }
        }

        // 焦点在代码窗却拿不到快照：多半宿主没勾「信任对 VBA 工程对象模型的访问」。
        // 这个开关归用户自己管 —— 程序不写注册表，只提示怎么开、开完要重开宿主。
        private void ExplainVbeDenied()
        {
            if (!ComCtx.VbeAccessDenied || _deniedShown) return;
            _deniedShown = true;

            ShowBalloon("拿不到 VBA 工程",
                "宿主里没有打开「信任对 VBA 工程对象模型的访问」。\r\n"
                + "WPS：文件 → 选项 → 信任中心 → 勾选该项；Excel：文件 → 选项 → 信任中心 → 宏设置。\r\n"
                + "勾完保存，把 WPS / Excel 完全退出（连托盘图标一起）再重新打开。");
        }

        private void ShowPopupAtCaret()
        {
            int x, top, bottom;
            if (!Native.CaretRect(out x, out top, out bottom))
            {
                _eng.Hide();
                return;
            }
            // ShowAt 是先定位再显示：分两步会让窗口在屏幕左上角闪一下。
            _popup.ShowAt(x, top, bottom);
        }

        // ---- 退出 ----

        // 幂等：托盘"退出"和 Application.Run 返回各会调一次。
        public void Shutdown()
        {
            if (Interlocked.Exchange(ref _shutdownDone, 1) != 0) return;
            _running = false;
            try { _timer?.Stop(); _timer?.Dispose(); _timer = null; } catch { }
            EndHiRes();     // 退出必须还原时钟精度，否则整机一直 1ms 心跳（费电）
            try
            {
                if (_hookThreadId != 0)
                    Native.PostThreadMessage(_hookThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
            try { if (_hookHandle != IntPtr.Zero) Native.UnhookWindowsHookEx(_hookHandle); } catch { }
            try { _showEvent?.Dispose(); } catch { }
            try { _popup.Hide(); _popup.Dispose(); } catch { }
            try { _snippetForm?.Hide(); _snippetForm?.Dispose(); } catch { }
            try { _snippetStore?.Dispose(); } catch { }
            try
            {
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            }
            catch { }

                     // 队列里最后几行别丢（进程一退就没了）
            ExitThread();
        }
    }
}

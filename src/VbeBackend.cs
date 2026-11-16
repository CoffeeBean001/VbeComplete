// VBE 后端：读快照、读模块全文、把补全写回去，以及那些"改一行"的操作。
// 只在 UI 线程调用；任何 COM 异常都被吞掉并返回失败，绝不让 VBE 忙时卡死自己。

using System;
using System.Collections.Generic;
using System.Text;

namespace VbeComplete
{
    internal sealed class Snapshot
    {
        public int Line;
        public string Text;
        public string Module;
        public int CaretCol;
        public override string ToString() => $"L{Line} C{CaretCol} {Module}";
    }

    // 引擎需要的 VBE 能力；抽成接口是为了让 Trigger / Accept 不碰 COM 也能测。
    internal interface IVbeSource
    {
        Snapshot Snapshot(bool verify = true, bool selCheck = true);
        ModuleScope CurrentScope();
        List<ModuleScope> ProjectScopes(out int signature);
        bool ApplyCompletion(int lineNo, int wordStartCol, int endCol, string completion);
    }

    internal sealed class VbeBackend : IVbeSource
    {
        // 快照缓存：行列都没动就复用上一轮
        private Snapshot _snapCache;
        private DateTime _snapAt = DateTime.MinValue;
        private readonly double _snapMaxAge = Settings.Double("SNAP_MAX_AGE", 1.0);

        // 模块镜像：整模块的行。改一行只改一行，永不为一次按键重读全文。
        private readonly List<string> _lines = new List<string>();
        private ModuleScope _scope;
        private string _mirrorName;              // 镜像属于哪个模块
        private bool _mirrorDirty = true;        // 改动来路不明（VBE 自己的命令 / 出错），下次整体重读
        private string _modText;                 // 全文缓存，只有整行跳转那种命令要
        private bool _modTextDirty = true;
        private int _curType;                    // 当前模块的组件类型
        private List<string> _curControls;       // 当前模块的窗体控件名

        // 全工程作用域缓存：模块名 -> 分析结果，按行数判过期（读全文跑正则比读行数贵得多）。
        private readonly Dictionary<string, ModuleScope> _modScopes =
            new Dictionary<string, ModuleScope>(StringComparer.OrdinalIgnoreCase);
        private List<ModuleScope> _scopesList = new List<ModuleScope>();
        private string _projectKey;             // 当前工程身份（Name|FileName），换工程要清缓存
        private string _pkeyCache;              // ProjectKey 的短缓存：读它要 3 次 COM，别每次都问
        private string _pkeyModule;
        private DateTime _pkeyAt = DateTime.MinValue;
        private int _scopesSig = int.MinValue;
        private DateTime _scopesAt = DateTime.MinValue;
        private readonly double _scopesTtl = Settings.Double("SCOPE_TTL", 2.0);
        // 打字中允许旧作用域列表顶多久（见 ProjectScopes）。
        private readonly double _scopesTypingTtl = Settings.Double("SCOPE_TTL_TYPING", 8.0);

        // 窗体控件名（模块名 -> 控件名 + 读取时刻）。走 Designer 约 23ms/窗体，故单独一条短缓存。
        private readonly Dictionary<string, KeyValuePair<List<string>, DateTime>> _formCtrls =
            new Dictionary<string, KeyValuePair<List<string>, DateTime>>(StringComparer.OrdinalIgnoreCase);
        private static readonly double FormCtrlTtl = Settings.Double("FORM_CTL_TTL", 5.0);
        private const int FormCtrlLimit = 300;      // 控件多到 300 个就不是正常窗体了，防卡死
        private const int MSForm = 3;               // vbext_ct_MSForm：UserForm 窗体

        // 行数变了以后，光标附近这么多行足够重新对齐；再多就直接整体重读。
        private const int MirrorWin = 24;
        private const int MirrorMaxShift = 400;

        public Action<int, bool, bool> SendVk;

        public void InvalidateSnapshot()
        {
            _snapCache = null;
            _paneObj = null;
        }

        private void Fail(Exception e)
        {
            ComCtx.Reset();
            InvalidateSnapshot();
            _mirrorDirty = true;
            _mirrorName = null;
            _modText = null;
            _modTextDirty = true;
            _pkeyCache = null;
            // 宿主重开 / 换了工程后，缓存的 ModuleScope 属于上一个工程（默认模块名到处重名），全部丢掉。
            _modScopes.Clear();
            _scopesList = new List<ModuleScope>();
            _scopesSig = int.MinValue;
            _scopesAt = DateTime.MinValue;
        }

        // 取"当前代码窗 + 它的 CodeModule"，拿不到返回 false。
        private bool TryCodePane(out dynamic pane, out dynamic module)
        {
            pane = null;
            module = null;
            try
            {
                dynamic vbe = ComCtx.Vbe();
                if (ReferenceEquals(vbe, null)) return false;
                pane = vbe.ActiveCodePane;
                if (ReferenceEquals(pane, null)) return false;
                module = pane.CodeModule;
                return !ReferenceEquals(module, null);
            }
            catch (Exception e)
            {
                Fail(e);
                pane = null;
                module = null;
                return false;
            }
        }

        // 轻量快照：多行选择或不在代码窗时返回 null；先只 GetSelection，行列没动就复用上一轮。
        public Snapshot Snapshot(bool verify = true, bool selCheck = true)
        {
            try
            {
                dynamic vbe = ComCtx.Vbe(verify);
                if (ReferenceEquals(vbe, null)) return null;
                dynamic cp = vbe.ActiveCodePane;
                if (ReferenceEquals(cp, null)) { InvalidateSnapshot(); return null; }

                int sl, sc, el, ec;
                if (!ComCall.CallFourOut(cp, "GetSelection", out sl, out sc, out el, out ec))
                {
                    InvalidateSnapshot();
                    return null;
                }
                if (sl != el) { InvalidateSnapshot(); return null; }

                if (_snapCache != null &&
                    _snapCache.Line == sl && _snapCache.CaretCol == ec &&
                    (DateTime.UtcNow - _snapAt).TotalSeconds <= _snapMaxAge)
                {
                    // 同一个代码窗对象 ⇒ 不可能换了模块：跳过 CodeModule + Name 两次往返
                    if (SamePane(cp)) return _snapCache;
                    // 行列相同 ≠ 还是同一个模块，必须再读一次 CodeModule.Name 才能复用缓存。
                    dynamic cmc = cp.CodeModule;
                    string mnow = "";
                    try { mnow = Convert.ToString(cmc.Name) ?? ""; } catch { }
                    if (string.Equals(mnow, _snapCache.Module, StringComparison.Ordinal))
                    {
                        RememberPane(cp);
                        return _snapCache;
                    }
                }

                dynamic cm = cp.CodeModule;
                string mod = "";
                try { mod = Convert.ToString(cm.Name) ?? ""; } catch { }
                string text = Convert.ToString(cm.Lines(sl, 1)) ?? "";

                // 快照必须自洽：VBE 先落文本再挪光标，所以读完行文本要再读一次选区。
                // 没在打字时行文本不会变，这次复核省掉（一次往返 50us 以上）。
                int sl2 = 0, sc2 = 0, el2 = 0, ec2 = 0;
                if (selCheck && ComCall.CallFourOut(cp, "GetSelection", out sl2, out sc2, out el2, out ec2))
                {
                    if (sl2 != el2) { InvalidateSnapshot(); return null; }
                    if (sl2 != sl)
                    {
                        sl = sl2; sc = sc2; ec = ec2;
                        text = Convert.ToString(cm.Lines(sl, 1)) ?? "";
                    }
                    else ec = ec2;
                }

                var s = new Snapshot { Line = sl, Text = text, Module = mod, CaretCol = ec };
                _snapCache = s; _snapAt = DateTime.UtcNow;
                RememberPane(cp);

                SyncMirror(cm, sl, mod, text);
                return s;
            }
            catch (Exception e) { Fail(e); return null; }
        }

        // 记下"这张快照属于哪个代码窗"。换模块就换了 CodePane 对象，据此可以跳过一次名字读取。
        private object _paneObj;

        private bool SamePane(object cp)
        {
            return _paneObj != null && ReferenceEquals(_paneObj, cp);
        }

        private void RememberPane(object cp)
        {
            // WPS 每轮结束都要 FinalReleaseComObject，多攥一个子对象会让它退不干净。
            if (ComCtx.IsWpsHost) { _paneObj = null; return; }
            if (!SamePane(cp)) _paneObj = cp;
        }

        // ---- 模块镜像 ----

        private static string Norm(string s) => (s ?? "").TrimEnd('\r', '\n');

        private static List<string> SplitBlock(string block, int n)
        {
            var parts = Parser.SplitLines(block);
            var list = new List<string>(n);
            for (int i = 0; i < n; i++)
                list.Add(i < parts.Length ? Norm(parts[i]) : "");
            return list;
        }

        // 光标行对上镜像：行数没变就只改这一行；行数变了在光标附近重新对齐；对不上就整体重读。
        private void SyncMirror(dynamic cm, int lineNo, string mod, string text)
        {
            int count;
            try { count = Convert.ToInt32(cm.CountOfLines); } catch { return; }

            if (_mirrorDirty || _scope == null || _lines.Count == 0 ||
                !string.Equals(mod, _mirrorName, StringComparison.Ordinal))
            {
                LoadMirror(cm, mod, count);
                return;
            }
            text = Norm(text);
            if (count == _lines.Count)
            {
                if (lineNo < 1 || lineNo > _lines.Count) return;
                if (string.Equals(_lines[lineNo - 1], text, StringComparison.Ordinal)) return;
                _lines[lineNo - 1] = text;
                _scope.Patch(lineNo, text);
                _modTextDirty = true;
                return;
            }
            SpliceMirror(cm, lineNo, count);
        }

        private void LoadMirror(dynamic cm, string mod, int count)
        {
            string all = count > 0 ? (Convert.ToString(cm.Lines(1, count)) ?? "") : "";
            _lines.Clear();
            _lines.AddRange(SplitBlock(all, count));
            _mirrorName = mod ?? "";
            _mirrorDirty = false;
            _modTextDirty = true;
            _scope = new ModuleScope(_mirrorName);
            _scope.LoadLines(_lines);
            _scope.ComponentType = _curType;
            _scope.SetControls(_curControls);
        }

        private void SpliceMirror(dynamic cm, int lineNo, int count)
        {
            int n = _lines.Count;
            int d = count - n;
            if (Math.Abs(d) > MirrorMaxShift || lineNo < 1)
            {
                LoadMirror(cm, _mirrorName, count);
                return;
            }
            int from = Math.Max(1, lineNo - MirrorWin);
            int to = Math.Min(count, lineNo + MirrorWin);
            if (to < from) { LoadMirror(cm, _mirrorName, count); return; }

            var fresh = SplitBlock(Convert.ToString(cm.Lines(from, to - from + 1)) ?? "", to - from + 1);
            int len = fresh.Count;

            int pre = 0;
            while (pre < len && from - 1 + pre < n &&
                   string.Equals(_lines[from - 1 + pre], fresh[pre], StringComparison.Ordinal))
                pre++;

            int suf = 0;
            while (suf < len - pre)
            {
                int ni = len - 1 - suf;
                int oi = to - 1 - suf - d;
                if (ni < pre || oi < 0 || oi >= n) break;
                if (!string.Equals(_lines[oi], fresh[ni], StringComparison.Ordinal)) break;
                suf++;
            }

            int oldStart = from - 1 + pre;                    // 0-based
            int oldLen = len - pre - suf - d;
            int newLen = len - pre - suf;
            // 窗口整段都能对上却没解释掉行数变化 = 改动在窗口外，别猜
            if (pre == len || oldLen < 0 || newLen < 0 || oldStart + oldLen > n ||
                (oldLen == 0 && newLen == 0))
            {
                LoadMirror(cm, _mirrorName, count);
                return;
            }

            var add = new List<string>(newLen);
            for (int i = 0; i < newLen; i++) add.Add(fresh[pre + i]);
            _lines.RemoveRange(oldStart, oldLen);
            _lines.InsertRange(oldStart, add);
            _scope.Splice(oldStart + 1, oldLen, add);
            _modTextDirty = true;
        }

        // 当前模块的作用域（增量维护好的，直接拿）。
        public ModuleScope CurrentScope()
        {
            if (_scope != null) return _scope;
            try
            {
                if (!TryCodePane(out dynamic _, out dynamic cm)) return null;
                string mod = "";
                try { mod = Convert.ToString(cm.Name) ?? ""; } catch { }
                int count = Convert.ToInt32(cm.CountOfLines);
                LoadMirror(cm, mod, count);
                return _scope;
            }
            catch (Exception e) { Fail(e); return null; }
        }

        // 当前模块全文（镜像拼出来，不给热路径用）。
        public string ModuleText()
        {
            if (_scope == null && CurrentScope() == null) return null;
            if (!_modTextDirty && _modText != null) return _modText;
            _modText = string.Join("\r\n", _lines);
            _modTextDirty = false;
            return _modText;
        }

        // 文本被我们改过之后必须调它，否则下一轮读到的是旧代码。
        public void OnWeWrote()
        {
            _mirrorDirty = true;      // 来路不明：下次快照整体重读
            _modText = null;
            _modTextDirty = true;
            InvalidateSnapshot();
            _scopesAt = DateTime.MinValue;
        }

        // 当前工程身份：Name|FileName，取不到返回空串。
        private static string ProjectKey()
        {
            try
            {
                dynamic vbe = ComCtx.Vbe();
                if (ReferenceEquals(vbe, null)) return "";
                dynamic proj = vbe.ActiveVBProject;
                if (ReferenceEquals(proj, null)) return "";
                string name = Convert.ToString(proj.Name) ?? "";
                string file = "";
                try { file = Convert.ToString(proj.FileName) ?? ""; } catch { }
                if (name.Length == 0 && file.Length == 0) return "";
                return name + "|" + file;
            }
            catch { return ""; }
        }

        // 800ms 内复用上次的工程身份；换模块（多半就是换工程）立刻重问。
        private string ProjectKeyCached()
        {
            string mod = _mirrorName ?? "";
            if (_pkeyCache != null &&
                string.Equals(mod, _pkeyModule, StringComparison.Ordinal) &&
                (DateTime.UtcNow - _pkeyAt).TotalMilliseconds < 800)
                return _pkeyCache;

            string k = ProjectKey();
            if (!string.IsNullOrEmpty(k))
            {
                _pkeyCache = k;
                _pkeyModule = mod;
                _pkeyAt = DateTime.UtcNow;
            }
            return k;
        }

        // 读：全工程作用域
        // 全工程每个模块的作用域分析（别的模块的 Public 也要能提示）。
        public List<ModuleScope> ProjectScopes(out int signature)
        {
            // 两个 .xlsm 的工程名、组件名都可能重名，只按模块名做缓存键会串工程，换工程就整体作废。
            string pkey = ProjectKeyCached();
            if (!string.IsNullOrEmpty(pkey) && !string.Equals(pkey, _projectKey, StringComparison.Ordinal))
            {
                _modScopes.Clear();
                _scopesList = new List<ModuleScope>();
                _scopesSig = int.MinValue;
                _scopesAt = DateTime.MinValue;
                // 换工程 = 换模块内容：镜像整体重读（作用域跟着重建）
                _mirrorDirty = true;
                _projectKey = pkey;
            }

            if (_scopesSig != int.MinValue &&
                (DateTime.UtcNow - _scopesAt).TotalSeconds < _scopesTtl)
            {
                signature = _scopesSig;
                return _scopesList;
            }

            // 打字中别的模块不可能变，先用旧列表顶着（最多 _scopesTypingTtl 秒），免得重算把候选窗推迟 50ms。
            if (_scopesSig != int.MinValue &&
                (DateTime.UtcNow - _scopesAt).TotalSeconds < _scopesTypingTtl &&
                App.LastTypingTs > 0 &&
                Environment.TickCount64 - App.LastTypingTs < 400)
            {
                signature = _scopesSig;
                return _scopesList;
            }

            signature = _scopesSig;
            var list = new List<ModuleScope>();
            int sig = 17;
            try
            {
                dynamic vbe = ComCtx.Vbe();
                if (ReferenceEquals(vbe, null)) return _scopesList;
                dynamic proj = vbe.ActiveVBProject;
                if (ReferenceEquals(proj, null)) return _scopesList;
                dynamic comps = proj.VBComponents;
                int n = Convert.ToInt32(comps.Count);

                var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i <= n; i++)
                {
                    try
                    {
                        dynamic comp = comps.Item(i);
                        string nm = Convert.ToString(comp.Name) ?? "";
                        if (nm.Length == 0) continue;
                        alive.Add(nm);

                        // 组件类型：1=标准模块 2=类模块 3=窗体 100=文档模块；读不出来当未知处理。
                        int ctype = 0;
                        try { ctype = Convert.ToInt32(comp.Type); } catch { }

                        // 窗体控件名只存在于设计器数据里，窗体代码模块通常一个字都没有。
                        List<string> ctrls = null;
                        if (ctype == MSForm) ctrls = FormControls(comp, nm);

                        // 当前模块由镜像增量维护，这里不读它的全文、也不重析（省下一次整模块扫描）。
                        if (!string.IsNullOrEmpty(_mirrorName) &&
                            string.Equals(nm, _mirrorName, StringComparison.OrdinalIgnoreCase))
                        {
                            _curType = ctype;
                            if (ctrls != null) _curControls = ctrls;
                            if (_scope != null)
                            {
                                _scope.ComponentType = ctype;
                                _scope.SetControls(ctrls);
                            }
                            continue;
                        }

                        dynamic cm = comp.CodeModule;
                        int count = Convert.ToInt32(cm.CountOfLines);
                        sig = unchecked(sig * 31 + nm.ToLowerInvariant().GetHashCode() * 17 + count);
                        // 控件名也算进签名：拖上新控件时模块文本没变，否则池子会一直用旧的。
                        if (ctrls != null)
                            foreach (var cn in ctrls)
                                sig = unchecked(sig * 31 + cn.ToLowerInvariant().GetHashCode());

                        ModuleScope ms;
                        if (_modScopes.TryGetValue(nm, out ms) && ms.LineCount == count)
                        {
                            ms.ComponentType = ctype;   // 缓存命中也要刷新类型
                            ms.SetControls(ctrls);
                            list.Add(ms);
                            continue;
                        }
                        string text = count > 0 ? Convert.ToString(cm.Lines(1, count)) : "";
                        ms = new ModuleScope(nm);
                        ms.Load(text);
                        ms.Trim();
                        ms.ComponentType = ctype;
                        ms.SetControls(ctrls);
                        _modScopes[nm] = ms;
                        list.Add(ms);
                    }
                    catch { }   // 单个组件读不出来（被锁定 / 奇怪类型）不影响其它
                }

                if (_modScopes.Count > alive.Count)
                {
                    var dead = new List<string>();
                    foreach (var k in _modScopes.Keys) if (!alive.Contains(k)) dead.Add(k);
                    foreach (var k in dead) _modScopes.Remove(k);
                }

                _scopesList = list;
                _scopesSig = sig;
                _scopesAt = DateTime.UtcNow;
                signature = sig;
            }
            catch (Exception e) { Fail(e); }
            return list;
        }

        // Designer.Controls 已是扁平全集（别递归）；取不到就返回空，绝不带崩整个作用域收集。
        private List<string> FormControls(object comp, string moduleName)
        {
            KeyValuePair<List<string>, DateTime> hit;
            if (_formCtrls.TryGetValue(moduleName, out hit) &&
                (DateTime.UtcNow - hit.Value).TotalSeconds < FormCtrlTtl)
                return hit.Key;

            var list = new List<string>();
            try
            {
                dynamic c = comp;
                dynamic designer = c.Designer;
                if (!ReferenceEquals(designer, null))
                {
                    dynamic cs = designer.Controls;
                    int n = Convert.ToInt32(cs.Count);
                    if (n > FormCtrlLimit) n = FormCtrlLimit;
                    // MSForms 的 Controls 集合是 0-based（Item(index)）
                    for (int k = 0; k < n; k++)
                    {
                        try
                        {
                            string nm = Convert.ToString(cs.Item(k).Name) ?? "";
                            if (nm.Length > 0) list.Add(nm);
                        }
                        catch { }   // 单个控件读不出来（奇怪类型）不影响其它
                    }
                }
            }
            catch { }   // 运行态 / WPS 无 Designer：空列表照样缓存，5 秒后重试

            _formCtrls[moduleName] = new KeyValuePair<List<string>, DateTime>(list, DateTime.UtcNow);
            return list;
        }

        // 我们自己写回的行也要喂给镜像，否则下一轮分析读到的是旧代码。
        private void MirrorLine(int lineNo, string text)
        {
            _snapCache = null;
            _modTextDirty = true;
            if (_scope == null || lineNo < 1 || lineNo > _lines.Count) { _mirrorDirty = true; return; }
            text = Norm(text);
            _lines[lineNo - 1] = text;
            _scope.Patch(lineNo, text);
        }

        private void MirrorDelete(int from, int count)
        {
            _snapCache = null;
            _modTextDirty = true;
            if (_scope == null || from < 1 || count < 1 || from + count - 1 > _lines.Count)
            {
                _mirrorDirty = true;
                return;
            }
            _lines.RemoveRange(from - 1, count);
            _scope.Splice(from, count, Array.Empty<string>());
        }

        private void MirrorInsert(int at, string text)
        {
            _snapCache = null;
            _modTextDirty = true;
            if (_scope == null || at < 1 || at > _lines.Count + 1) { _mirrorDirty = true; return; }
            var add = new List<string>();
            var parts = Parser.SplitLines(text ?? "");
            int n = parts.Length;
            if (n > 1 && parts[n - 1].Length == 0) n--;   // 末尾换行切出来的空元素
            for (int i = 0; i < n; i++) add.Add(Norm(parts[i]));
            if (add.Count == 0) add.Add("");
            _lines.InsertRange(at - 1, add);
            _scope.Splice(at, 0, add);
        }

        // 把 [wordStartCol, endCol) 换成 completion（都是 1-based 字符列）。
        public bool ApplyCompletion(int lineNo, int wordStartCol, int endCol, string completion)
        {
            try
            {
                dynamic vbe = ComCtx.Vbe();
                if (ReferenceEquals(vbe, null)) return false;
                dynamic cp = vbe.ActiveCodePane;
                if (ReferenceEquals(cp, null)) return false;
                dynamic cm = cp.CodeModule;

                string lineText = Convert.ToString(cm.Lines(lineNo, 1)) ?? "";
                string newLine = Rules.ReplaceWord(lineText, wordStartCol, endCol, completion);

                int start0 = Math.Max(0, wordStartCol - 1);
                int end0 = start0 + completion.Length;

                cm.ReplaceLine(lineNo, newLine);

                // ReplaceLine 会重排整行，落光标的位置按读回来的真行算。
                string actual = Convert.ToString(cm.Lines(lineNo, 1)) ?? "";
                MirrorLine(lineNo, actual);
                if (actual != newLine)
                    end0 = Rules.FindCompletionEnd(actual, completion, end0 - completion.Length, end0);
                if (end0 > actual.Length) end0 = actual.Length;

                int caretCol = Rules.DispCol(actual, end0, 4);
                if (caretCol < 0) caretCol = 0;

                cp.SetSelection(lineNo, 1, lineNo, 1);
                cp.SetSelection(lineNo, caretCol, lineNo, caretCol);
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        // 写：行操作
        public bool ReplaceLineRaw(int lineNo, string text)
        {
            if (!TryCodePane(out _, out dynamic cm)) return false;
            try
            {
                cm.ReplaceLine(lineNo, text);
                MirrorLine(lineNo, text);
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        public bool DeleteLines(int from, int count)
        {
            if (!TryCodePane(out _, out dynamic cm)) return false;
            try
            {
                cm.DeleteLines(from, count);
                MirrorDelete(from, count);
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        public bool InsertLines(int at, string text)
        {
            if (!TryCodePane(out _, out dynamic cm)) return false;
            try
            {
                cm.InsertLines(at, text);
                MirrorInsert(at, text);
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        public int LineCount()
        {
            // VBE 关着时 ActiveCodePane 是 null，那是"没有代码窗"不是故障，安静返回 -1。
            if (!TryCodePane(out _, out dynamic cm)) return -1;
            try { return Convert.ToInt32(cm.CountOfLines); }
            catch (Exception e) { Fail(e); return -1; }
        }

        public string LineAt(int lineNo)
        {
            if (!TryCodePane(out _, out dynamic cm)) return null;
            try { return Convert.ToString(cm.Lines(lineNo, 1)) ?? ""; }
            catch (Exception e) { Fail(e); return null; }
        }

        // 一次读回连续 count 行：逐行读是 count 次跨进程往返，太贵。读失败返回 null。
        public string LinesBlock(int from, int count)
        {
            if (from < 1 || count < 1) return null;
            if (!TryCodePane(out _, out dynamic cm)) return null;
            try { return Convert.ToString(cm.Lines(from, count)); }
            catch (Exception e) { Fail(e); return null; }
        }

        // 控件 ID 与 VBE 语言版本无关（中英文都是 192 / 2552），绝不按 Caption 找；FindControl 要缓存。
        public const int CmdCommentBlock = 192;
        public const int CmdUncommentBlock = 2552;
        private const int MsoControlButton = 1;

        private object _cmdComment;     // 缓存住的 CommandBarControl（只在 UI 线程用）
        private object _cmdUncomment;

        // 直接执行 VBE 自己的命令。返回 false 时调用方必须回退到手写路径。
        public bool RunVbeCommand(int id)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    object ctl = id == CmdCommentBlock ? _cmdComment : _cmdUncomment;
                    if (ctl == null)
                    {
                        dynamic vbe = ComCtx.Vbe();
                        if (ReferenceEquals(vbe, null)) return false;
                        dynamic bars = vbe.CommandBars;
                        if (ReferenceEquals(bars, null)) return false;
                        ctl = bars.FindControl(MsoControlButton, id);
                        if (ReferenceEquals(ctl, null)) return false;
                        if (id == CmdCommentBlock) _cmdComment = ctl; else _cmdUncomment = ctl;
                    }

                    dynamic d = ctl;
                    if (!d.Enabled)
                    {
                        // 控件存在但当前上下文不让用（VBE 不在前台等）。缓存别丢，交给调用方回退手写。
                        return false;
                    }
                    d.Execute();
                    return true;
                }
                catch (Exception e)
                {
                    // 多半是 VBE 关掉又打开、缓存的 IDispatch 已失效：清缓存重试一次。
                    DropCmdCache(id);
                    if (attempt == 1) { Fail(e); return false; }
                }
            }
            return false;
        }

        private void DropCmdCache(int id)
        {
            if (id == CmdCommentBlock) _cmdComment = null;
            else if (id == CmdUncommentBlock) _cmdUncomment = null;
        }

        // 这种行得先用 InsertLines 真的建出来。
        public bool WriteLineRaw(int lineNo, string text)
        {
            int n = LineCount();
            if (n >= 0 && lineNo > n)
            {
                if (lineNo != n + 1) return false;   // 差得离谱，不猜
                return InsertLines(lineNo, text ?? "");
            }
            return ReplaceLineRaw(lineNo, text);
        }

        // 本模块第 lineNo 行【上方】window 行（越靠近它的越靠后）。
        private List<string> LinesAbove(int lineNo, int window)
        {
            var list = new List<string>();
            int last = Math.Min(lineNo - 2, _lines.Count - 1);
            int first = Math.Max(0, last - window + 1);
            for (int i = first; i <= last; i++) list.Add(_lines[i]);
            return list;
        }

        // 本模块第 lineNo 行【下方】window 行（越靠近它的越靠前）。
        private List<string> LinesBelow(int lineNo, int window)
        {
            var list = new List<string>();
            int first = Math.Max(0, lineNo - 1);
            int last = Math.Min(_lines.Count - 1, first + window - 1);
            for (int i = first; i <= last; i++) list.Add(_lines[i]);
            return list;
        }

        // 回车只在光标附近看上下文，缩进推断本来也不会往上翻几百行。
        private const int CtxWindow = 400;

        private bool EnsureMirror()
        {
            if (_scope != null && !_mirrorDirty) return true;
            try
            {
                if (!TryCodePane(out _, out dynamic cm)) return false;
                string mod = "";
                try { mod = Convert.ToString(cm.Name) ?? ""; } catch { }
                LoadMirror(cm, mod, Convert.ToInt32(cm.CountOfLines));
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        // Shift+Enter（smart=false）/ 回车（smart=true）：在光标行下方新起一行，本行不动。
        public bool NewLineBelow(bool smart, bool autoClose, bool alignBranch)
        {
            try
            {
                int sl, sc, el, ec;
                if (!TryGetSelection(out sl, out sc, out el, out ec)) return false;
                if (smart && (sl != el || sc != ec)) return false;      // 有选区：不接管
                int anchor = smart ? sl : Math.Max(sl, el);
                if (anchor < 1) return false;

                // anchor 可能超过 CountOfLines（模块末尾的虚拟空行），夹回最后一行。
                int lineCount = LineCount();
                if (lineCount < 0) return false;
                if (anchor > lineCount) anchor = Math.Max(1, lineCount);

                if (!EnsureMirror()) return false;

                string lineText = LineAt(anchor);
                if (lineText == null) lineText = "";
                lineText = Norm(lineText);
                if (anchor <= _lines.Count && !string.Equals(_lines[anchor - 1], lineText, StringComparison.Ordinal))
                    MirrorLine(anchor, lineText);

                // 钩子那边可能因快照落后而"先接管了再说"，这里必须用真 COM 数据再判一次。
                if (smart && !Editing.EnterIndentWanted(lineText, ec)) return false;

                string closerText = null;
                string fixedLine = null;
                string indent;

                var above = LinesAbove(anchor, CtxWindow);
                var forUnit = new List<string>(above);
                forUnit.Add(lineText);
                string unit = Editing.IndentUnitFor(forUnit);

                if (smart)
                {
                    if (lineText.Trim().Length == 0) return false;      // 空行：交给 VBE
                    if (Editing.UnterminatedString(lineText)) return false;  // 让 VBE 补右引号
                    int caretChar = Rules.CharColFromDisp(lineText, ec);
                    if (caretChar - 1 < lineText.Length &&
                        lineText.Substring(caretChar - 1).Trim().Length > 0)
                        return false;                                   // 光标后还有内容 = 拆行

                    indent = Editing.NextLineIndent(lineText, above, unit);
                    if (alignBranch)
                    {
                        string mine, next;
                        if (Editing.BranchAlign(lineText, above, unit, out mine, out next))
                        {
                            fixedLine = mine;
                            indent = next;
                        }
                    }
                    if (autoClose) closerText = CloserFor(lineText, above, anchor);
                }
                else
                {
                    if (lineText.Trim().Length == 0)
                    {
                        // 空行：不替用户猜层级，照抄本行空白（= VBE 原生的结果）
                        indent = Editing.LeadingWs(lineText);
                    }
                    else
                    {
                        indent = Editing.NextLineIndent(lineText, above, unit);
                        if (alignBranch)
                        {
                            string mine, next;
                            if (Editing.BranchAlign(lineText, above, unit, out mine, out next))
                            {
                                fixedLine = mine;
                                indent = next;
                            }
                        }
                    }
                    if (autoClose) closerText = CloserFor(lineText, above, anchor);
                }

                // 分支行先拉回它该在的层级：只动行首空白，行内一个字符都不碰
                if (fixedLine != null)
                {
                    string ws = Editing.LeadingWs(lineText);
                    if (ws != fixedLine)
                    {
                        string body = lineText.Substring(ws.Length);
                        if (!WriteLineRaw(anchor, fixedLine + body)) return false;
                    }
                }

                if (!InsertLines(anchor + 1, indent)) return false;
                if (closerText != null)
                {
                    if (!InsertLines(anchor + 2, closerText))
                    {
                        // 收尾插失败：把刚插的空行收掉，回到"什么都没发生"
                        try { DeleteLines(anchor + 1, 1); } catch { }
                        return false;
                    }
                }

                // 新行已插入，之后不能再返回 false（调用方会再补一个回车），所以 SetCaret 单独 try。
                try { SetCaret(anchor + 1, indent.Length + 1); }
                catch { }
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        // 块头该补的收尾（含缩进）；不该补返回 null。
        private string CloserFor(string lineText, List<string> above, int anchor)
        {
            string closer = Editing.BlockCloser(lineText);
            if (closer == null) return null;
            string baseWs = Editing.LeadingWs(lineText);
            var below = LinesBelow(anchor, CtxWindow);
            // 必须把 kind 与上方各行一起传：要靠 kind 分辨"下面那个同类收尾是不是本块的"。
            if (!Editing.CloserNeeded(below, closer, Editing.IndentWidth(baseWs),
                    Editing.BlockKind(lineText), above)) return null;
            return baseWs + closer;
        }

        // 退格删整行：光标所在行只有空白时一次删掉，光标落到上一行行尾（与 VBE 原生一致）。
        public bool DeleteBlankLineHere()
        {
            try
            {
                int sl, sc, el, ec;
                if (!TryGetSelection(out sl, out sc, out el, out ec)) return false;
                if (sl != el || sc != ec) return false;         // 有选区：那是"删选区"

                int count = LineCount();
                if (count <= 0 || sl < 1 || sl > count) return false;

                string raw = LineAt(sl) ?? "";
                // 权威复核：喂进去的是此刻真读到的行与列（快照可能已过期）。
                if (!Editing.BlankLineDeleteWanted(raw, ec)) return false;

                if (!DeleteLines(sl, 1)) return false;

                try
                {
                    if (sl <= 1) SetCaret(1, 1);
                    else CaretToLineEnd(sl - 1);
                }
                catch { }
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        // 退格删一对：光标夹在一对空括号 / 空引号中间时把两端都删掉。
        public bool DeletePairHere()
        {
            try
            {
                int sl, sc, el, ec;
                if (!TryGetSelection(out sl, out sc, out el, out ec)) return false;
                if (sl != el || sc != ec) return false;         // 有选区：那是"删选区"

                string raw = LineAt(sl);
                if (raw == null) return false;

                int left, right;
                if (!Editing.PairDeleteSpan(raw, ec, out left, out right)) return false;

                string newLine = raw.Remove(left, right - left + 1);
                if (!WriteLineRaw(sl, newLine)) return false;

                string actual = LineAt(sl) ?? newLine;
                int caretOff = left;
                if (!string.Equals(actual, newLine, StringComparison.Ordinal))
                {
                    int off = LocateByPrefix(actual, newLine, left);
                    if (off >= 0) caretOff = off;
                }
                caretOff = Math.Max(0, Math.Min(caretOff, actual.Length));
                try { SetCaret(sl, caretOff + 1); }
                catch { }
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        // 在 VBE 重排过的行里按"压平前缀"当指纹找回落点，找不到返回 -1。
        private static int LocateByPrefix(string actual, string written, int off0)
        {
            if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(written) || off0 < 0) return -1;
            if (off0 > written.Length) off0 = written.Length;
            string want = NormCode(written.Substring(0, off0));
            if (want.Length == 0) return -1;

            int i = 0;
            for (; i < actual.Length && want.Length > 0; i++)
            {
                char c = char.ToLowerInvariant(actual[i]);
                if (c == ' ' || c == '\t') continue;
                if (c != want[0]) return -1;
                want = want.Substring(1);
            }
            return want.Length == 0 ? i : -1;
        }

        private static string NormCode(string s)
        {
            var sb = new StringBuilder(s == null ? 0 : s.Length);
            foreach (char c in s ?? "")
            {
                if (c == ' ' || c == '\t') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // 这一行是不是 `Call foo` 形式（VBE 会把空括号删掉）。
        public static bool InCallStatement(string line)
        {
            if (line == null) return false;
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i + 4 > line.Length) return false;
            if (string.Compare(line, i, "Call", 0, 4, StringComparison.OrdinalIgnoreCase) != 0) return false;
            return i + 4 < line.Length && (line[i + 4] == ' ' || line[i + 4] == '\t');
        }

        // 自动配对：敲 ( / " 时补出右半边，光标落在中间。
        public bool InsertPair(char openCh)
        {
            char closeCh = Editing.CloseOf(openCh);
            if (closeCh == '\0') return false;

            bool wrote = false;
            try
            {
                int sl, sc, el, ec;
                if (!TryGetSelection(out sl, out sc, out el, out ec)) return false;

                if (sl != el || sc != ec)
                {
                    bool wrap = openCh == '"' || openCh == '“' || openCh == '(' || openCh == '（';
                    if (sl == el && sc < ec && wrap)
                        return WrapSelection(sl, sc, ec, openCh, closeCh);
                    return false;     // 其余选区：替换语义交给 VBE，别插
                }

                string lineText = LineAt(sl);
                if (lineText == null) return false;

                int charCol = Rules.CharColFromDisp(lineText, ec);
                // 只挡"注释里"，不挡"字符串里"：未闭合字符串末尾的引号正是收尾那一半。
                if (Rules.InCommentOnly(lineText, charCol)) return false;
                if (InCallStatement(lineText)) return false;   // `Call foo` 里的空括号 VBE 会删掉

                int col0 = Math.Max(0, Math.Min(charCol - 1, lineText.Length));
                string newLine;
                int caretOff;
                bool paired;

                if (openCh == ')' || openCh == '）')
                {
                    // 右半边已经在光标右边 -> 只跨过去；否则放行，让 VBE 真插一个
                    if (col0 >= lineText.Length || lineText[col0] != openCh) return false;
                    newLine = null;
                    caretOff = col0 + 1;
                    paired = false;
                }
                else
                {
                    newLine = Editing.PairInsertion(lineText, col0, openCh, closeCh,
                        out caretOff, out paired);
                }

                if (newLine != null)
                {
                    if (!WriteLineRaw(sl, newLine)) return false;
                    wrote = true;
                }

                string actual = newLine == null ? lineText : (LineAt(sl) ?? newLine);
                if (newLine != null && !string.Equals(actual, newLine, StringComparison.Ordinal))
                {
                    int off = paired
                        ? LocateInserted(actual, newLine, openCh, closeCh, col0)
                        : LocateByPrefix(actual, newLine, col0 + 1);
                    if (off >= 0) caretOff = off;
                    else caretOff = Math.Min(caretOff, actual.Length);
                }
                caretOff = Math.Max(0, Math.Min(caretOff, actual.Length));

                // 文本已经写进去了 —— 落光标失败不能把结果拉成 false，否则调用方
                // 会再注入一个开符号，行里就多出一个 `(`
                try { SetCaret(sl, caretOff + 1); }
                catch { }
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
            finally
            {
                if (wrote) OnWeWrote();
            }
        }

        // 在重排过的行里找 openCh+closeCh，取离 col0 最近的那一处。
        private static int LocateInserted(string actual, string written,
            char openCh, char closeCh, int col0)
        {
            if (string.IsNullOrEmpty(actual)) return -1;
            string pair = new string(new[] { openCh, closeCh });
            int best = -1;
            int i = 0;
            while (i <= actual.Length - pair.Length)
            {
                int hit = actual.IndexOf(pair, i, StringComparison.Ordinal);
                if (hit < 0) break;
                if (best < 0 || Math.Abs(hit - col0) < Math.Abs(best - col0)) best = hit;
                i = hit + 1;
            }
            if (best >= 0) return best + openCh.ToString().Length;
            return LocateByPrefix(actual, written, col0 + 1);
        }

        // 单行选区用一对符号包起来（abc -> "abc" / (abc)）。
        private bool WrapSelection(int sl, int sc, int ec, char openCh, char closeCh)
        {
            try
            {
                string lineText = LineAt(sl);
                if (lineText == null) return false;

                int a = Math.Max(0, Math.Min(Rules.CharColFromDisp(lineText, sc) - 1, lineText.Length));
                int b = Math.Max(a, Math.Min(Rules.CharColFromDisp(lineText, ec) - 1, lineText.Length));
                if (b <= a) return false;        // 实际没选中字符：交给 VBE

                string newLine = lineText.Substring(0, a) + openCh +
                                 lineText.Substring(a, b - a) + closeCh +
                                 lineText.Substring(b);
                if (!WriteLineRaw(sl, newLine)) return false;

                string actual = LineAt(sl) ?? newLine;
                int tgt = a + 1 + (b - a) + 1;
                if (!string.Equals(actual, newLine, StringComparison.Ordinal))
                {
                    int off = LocateByPrefix(actual, newLine, tgt);
                    if (off >= 0) tgt = off;
                }
                tgt = Math.Max(0, Math.Min(tgt, actual.Length));
                try { SetCaret(sl, tgt + 1); }
                catch { }
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        // 把代码窗光标上/下移 delta 行，尽量保持同一列。不能让 VBE 自己动：Shift+↑/↓ 是扩选。
        public bool MoveCaretLine(int delta)
        {
            try
            {
                int sl, sc, el, ec;
                if (!TryGetSelection(out sl, out sc, out el, out ec)) return false;
                int line, col;
                if (el >= sl) { line = el; col = ec; } else { line = sl; col = sc; }
                int target = line + delta;
                int count = LineCount();
                if (line < 1 || target < 1 || count < 0 || target > count) return false;

                // 先把列钳回目标行的行尾：SetSelection 给超长列会直接抛 COM 异常。
                string tgt = LineAt(target);
                if (tgt != null)
                {
                    int eol = Rules.DispCol(tgt, tgt.Length);
                    if (col > eol) col = eol;
                }
                if (col < 1) col = 1;

                if (!TryCodePane(out dynamic cp, out _)) return false;
                cp.SetSelection(target, col, target, col);
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        // 把一段（可能多行的）片段插到光标处；有选区是替换语义。
        public bool InsertSnippet(string text)
        {
            text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            if (text.Trim().Length == 0) return false;

            int sl = 0, k = 0;
            try
            {
                int sc, el, ec;
                if (!TryGetSelection(out sl, out sc, out el, out ec)) return false;
                if (sl < 1 || el < sl) return false;

                int count = LineCount();
                if (count < 0) return false;

                string first = sl > count ? "" : (LineAt(sl) ?? "");
                string last = sl == el ? first : (el > count ? "" : (LineAt(el) ?? ""));

                int i = Math.Max(0, Math.Min(Rules.CharColFromDisp(first, sc) - 1, first.Length));
                int colEnd = Rules.CharColFromDisp(last, ec);
                int j = sl == el
                    ? Math.Max(i, Math.Min(colEnd - 1, last.Length))
                    : Math.Max(0, Math.Min(colEnd - 1, last.Length));

                string head = first.Substring(0, i);
                string tail = last.Substring(j);

                var block = text.Split('\n');
                var output = new List<string>();
                if (block.Length == 1)
                {
                    output.Add(head + block[0] + tail);
                }
                else
                {
                    output.Add(head + block[0]);
                    for (int x = 1; x < block.Length - 1; x++) output.Add(block[x]);
                    output.Add(block[block.Length - 1] + tail);
                }
                k = output.Count - 1;

                // 光标可能停在模块末尾的虚拟空行：那一行还不存在，先建成真的空行
                bool virtualHead = sl > count && k > 0;
                if (virtualHead)
                {
                    if (!InsertLines(sl, "")) return false;
                }

                if (k > 0)
                {
                    var mid = new StringBuilder();
                    for (int x = 1; x < output.Count; x++)
                    {
                        if (x > 1) mid.Append("\r\n");
                        mid.Append(output[x]);
                    }
                    if (!InsertLines(sl + 1, mid.ToString()))
                    {
                        if (virtualHead) { try { DeleteLines(sl, 1); } catch { } }

                        return false;
                    }
                }

                if (!WriteLineRaw(sl, output[0]))
                {
                    try
                    {
                        if (virtualHead) DeleteLines(sl, k + 1);
                        else if (k > 0) DeleteLines(sl + 1, k);
                    }
                    catch { }
                    return false;
                }

                // 多行选区：把被替换掉的旧行收掉（插进来的 k 行把它们挤下去了）
                int drop = el - sl;
                if (drop > 0)
                {
                    try { DeleteLines(sl + 1 + k, drop); }
                    catch { }
                }

                OnWeWrote();

                int lineNo = sl + k;
                string actual = LineAt(lineNo) ?? output[output.Count - 1];
                int pos = TailPos(actual, tail);
                try { SetCaret(lineNo, pos + 1); }
                catch { }

                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        private static int TailPos(string actual, string tail)
        {
            if (string.IsNullOrEmpty(tail)) return actual == null ? 0 : actual.Length;
            if (string.IsNullOrEmpty(actual)) return 0;
            int p = actual.LastIndexOf(tail, StringComparison.Ordinal);
            return p >= 0 ? p : actual.Length;
        }

        public bool CaretToLineEnd(int lineNo)
        {
            if (!TryCodePane(out dynamic cp, out dynamic cm)) return false;
            try
            {
                int count = Convert.ToInt32(cm.CountOfLines);
                if (lineNo < 1 || lineNo > count) return false;
                string text = Convert.ToString(cm.Lines(lineNo, 1)) ?? "";
                int col = Rules.DispCol(text.TrimEnd('\r', '\n'), text.TrimEnd('\r', '\n').Length, 4);
                cp.SetSelection(lineNo, 1, lineNo, 1);
                cp.SetSelection(lineNo, col, lineNo, col);
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        // 把光标放到第 charCol 个字符之前（1-based 字符列）。参数是"第几个字符"而非"跳过几个"，否则会右偏一格。
        public bool SetCaret(int lineNo, int charCol)
        {
            if (!TryCodePane(out dynamic cp, out dynamic cm)) return false;
            try
            {
                string text = Convert.ToString(cm.Lines(lineNo, 1)) ?? "";
                int col = Rules.CaretColForCharCol(text, charCol, 4);
                cp.SetSelection(lineNo, 1, lineNo, 1);
                cp.SetSelection(lineNo, col, lineNo, col);
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        // 设一个跨行选区（1-based 行 / VBE 显示列），批量注释走这条。
        public bool SetSelectionRange(int sl, int sc, int el, int ec)
        {
            try
            {
                if (!TryCodePane(out dynamic cp, out _)) return false;
                cp.SetSelection(sl, sc, el, ec);
                InvalidateSnapshot();
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        // 当前选区（四个 Long，都是 ByRef 出参，只能走 IDispatch 直调）。
        public bool TryGetSelection(out int sl, out int sc, out int el, out int ec)
        {
            sl = sc = el = ec = 0;
            try
            {
                if (!TryCodePane(out dynamic cp, out _)) return false;
                return ComCall.CallFourOut((object)cp, "GetSelection", out sl, out sc, out el, out ec);
            }
            catch (Exception e) { Fail(e); return false; }
        }

        public bool FocusVbe()
        {
            try
            {
                dynamic vbe = ComCtx.Vbe();
                if (!ReferenceEquals(vbe, null))
                {
                    try { vbe.MainWindow.SetFocus(); return true; } catch { }
                }
            }
            catch (Exception e) { Fail(e); }

            // COM 走不通时的兜底：直接按 VBE 主框架的类名把前台抢回来
            try
            {
                IntPtr frame = Native.FindWindow("wndclass_desked_gsk", null);
                if (frame != IntPtr.Zero) return Native.SetForegroundWindow(frame);
            }
            catch { }
            return false;
        }
    }
}

// 作用域分析：把名字分成三档，4 = 本过程、3 = 本模块、2 = 别的模块 Public。
// 增量式：改一行只重算那一行（或那条跨行语句），整模块全量扫只发生在首次加载 / 换工程。

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace VbeComplete
{
    // 模块级名字的记账：能不能导出到别的模块、图标画什么。
    internal sealed class NameRec
    {
        public bool Public;
        public string Kind;
    }

    internal sealed class Decl
    {
        public string Name;
        public bool IsPrivate;    // 只对模块级有意义（没写 Public 就是私有）
        public string KindHint;   // sub / function / property / type / enum
        public bool ForceModule;  // 写在过程首行、但归属模块的声明（过程名 / 类型名）
    }

    // 一行贡献了什么：结构事件、语句级声明（挂语句首行）、本物理行的自由标识符。
    internal sealed class LineInfo
    {
        public const byte None = 0, ProcHead = 1, EndProc = 2, TypeHead = 3, EndType = 4;
        public byte Kind;
        public string Name;
        public string ProcKind;
        public bool IsPrivate;
        public bool InlineEnd;        // Sub A(): x = 1: End Sub
        public List<Decl> Decls;
        public string[] Implicit;
    }

    // Type / Enum 块（含首尾行，1-based）
    internal struct TypeSpan
    {
        public int From, To;
        public string Owner;
        public bool IsEnum, IsPrivate;
    }

    internal sealed class ModuleScope
    {
        // 单个过程超过这么多行时，只看光标附近，不让一次按键扫十万行。
        private const int ProcScanMax = 8000;
        private const int ProcScanHalf = 4000;

        public string Module;
        public int ComponentType;
        public List<string> Controls = new List<string>();
        public int LineCount => _lines.Count;

        // 内容版本：每改一行 +1，Engine 用它判断池子要不要重算。
        public int Revision { get; private set; }

        // 模块级名字版本：只有过程外的行改动才 +1，Engine 用它缓存"本模块 + 外部模块"那部分池子。
        public int ModuleRev => _modRev;

        private readonly List<string> _lines = new List<string>();
        private readonly List<LineInfo> _info = new List<LineInfo>();
        private List<ProcInfo> _procs = new List<ProcInfo>();
        private List<TypeSpan> _types = new List<TypeSpan>();

        // 同一个名字到处重复出现，共享一份实例，省掉大半字符串分配。
        private readonly Dictionary<string, string> _intern =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 模块级名字：名字 -> 记账（能否导出、图标种类），按 _modRev 缓存
        private readonly Dictionary<string, NameRec> _modRecs =
            new Dictionary<string, NameRec>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _modOrder = new List<string>();
        private Dictionary<string, int> _modNames;
        private int _modRev = 1;
        private int _modNamesRev = -1;
        private List<string> _exported;
        private int _exportedRev = -1;

        // 每个过程的名字集合，按过程版本号缓存
        private sealed class ProcCacheEntry
        {
            public int Rev;
            public Dictionary<string, int> Names;
        }

        private readonly Dictionary<string, ProcCacheEntry> _procCache =
            new Dictionary<string, ProcCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _procRev =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, int> _vis;
        private string _visProc;
        private long _visKey = -1;

        public ModuleScope(string module)
        {
            Module = module ?? "";
        }

        public IReadOnlyList<ProcInfo> Procs => _procs;
        public IReadOnlyList<TypeSpan> Types => _types;

        // 调试 / 自检用
        public string LineText(int ln) => ln >= 1 && ln <= _lines.Count ? _lines[ln - 1] : null;
        public byte LineKind(int ln) =>
            ln >= 1 && ln <= _info.Count && _info[ln - 1] != null ? _info[ln - 1].Kind : (byte)0;
        public string[] LineImplicit(int ln) =>
            ln >= 1 && ln <= _info.Count && _info[ln - 1] != null ? _info[ln - 1].Implicit : null;
        public List<Decl> LineDecls(int ln) =>
            ln >= 1 && ln <= _info.Count && _info[ln - 1] != null ? _info[ln - 1].Decls : null;

        // ---- 加载 ----

        public void Load(string text)
        {
            LoadLines(Parser.SplitLines(text));
        }

        public void LoadLines(IList<string> lines)
        {
            _lines.Clear();
            _info.Clear();
            for (int i = 0; i < lines.Count; i++)
            {
                _lines.Add(lines[i] ?? "");
                _info.Add(null);
            }
            for (int i = 1; i <= _lines.Count; i++) Classify(i);
            RebuildStructure();
            for (int i = 1; i <= _lines.Count; i++) AnalyzeImplicit(i);
            int head = 1;
            while (head <= _lines.Count)
            {
                AnalyzeDecls(head);
                head = StatementEnd(head) + 1;
            }
            _visKey = -1;
            Revision++;
        }

        // 别的模块只要"能导出什么"，逐行数据留着白占内存。
        public void Trim()
        {
            EnsureModuleNames();
            _lines.Clear();
            _info.Clear();
            _procs.Clear();
            _types.Clear();
            _procCache.Clear();
            _procRev.Clear();
            _vis = null;
            _visKey = -1;
        }

        // ---- 增量维护 ----

        // 改一行（1-based）。
        public void Patch(int lineNo, string text)
        {
            if (lineNo < 1 || lineNo > _lines.Count) return;
            text = text ?? "";
            if (string.Equals(_lines[lineNo - 1], text, StringComparison.Ordinal)) return;

            int head = StatementHead(lineNo);
            int oldEnd = StatementEnd(head);
            var oldTypes = new List<TypeSpan>(_types);
            _lines[lineNo - 1] = text;
            int newEnd = StatementEnd(head);
            Refresh(head, Math.Max(oldEnd, newEnd), false, oldTypes);
            _visKey = -1;
            Revision++;
        }

        // 把 [at1, at1+remove-1] 这段换成 insert（at1 是 1-based 行号）。
        public void Splice(int at1, int remove, IList<string> insert)
        {
            insert = insert ?? Array.Empty<string>();
            int at = Math.Max(1, Math.Min(at1, _lines.Count + 1));
            if (remove < 0) remove = 0;
            if (at + remove - 1 > _lines.Count) remove = _lines.Count - at + 1;

            int from = StatementHead(at);
            int oldEnd = remove > 0 ? StatementEnd(Math.Min(at + remove - 1, _lines.Count)) : from;
            // 被删掉的那几行要是过程头 / End / Type 头，结构必须整重算（扫不到它们了）
            var oldTypes = new List<TypeSpan>(_types);
            bool cutStruct = false;
            for (int i = at; i < at + remove && i <= _info.Count; i++)
                if (_info[i - 1] != null && _info[i - 1].Kind != LineInfo.None) cutStruct = true;

            _lines.RemoveRange(at - 1, remove);
            _info.RemoveRange(at - 1, remove);
            for (int i = 0; i < insert.Count; i++)
            {
                _lines.Insert(at - 1 + i, insert[i] ?? "");
                _info.Insert(at - 1 + i, null);
            }

            int delta = insert.Count - remove;
            // 结构没变时行号也得跟着挪：过程/类型块的起止行整体平移
            if (delta != 0) ShiftLines(at, delta);
            int to = Math.Max(oldEnd + delta, from);
            if (insert.Count > 0) to = Math.Max(to, StatementEnd(Math.Min(at + insert.Count - 1, _lines.Count)));
            Refresh(from, to, cutStruct, oldTypes);
            _visKey = -1;
            Revision++;
        }

        // 行数变化后平移结构与类型块的行号（结构真变了的话 RebuildStructure 会整重算，这里只是省掉那次全扫）。
        private void ShiftLines(int from, int delta)
        {
            int n = _lines.Count;
            for (int i = 0; i < _procs.Count; i++)
            {
                var p = _procs[i];
                if (p.StartLine >= from) p.StartLine += delta;
                if (p.EndLine >= from) p.EndLine += delta;
                if (p.StartLine < 1) p.StartLine = 1;
                if (p.EndLine < p.StartLine) p.EndLine = p.StartLine;
                if (p.EndLine > n) p.EndLine = n;
            }
            for (int i = 0; i < _types.Count; i++)
            {
                var t = _types[i];
                if (t.From >= from) t.From += delta;
                if (t.To >= from) t.To += delta;
                if (t.From < 1) t.From = 1;
                if (t.To < t.From) t.To = t.From;
                if (t.To > n) t.To = n;
                _types[i] = t;
            }
        }

        // 重算 [from..to]：结构分类、自由标识符、跨行语句的声明。
        private void Refresh(int from, int to, bool forceStruct = false, List<TypeSpan> oldTypes = null)
        {
            if (from < 1) from = 1;
            if (to > _lines.Count) to = _lines.Count;

            bool structChanged = forceStruct;
            for (int i = from; i <= to; i++)
                if (Classify(i)) structChanged = true;
            // 结构变了先重建：下面两步都要靠"这一行属于哪个过程 / 在不在 Type 块里"
            if (structChanged) RebuildStructure();

            Pass(from, to);
            if (structChanged && oldTypes != null) RetouchTypes(oldTypes);
            if (!structChanged) TouchRevisions(from, to);
        }

        // 重算 [from..to]：每行的自由标识符 + 每条语句的声明
        private void Pass(int from, int to)
        {
            if (from < 1) from = 1;
            if (to > _lines.Count) to = _lines.Count;
            if (to < from) return;
            // 声明只挂在语句首行：先整段清掉，再由下面的语句循环按新的语句边界重算
            for (int i = from; i <= to; i++)
            {
                AnalyzeImplicit(i);
                if (_info[i - 1] != null) _info[i - 1].Decls = null;
            }
            int head = StatementHead(from);
            while (head <= to && head <= _lines.Count)
            {
                AnalyzeDecls(head);
                head = StatementEnd(head) + 1;
            }
        }

        // Type 块范围变了：块内每一行的"跳过/收成员"口径跟着变，这些行必须重算。
        private void RetouchTypes(List<TypeSpan> oldTypes)
        {
            if (oldTypes == null || SameSpans(oldTypes, _types)) return;
            long work = 0;
            foreach (var t in oldTypes) work += t.To - t.From + 1;
            foreach (var t in _types) work += t.To - t.From + 1;
            if (work > 4000)
            {
                Pass(1, _lines.Count);
                TouchRevisions(1, _lines.Count);
                return;
            }
            foreach (var t in oldTypes)
            {
                Pass(t.From, t.To);
                TouchRevisions(t.From, t.To);
            }
            foreach (var t in _types)
            {
                Pass(t.From, t.To);
                TouchRevisions(t.From, t.To);
            }
        }

        private static bool SameSpans(List<TypeSpan> a, List<TypeSpan> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].From != b[i].From || a[i].To != b[i].To ||
                    a[i].IsEnum != b[i].IsEnum || a[i].IsPrivate != b[i].IsPrivate ||
                    !string.Equals(a[i].Owner, b[i].Owner, StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

        // 哪些行变了就只作废哪些缓存
        private void TouchRevisions(int from, int to)
        {
            bool mod = false;
            for (int i = from; i <= to && i <= _lines.Count; i++)
            {
                var p = ProcAt(i);
                if (p == null) { mod = true; continue; }
                int r;
                _procRev.TryGetValue(p.ScopeKey, out r);
                _procRev[p.ScopeKey] = r + 1;
            }
            if (mod) _modRev++;
        }

        // 行 -> 所属过程（结构变了以后仍然对，因为这是查出来的不是存的）
        public ProcInfo ProcAt(int line)
        {
            if (line < 1 || _procs.Count == 0) return null;
            int lo = 0, hi = _procs.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_procs[mid].StartLine <= line) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            for (int i = best; i >= 0 && i > best - 4; i--)
                if (line <= _procs[i].EndLine) return _procs[i];
            return null;
        }

        // ---- 结构（过程 / 类型块）----

        private bool Classify(int ln)
        {
            var li = _info[ln - 1];
            if (li == null) _info[ln - 1] = li = new LineInfo();
            byte oldKind = li.Kind;
            string oldName = li.Name;
            bool oldInline = li.InlineEnd;

            li.Kind = 0; li.Name = null; li.ProcKind = null;
            li.IsPrivate = false; li.InlineEnd = false;

            string code = Parser.CodeOnly(_lines[ln - 1]);
            string fw = FirstWordLower(code);
            if (fw != null)
            {
                if (fw == "type" || fw == "enum" || fw == "public" || fw == "private")
                {
                    var tm = ReTypeHead.Match(code);
                    if (tm.Success)
                    {
                        li.Kind = LineInfo.TypeHead;
                        li.Name = Intern(Clean(tm.Groups[3].Value));
                        li.ProcKind = tm.Groups[2].Value.Equals("Enum", StringComparison.OrdinalIgnoreCase)
                            ? "enum" : "type";
                        li.IsPrivate = IsPrivateWord(tm.Groups[1]);
                    }
                }
                if (li.Kind == 0 &&
                    (fw == "sub" || fw == "function" || fw == "property" || fw == "declare" ||
                     fw == "public" || fw == "private" || fw == "friend" || fw == "global" || fw == "static"))
                {
                    var pm = ReProcHead.Match(code);
                    // Declare 只是模块级声明，不是过程：不参与"光标在哪个过程里"的判定。
                    if (pm.Success && !pm.Groups[2].Value.StartsWith("Declare", StringComparison.OrdinalIgnoreCase))
                    {
                        li.Kind = LineInfo.ProcHead;
                        li.Name = Intern(Clean(pm.Groups[3].Value));
                        string head = pm.Groups[2].Value.ToUpperInvariant();
                        if (head.Contains("FUNCTION")) li.ProcKind = "function";
                        else if (head.StartsWith("PROPERTY", StringComparison.Ordinal)) li.ProcKind = "property";
                        else li.ProcKind = "sub";
                        li.IsPrivate = IsPrivateWord(pm.Groups[1]);
                        li.InlineEnd = ReInlineEnd.IsMatch(code);
                    }
                }
                if (li.Kind == 0 && fw == "end")
                {
                    if (ReEndProc.IsMatch(code)) li.Kind = LineInfo.EndProc;
                    else if (ReEndType.IsMatch(code)) li.Kind = LineInfo.EndType;
                }
            }
            return li.Kind != oldKind ||
                   !string.Equals(li.Name, oldName, StringComparison.OrdinalIgnoreCase) ||
                   li.InlineEnd != oldInline;
        }

        private void RebuildStructure()
        {
            int n = _lines.Count;
            var procs = new List<ProcInfo>();
            var open = new List<ProcInfo>();
            var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var types = new List<TypeSpan>();
            TypeSpan cur = default;
            bool inType = false;

            for (int i = 1; i <= n; i++)
            {
                var li = _info[i - 1];
                byte k = li == null ? LineInfo.None : li.Kind;
                if (k == LineInfo.ProcHead)
                {
                    int c;
                    used.TryGetValue(li.Name, out c);
                    used[li.Name] = ++c;
                    // 上一个还没等到 End 的过程必须在这里收尾，否则会一路吃到模块末尾
                    foreach (var prev in open)
                        if (prev.EndLine > i) prev.EndLine = Math.Max(prev.StartLine, i);
                    open.Clear();

                    var p = new ProcInfo
                    {
                        Name = li.Name,
                        Kind = li.ProcKind,
                        StartLine = i,
                        EndLine = n,
                        ScopeKey = c > 1 ? li.Name + "#" + c : li.Name,
                    };
                    procs.Add(p);
                    if (li.InlineEnd) p.EndLine = i;
                    else open.Add(p);
                }
                else if (k == LineInfo.EndProc)
                {
                    if (open.Count > 0)
                    {
                        open[open.Count - 1].EndLine = i;
                        open.RemoveAt(open.Count - 1);
                    }
                }
                else if (k == LineInfo.TypeHead)
                {
                    if (inType) { cur.To = i - 1; types.Add(cur); }
                    cur = new TypeSpan
                    {
                        From = i,
                        To = n,
                        Owner = li.Name,
                        IsEnum = li.ProcKind == "enum",
                        IsPrivate = li.IsPrivate,
                    };
                    inType = true;
                }
                else if (k == LineInfo.EndType)
                {
                    if (inType) { cur.To = i; types.Add(cur); inType = false; }
                }
            }
            if (inType) { cur.To = n; types.Add(cur); }

            _procs = procs;
            _types = types;
            ProcKinds.Clear();
            TypeNames.Clear();
            foreach (var p in procs)
                if (!string.IsNullOrEmpty(p.Name)) ProcKinds[p.Name] = p.Kind;
            foreach (var t in types)
                if (!string.IsNullOrEmpty(t.Owner)) TypeNames.Add(t.Owner);

            _procCache.Clear();
            _procRev.Clear();
            _modRev++;
        }

        public Dictionary<string, string> ProcKinds { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TypeNames { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ---- 单行分析 ----

        private void AnalyzeImplicit(int ln)
        {
            var li = _info[ln - 1];
            if (li == null) _info[ln - 1] = li = new LineInfo();
            li.Implicit = null;

            if (ln < 1 || ln > _lines.Count) return;
            if (InTypeBlock(ln)) return;

            string code = Parser.CodeOnly(_lines[ln - 1]);
            if (code.Length == 0) return;

            string trimmed = code.TrimStart();
            if (trimmed.StartsWith("Attribute ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Option ", StringComparison.OrdinalIgnoreCase))
                return;

            List<string> names = null;
            int i = 0, len = code.Length;
            while (i < len)
            {
                char c = code[i];
                if (!char.IsLetter(c) && c != '_') { i++; continue; }
                int start = i++;
                while (i < len && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i++;
                if (i < len && (code[i] == '%' || code[i] == '&' || code[i] == '#' ||
                                code[i] == '@' || code[i] == '$'))
                    i++;
                string w = Clean(code.Substring(start, i - start));
                if (w.Length == 0) continue;

                if (IsMemberAccess(code, start)) continue;
                if (PrevWordIs(code, start, "As") || PrevWordIs(code, start, "New") ||
                    PrevWordIs(code, start, "Implements")) continue;
                if (PrevWordIs(code, start, "GoTo") || PrevWordIs(code, start, "GoSub") ||
                    PrevWordIs(code, start, "Resume")) continue;
                if (IsLabelDefinition(code, i) || IsNamedArg(code, i)) continue;
                if (IsAfterNumber(code, start)) continue;
                if (VbaBuiltins.IsKeyword(w) || VbaBuiltins.IsBuiltin(w)) continue;
                if (w.Length < 2 && w[0] < 0x80 && !char.IsLetter(w[0])) continue;

                if (names == null) names = new List<string>(6);
                names.Add(Intern(w));
            }
            if (names != null) li.Implicit = names.ToArray();
        }

        // 语句级声明：只认语句首行，跨续行符整条收下来。
        // 这里刻意不看"这一行属于哪个过程"——归属在查询时按 ProcAt 决定，
        // 这样结构一变（多一个 Sub）不用回头重算任何一行的声明。
        private void AnalyzeDecls(int head)
        {
            var li = _info[head - 1];
            if (li == null) _info[head - 1] = li = new LineInfo();
            if (li.Decls != null) li.Decls.Clear();

            int end = StatementEnd(head);
            string code = LogicalCode(head, end);
            if (code == null) return;
            string fw = FirstWordLower(code);
            if (fw == null) return;

            // ---- Type / Enum 块内的成员 ----
            TypeSpan span;
            if (TryTypeSpan(head, out span) && head != span.From)
            {
                if (span.IsEnum)
                    foreach (string seg in code.Split(':'))
                    {
                        var mm = ReMemberName.Match(seg);
                        if (!mm.Success) continue;
                        string mem = Clean(mm.Groups[1].Value);
                        if (mem.Length == 0 || VbaBuiltins.IsKeyword(mem)) continue;
                        AddDecl(li, mem, span.IsPrivate, null, true);
                    }
                return;
            }

            // ---- 类型 / 过程头：名字永远属于模块 ----
            if (li.Kind == LineInfo.TypeHead)
            {
                AddDecl(li, li.Name, li.IsPrivate, li.ProcKind, true);
                return;
            }
            if (li.Kind == LineInfo.ProcHead)
            {
                AddDecl(li, li.Name, li.IsPrivate, li.ProcKind, true);
                AddParams(li, code);
                return;
            }
            if (li.Kind == LineInfo.EndProc) return;

            int k0 = 0;
            while (k0 < code.Length && (code[k0] == ' ' || code[k0] == '\t')) k0++;
            bool hashLine = k0 < code.Length && code[k0] == '#';
            if ((hashLine || fw == "option" || fw == "attribute") && ReSkipLine.IsMatch(code)) return;

            // Declare / 带修饰符的过程头：只有关键字行才跑这个正则
            if (fw == "declare" || fw == "sub" || fw == "function" || fw == "property" ||
                fw == "public" || fw == "private" || fw == "friend" || fw == "global" || fw == "static")
            {
                var pm = ReProcHead.Match(code);
                if (pm.Success)
                {
                    bool isPriv = IsPrivateWord(pm.Groups[1]);
                    string hw = pm.Groups[2].Value.ToUpperInvariant();
                    string kind = hw.Contains("FUNCTION") ? "function"
                        : hw.StartsWith("PROPERTY", StringComparison.Ordinal) ? "property" : "sub";
                    AddDecl(li, Clean(pm.Groups[3].Value), isPriv, kind, true);
                    AddParams(li, code);
                    return;
                }
            }

            // ---- 变量 / 常量声明：本地与模块级同名同姓，只有"导出与否"不同 ----
            var lm = ReLocalDecl.Match(code);
            if (lm.Success)
            {
                foreach (var v in DeclNames(lm.Groups[1].Value))
                    AddDecl(li, v, true, null, false);
                return;
            }

            if (fw == "public" || fw == "private" || fw == "global" || fw == "friend")
            {
                var dm = ReModuleDim.Match(code);
                if (dm.Success)
                {
                    bool isPriv = !dm.Groups[1].Success || IsPrivateWord(dm.Groups[1]);
                    foreach (var v in DeclNames(dm.Groups[2].Value))
                        AddDecl(li, v, isPriv, null, false);
                    return;
                }

                var bm = ReModuleBare.Match(code);
                if (bm.Success)
                {
                    bool isPriv = IsPrivateWord(bm.Groups[1]);
                    foreach (var v in DeclNames(bm.Groups[2].Value))
                        AddDecl(li, v, isPriv, null, false);
                }
            }
        }

        private void AddParams(LineInfo li, string code)
        {
            int lp = code.IndexOf('(');
            int rp = code.LastIndexOf(')');
            if (lp < 0 || rp <= lp) return;
            foreach (var a in ParamNames(code.Substring(lp + 1, rp - lp - 1)))
                AddDecl(li, a, false, null, false);
        }

        private void AddDecl(LineInfo li, string name, bool isPrivate, string kind, bool forceModule)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (li.Decls == null) li.Decls = new List<Decl>(2);
            string n = Intern(name);
            for (int i = 0; i < li.Decls.Count; i++)
                if (string.Equals(li.Decls[i].Name, n, StringComparison.OrdinalIgnoreCase)) return;
            li.Decls.Add(new Decl
            {
                Name = n, IsPrivate = isPrivate, KindHint = kind, ForceModule = forceModule,
            });
        }

        // ---- 语句 / 行 ----

        private bool IsContAt(int ln)
        {
            // 第 ln 行是不是上一行的续行
            if (ln <= 1 || ln > _lines.Count) return false;
            string prev = Parser.CodeOnly(_lines[ln - 2]).Trim();
            return Editing.IsContinuation(prev);
        }

        private int StatementHead(int ln)
        {
            if (ln < 1) ln = 1;
            int i = Math.Min(ln, Math.Max(1, _lines.Count));
            int guard = 0;
            while (i > 1 && IsContAt(i) && ++guard < 64) i--;
            return i;
        }

        private int StatementEnd(int head)
        {
            int i = Math.Max(1, head);
            int guard = 0;
            while (i < _lines.Count && ++guard < 64)
            {
                string code = Parser.CodeOnly(_lines[i - 1]).Trim();
                if (!Editing.IsContinuation(code)) break;
                i++;
            }
            return Math.Min(i, Math.Max(1, _lines.Count));
        }

        private string LogicalCode(int head, int end)
        {
            if (head < 1 || head > _lines.Count) return null;
            var sb = new StringBuilder(160);
            for (int j = head; j <= end && j <= _lines.Count; j++)
            {
                string c = Parser.CodeOnly(_lines[j - 1]);
                string t = c.TrimEnd();
                bool more = Editing.IsContinuation(t);
                sb.Append(more ? t.Substring(0, t.Length - 1) : c);
                sb.Append(' ');
                if (!more) break;
            }
            return sb.ToString();
        }

        private bool InTypeBlock(int ln)
        {
            TypeSpan s;
            return TryTypeSpan(ln, out s) && !s.IsEnum;
        }

        private bool TryTypeSpan(int ln, out TypeSpan span)
        {
            for (int i = 0; i < _types.Count; i++)
                if (ln >= _types[i].From && ln <= _types[i].To) { span = _types[i]; return true; }
            span = default;
            return false;
        }

        // ---- 查询 ----

        // 模块级名字（含过程名、控件名），rank 3。Engine 直接遍历，不复制。
        public Dictionary<string, int> ModuleNames()
        {
            EnsureModuleNames();
            return _modNames;
        }

        // 某个过程能直接用的名字，rank 4；不在过程里返回 null。
        // 返回具体类型而不是接口：Engine 每帧都要枚举 + 查它，接口会盒化枚举器。
        public Dictionary<string, int> ProcNames(string procKey, int caretLine)
        {
            var p = FindProc(procKey);
            return p == null ? null : ProcNameDict(p, caretLine);
        }

        public void SetControls(List<string> ctrls)
        {
            if (ctrls == null || ctrls.Count == 0) return;
            if (ReferenceEquals(ctrls, Controls)) return;   // 同一份列表，别白白作废名字缓存
            Controls = ctrls;
            _modRev++;
        }

        // 名字 -> 档位（4 本过程 / 3 本模块）。给自检用；Engine 走上面两条不复制。
        public Dictionary<string, int> Visible(string procKey, int caretLine)
        {
            EnsureModuleNames();
            int pr = 0;
            if (!string.IsNullOrEmpty(procKey)) _procRev.TryGetValue(procKey, out pr);
            long key = ((long)_modRev << 32) | (uint)pr;
            if (_vis != null && key == _visKey &&
                string.Equals(_visProc, procKey ?? "", StringComparison.OrdinalIgnoreCase))
                return _vis;

            var map = new Dictionary<string, int>(_modNames, StringComparer.OrdinalIgnoreCase);
            foreach (var c in Controls) map[c] = 3;

            var p = FindProc(procKey);
            if (p != null)
            {
                var names = ProcNameDict(p, caretLine);
                foreach (var n in names.Keys) map[n] = 4;
            }

            _vis = map;
            _visProc = procKey ?? "";
            _visKey = key;
            return map;
        }

        private ProcInfo FindProc(string procKey)
        {
            if (string.IsNullOrEmpty(procKey)) return null;
            foreach (var p in _procs)
                if (string.Equals(p.ScopeKey, procKey, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        private Dictionary<string, int> ProcNameDict(ProcInfo p, int caretLine)
        {
            int rev;
            _procRev.TryGetValue(p.ScopeKey, out rev);
            ProcCacheEntry cached;
            if (_procCache.TryGetValue(p.ScopeKey, out cached) && cached.Rev == rev)
                return cached.Names;

            // 复用本过程上一次的字典：打字时每键都要重算一次，能省一次分配就省一次
            var set = cached == null ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) : cached.Names;
            set.Clear();
            int from = p.StartLine, to = p.EndLine;
            if (to - from + 1 > ProcScanMax && caretLine > 0)
            {
                from = Math.Max(from, caretLine - ProcScanHalf);
                to = Math.Min(to, caretLine + ProcScanHalf);
            }
            if (to > _lines.Count) to = _lines.Count;
            for (int i = from; i <= to; i++) Collect(i, set);

            _procCache[p.ScopeKey] = new ProcCacheEntry { Rev = rev, Names = set };
            return set;
        }

        private void Collect(int ln, Dictionary<string, int> map)
        {
            var li = _info[ln - 1];
            if (li == null) return;
            if (li.Implicit != null)
                foreach (var n in li.Implicit) map.TryAdd(n, 4);
            if (li.Decls != null)
                foreach (var d in li.Decls)
                {
                    if (d.ForceModule) continue;   // 模块级的走 _modNames
                    map.TryAdd(d.Name, 4);
                }
        }

        private void EnsureModuleNames()
        {
            if (_modNamesRev == _modRev) return;

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            _modRecs.Clear();

            // 过程之间的空隙 = 模块级代码
            int cursor = 1;
            foreach (var p in _procs)
            {
                AddModuleSpan(cursor, p.StartLine - 1, map, order);
                cursor = Math.Max(cursor, p.EndLine + 1);
            }
            AddModuleSpan(cursor, _lines.Count, map, order);

            // 过程首行上的"归属模块"声明（过程名 / 类型名）
            foreach (var p in _procs)
            {
                var li = _info.Count >= p.StartLine ? _info[p.StartLine - 1] : null;
                if (li == null || li.Decls == null) continue;
                foreach (var d in li.Decls)
                    if (d.ForceModule) PutModule(d, map, order);
            }

            foreach (var c in Controls)
                if (!string.IsNullOrEmpty(c) && !map.ContainsKey(c)) { map[c] = 3; order.Add(c); }

            _modNames = map;
            _modOrder.Clear();
            _modOrder.AddRange(order);
            _modNamesRev = _modRev;
            _exportedRev = -1;
        }

        private void AddModuleSpan(int from, int to, Dictionary<string, int> map, List<string> order)
        {
            if (from < 1) from = 1;
            if (to > _lines.Count) to = _lines.Count;
            for (int i = from; i <= to; i++)
            {
                var li = _info[i - 1];
                if (li == null) continue;
                if (li.Decls != null)
                    foreach (var d in li.Decls)
                    {
                        if (d.ForceModule) PutModule(d, map, order);
                        else if (ProcAt(i) == null) PutModule(d, map, order);
                    }
                if (li.Implicit != null)
                    foreach (var n in li.Implicit)
                    {
                        if (!map.ContainsKey(n)) { map[n] = 3; order.Add(n); }
                        NameRec r;
                        if (!_modRecs.TryGetValue(n, out r)) _modRecs[n] = new NameRec();
                    }
            }
        }

        private void PutModule(Decl d, Dictionary<string, int> map, List<string> order)
        {
            if (!map.ContainsKey(d.Name)) { map[d.Name] = 3; order.Add(d.Name); }
            NameRec r;
            if (!_modRecs.TryGetValue(d.Name, out r)) _modRecs[d.Name] = r = new NameRec();
            if (!d.IsPrivate) r.Public = true;
            if (d.KindHint != null) r.Kind = d.KindHint;
        }

        // 别的模块能用的名字（Public 过程 / 变量 / 类型 / 枚举成员）
        public List<string> ExportedNames()
        {
            EnsureModuleNames();
            if (_exportedRev == _modRev && _exported != null) return _exported;
            var list = new List<string>();
            foreach (var n in _modOrder)
            {
                NameRec r;
                if (_modRecs.TryGetValue(n, out r) && r.Public) list.Add(n);
            }
            _exported = list;
            _exportedRev = _modRev;
            return list;
        }

        // 图标档位：过程 > 类型 > 变量
        public string KindOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string kind;
            if (ProcKinds.TryGetValue(name, out kind)) return kind;
            if (TypeNames.Contains(name)) return "type";
            NameRec r;
            if (_modRecs.TryGetValue(name, out r) && r.Kind != null) return r.Kind;
            return null;
        }

        // ---- 词法工具 ----

        private static readonly Regex ReProcHead = new Regex(
            @"^[ \t]*(?:(Public|Private|Friend|Global)[ \t]+)?(?:Static[ \t]+)?" +
            @"(Sub|Function|Property[ \t]+(?:Get|Let|Set)|Declare[ \t]+(?:PtrSafe[ \t]+)?(?:Sub|Function))" +
            @"[ \t]+(" + Id + ")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReEndProc = new Regex(
            @"^[ \t]*End[ \t]+(?:Sub|Function|Property)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReInlineEnd = new Regex(
            @":[ \t]*End[ \t]+(?:Sub|Function|Property)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReLocalDecl = new Regex(
            @"^[ \t]*(?:(?:Static[ \t]+)?(?:Dim|Const|ReDim|WithEvents)[ \t]+|Static[ \t]+)(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReModuleBare = new Regex(
            @"^[ \t]*(Public|Private|Global|Friend)[ \t]+(?!(?:Sub|Function|Property|Declare|Type|Enum|Const|Dim|WithEvents|Event)\b)" +
            @"(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReModuleDim = new Regex(
            @"^[ \t]*(?:(Public|Private|Global|Friend)[ \t]+)?" +
            @"(?:Dim[ \t]+(?:WithEvents[ \t]+)?|Const[ \t]+|WithEvents[ \t]+)(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReTypeHead = new Regex(
            @"^[ \t]*(?:(Public|Private)[ \t]+)?(Type|Enum)[ \t]+(" + Id + ")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReEndType = new Regex(
            @"^[ \t]*End[ \t]+(?:Type|Enum)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReMemberName = new Regex(
            @"^[ \t]*(" + Id + ")", RegexOptions.Compiled);

        private static readonly Regex ReSkipLine = new Regex(
            @"^[ \t]*(?:Option|Attribute|#)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReIdentHead = new Regex("^(" + Id + ")", RegexOptions.Compiled);

        private static readonly Regex ReParamModifier = new Regex(
            @"^\s*(?:Optional|ByVal|ByRef|ParamArray)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Unicode 标识符：VBA 里中文变量名合法。
        private const string Id = @"[\p{L}_][\p{L}\p{N}_]*[\%\&\#\@\$]?";

        private string Intern(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string hit;
            if (_intern.TryGetValue(s, out hit)) return hit;
            _intern[s] = s;
            return s;
        }

        private static bool IsPrivateWord(Group g)
            => g.Success && g.Value.Equals("Private", StringComparison.OrdinalIgnoreCase);

        private static string FirstWordLower(string code)
        {
            int i = 0, n = code.Length;
            while (i < n && (code[i] == ' ' || code[i] == '\t')) i++;
            int s = i;
            while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i++;
            return i == s ? null : code.Substring(s, i - s).ToLowerInvariant();
        }

        private static bool IsMemberAccess(string code, int at)
        {
            int p = at - 1;
            while (p >= 0 && (code[p] == ' ' || code[p] == '\t')) p--;
            return p >= 0 && code[p] == '.';
        }

        private static bool PrevWordIs(string code, int at, string word)
        {
            int p = at - 1;
            while (p >= 0 && (code[p] == ' ' || code[p] == '\t')) p--;
            int e = p;
            while (p >= 0 && (char.IsLetterOrDigit(code[p]) || code[p] == '_')) p--;
            if (e - p != word.Length) return false;
            for (int i = 0; i < word.Length; i++)
                if (char.ToUpperInvariant(code[p + 1 + i]) != char.ToUpperInvariant(word[i]))
                    return false;
            return true;
        }

        private static bool IsLabelDefinition(string code, int from)
        {
            int after = from;
            while (after < code.Length && (code[after] == ' ' || code[after] == '\t')) after++;
            if (after >= code.Length || code[after] != ':') return false;
            for (int i = after + 1; i < code.Length; i++)
                if (code[i] != ' ' && code[i] != '\t') return false;
            return true;
        }

        private static bool IsNamedArg(string code, int from)
        {
            int after = from;
            while (after < code.Length && (code[after] == ' ' || code[after] == '\t')) after++;
            return after + 1 < code.Length && code[after] == ':' && code[after + 1] == '=';
        }

        private static bool IsAfterNumber(string code, int at)
        {
            int p = at - 1;
            if (p < 0) return false;
            char c = code[p];
            return char.IsDigit(c) || c == '&';
        }

        private static IEnumerable<string> DeclNames(string rest)
        {
            foreach (var part in Parser.SplitTopLevel(rest))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                var mm = ReIdentHead.Match(p);
                if (!mm.Success) continue;
                string name = Clean(mm.Groups[1].Value);
                if (name.Length == 0 || VbaBuiltins.IsKeyword(name)) continue;
                yield return name;
            }
        }

        private static IEnumerable<string> ParamNames(string inner)
        {
            foreach (var part in Parser.SplitTopLevel(inner))
            {
                string p = ReParamModifier.Replace(part, "").Trim();
                p = ReParamModifier.Replace(p, "").Trim();
                p = p.TrimStart('(');
                var mm = ReIdentHead.Match(p);
                if (!mm.Success) continue;
                string name = Clean(mm.Groups[1].Value);
                if (name.Length == 0 || VbaBuiltins.IsKeyword(name)) continue;
                yield return name;
            }
        }

        private static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.TrimEnd('%', '&', '!', '#', '@', '$').Trim();
        }
    }
}

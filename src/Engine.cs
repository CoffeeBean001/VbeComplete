// 候选引擎：建池、匹配、排序、弹窗、写回。

using System;
using System.Collections.Generic;

namespace VbeComplete
{
    internal sealed class Candidate
    {
        public string Name;
        public string Kind;        // func / prop / type / var，决定图标
        public int Pos;            // 首个命中下标（越小越靠前）
        public int Span;           // 命中跨度
        public int[] Hits;         // 逐字符命中下标，UI 高亮用；空 = 全都算命中
        public int HitKind;        // 4 相等 / 3 前缀 / 2 词首缩略 / 1 连续 / 0 分散
        public int Run;            // 最长连续命中段
        public int WordStarts;     // 命中落在词边界上的个数
        public int CaseHits;       // 大小写与输入一致的个数
        public int ScopeKind;      // 4 本过程 / 3 本模块 / 2 其它模块 Public
        public int Score;          // 匹配质量分，构造时算一次
    }

    internal sealed class Engine
    {
        public IVbeSource Backend;

        private const int MaxRows = 10;
        public int ViewRows => MaxRows;

        public Action<IReadOnlyList<Candidate>, int, int> OnShow;   // items, selected, top
        public Action OnHide;
        public Action OnMove;

        private List<Candidate> _items = new List<Candidate>();
        private int _sel;
        private int _top;
        public bool Visible { get; private set; }

        // 池 = 头（本过程名字，rank 4）+ 尾（本模块名字 rank 3 / 别的模块导出名 rank 2）。
        // 打字只动头，头只有几十项；尾在模块级改动之前一直是同一份，不复制也不重算。
        private readonly List<string> _headNames = new List<string>();
        private readonly List<string> _tailNames = new List<string>();
        private readonly List<int> _tailRanks = new List<int>();
        // 与 _tailNames 平行的字符掩码，先用它挡掉不可能命中的名字
        private ulong[] _tailMask = new ulong[512];
        // 别的模块能导出的名字，按全工程签名缓存
        private readonly List<string> _otherNames = new List<string>();
        private int _otherSig = int.MinValue;
        private ModuleScope _others_scope;
        private ModuleScope _poolScope;
        private List<ModuleScope> _others = new List<ModuleScope>();
        private string _poolProc;
        private int _poolSig = int.MinValue;
        private int _poolScopeRev = -1;
        private bool _poolReady;
        // 组头时用的过程名集合（去重用），以及尾缓存的有效条件
        private Dictionary<string, int> _poolProcNames;
        private ModuleScope _tailScope;
        private int _tailModRev = int.MinValue;
        private int _tailSig = int.MinValue;
        private bool _tailReady;

        // 这一批候选属于哪个位置，Accept 时复核。
        private string _ctxModule;
        private int _ctxLine;
        private int _ctxWordStart;

        // 上一轮是不是因为"光标前没有词"收的窗（App 判打字竞态用）。
        public bool LastTriggerEmpty { get; private set; }

        public IReadOnlyList<Candidate> Items => _items;
        public int SelectedIndex => _sel;
        public int TopIndex => _top;

        // 预热：先把全工程作用域算一遍，免得第一次按键慢一拍。
        public void WarmScopes()
        {
            try { Backend?.ProjectScopes(out _); } catch { }
            try { Backend?.CurrentScope(); } catch { }
        }

        // ---- 池子 ----

        private void EnsurePool(ModuleScope scope, string procKey, int caretLine,
            List<ModuleScope> others, int sig)
        {
            if (!EnsureTail(scope, others, sig))
                BuildTail(scope, others, sig);

            int rev = scope == null ? -1 : scope.Revision;
            if (_poolReady && ReferenceEquals(scope, _poolScope) && _poolSig == sig && _poolScopeRev == rev &&
                string.Equals(procKey ?? "", _poolProc ?? "", StringComparison.OrdinalIgnoreCase))
                return;
            _poolScope = scope;
            _poolProc = procKey;
            _poolSig = sig;
            _poolScopeRev = rev;
            _poolReady = true;

            // 头：本过程能直接用的名字，几十项，重算一次也就几微秒
            var head = _headNames;
            head.Clear();
            var procNames = scope == null ? null : scope.ProcNames(procKey, caretLine);
            _poolProcNames = procNames;
            if (procNames == null) return;
            foreach (var kv in procNames)
                if (!string.IsNullOrEmpty(kv.Key)) head.Add(kv.Key);
        }

        // 尾缓存是否还新鲜：同一个模块、模块级没改、别的模块没改。
        private bool EnsureTail(ModuleScope scope, List<ModuleScope> others, int sig)
        {
            if (!_tailReady) return false;
            if (!ReferenceEquals(scope, _tailScope)) return false;
            if (!ReferenceEquals(others, _others)) return false;
            if (_tailSig != sig) return false;
            return _tailModRev == (scope == null ? -1 : scope.ModuleRev);
        }

        private void BuildTail(ModuleScope scope, List<ModuleScope> others, int sig)
        {
            var names = _tailNames;
            var ranks = _tailRanks;
            names.Clear();
            ranks.Clear();
            if (names.Capacity < 512) { names.Capacity = 512; ranks.Capacity = 512; }

            // 本模块的名字（含过程名与控件名）
            var modNames = scope == null ? null : scope.ModuleNames();
            if (modNames != null)
                foreach (var kv in modNames)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    names.Add(kv.Key);
                    ranks.Add(3);
                }

            // 别的模块能导出的名字，本模块已有的跳过
            foreach (var n in OtherNames(others, scope, sig))
            {
                if (modNames != null && modNames.ContainsKey(n)) continue;
                names.Add(n);
                ranks.Add(2);
            }

            _tailModRev = scope == null ? -1 : scope.ModuleRev;
            _tailSig = sig;
            _tailScope = scope;
            _others = others;
            _tailReady = true;

            // 掩码和名字一起算好，按键时就不用再碰字符串
            if (_tailMask.Length < names.Count) _tailMask = new ulong[names.Count + 256];
            for (int i = 0; i < names.Count; i++) _tailMask[i] = Fuzzy.Mask(names[i]);
        }

        private List<string> OtherNames(List<ModuleScope> others, ModuleScope scope, int sig)
        {
            if (_otherSig == sig && ReferenceEquals(others, _others) && ReferenceEquals(scope, _others_scope))
                return _otherNames;
            var list = new List<string>(256);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string self = scope == null ? null : scope.Module;
            foreach (var ms in others)
            {
                if (self != null && string.Equals(ms.Module, self, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var n in ms.ExportedNames())
                    if (n.Length > 0 && seen.Add(n)) list.Add(n);
            }
            _otherNames.Clear();
            _otherNames.AddRange(list);
            _otherSig = sig;
            _others_scope = scope;
            return _otherNames;
        }

        public void InvalidatePool()
        {
            _poolReady = false;
            _tailReady = false;
            _poolSig = int.MinValue;
            _tailSig = int.MinValue;
            _poolScopeRev = -1;
        }

        // ---- 触发 ----

        // manual=true 是 Ctrl+Space：光标前没有词时也列出全部。
        public bool Trigger(bool manual, Snapshot snap = null)
        {
            LastTriggerEmpty = false;
            snap = snap ?? Backend?.Snapshot();
            if (snap == null) { Hide(); return false; }

            string line = snap.Text ?? "";
            int caret = Rules.CharColFromDisp(line, snap.CaretCol);
            int wordStart;
            string word = Rules.WordBefore(line, caret, out wordStart);

            if (word.Length == 0)
            {
                if (!manual) { LastTriggerEmpty = true; Hide(); return false; }
                wordStart = caret;
            }

            string mod = snap.Module ?? "";
            var scope = Backend?.CurrentScope();
            var proc = scope?.ProcAt(snap.Line);
            int sig = 0;
            var others = Backend?.ProjectScopes(out sig) ?? _others;
            EnsurePool(scope, proc?.ScopeKey, snap.Line, others, sig);

            var hits = new List<Candidate>(64);
            var head = _headNames;
            for (int pi = 0; pi < head.Count; pi++)
            {
                string name = head[pi];
                if (string.Equals(name, word, StringComparison.OrdinalIgnoreCase)) continue;
                FuzzyHit fh;
                if (!Fuzzy.Match(name, word, out fh)) continue;
                hits.Add(MakeCandidate(name, 4, ref fh));
            }

            var tail = _tailNames;
            var tailRanks = _tailRanks;
            var tailMask = _tailMask;
            var shadows = _poolProcNames;
            ulong qmask = Fuzzy.Mask(word);
            for (int pi = 0; pi < tail.Count; pi++)
            {
                // 掩码不够 = 一定不是子序列，直接跳过，连名字都不用取
                if (qmask != 0 && (tailMask[pi] & qmask) != qmask) continue;
                string name = tail[pi];
                if (string.Equals(name, word, StringComparison.OrdinalIgnoreCase)) continue;
                FuzzyHit fh;
                if (!Fuzzy.Match(name, word, out fh)) continue;
                // 本过程里也有同名声明时，以头那份为准（只在真命中时才查，打字路径零开销）
                if (shadows != null && shadows.ContainsKey(name)) continue;
                hits.Add(MakeCandidate(name, tailRanks[pi], ref fh));
            }

            if (hits.Count == 0) { Hide(); return false; }

            hits.Sort(Cmp);

            _items = hits;
            _sel = 0;
            _top = 0;
            _ctxModule = mod;
            _ctxLine = snap.Line;
            _ctxWordStart = wordStart;
            Visible = true;
            OnShow?.Invoke(_items, _sel, _top);
            return true;
        }

        private Candidate MakeCandidate(string name, int rank, ref FuzzyHit fh)
        {
            var c = new Candidate
            {
                Name = name,
                Pos = fh.First,
                Span = fh.Span,
                Hits = fh.Positions,
                HitKind = fh.Kind,
                Run = fh.Run,
                WordStarts = fh.WordStarts,
                CaseHits = fh.CaseHits,
                ScopeKind = rank,
            };
            c.Kind = Classify(c.Name);
            c.Score = FuzzyScore(c);
            return c;
        }

        // 权重照 VSCode fuzzyScore 的原值
        private const int SC_MATCH = 16;
        private const int SC_CONSECUTIVE = 29;
        private const int SC_WORD_START = 23;
        private const int SC_CASE = 23;
        private const int SC_GAP_LEADING = 5;
        private const int SC_GAP_INNER = 1;

        // 匹配质量分（越大越好），不含作用域与名字长度。
        public static int FuzzyScore(Candidate c)
        {
            if (c == null) return 0;
            int q = c.Hits != null ? c.Hits.Length : 0;
            if (q == 0) return 0;   // Ctrl+Space 全列：不比匹配质量
            return q * SC_MATCH
                 + Math.Max(0, c.Run - 1) * SC_CONSECUTIVE
                 + c.WordStarts * SC_WORD_START
                 + c.CaseHits * SC_CASE
                 - c.Pos * SC_GAP_LEADING
                 - Math.Max(0, c.Span - q) * SC_GAP_INNER;
        }

        private static int Locality(Candidate c)
        {
            return c.ScopeKind <= 0 ? 2 : Math.Min(c.ScopeKind, 4);
        }

        // 分级比较，不做加权求和：命中档次 → 匹配质量 → 作用域 → 名字长度 → 字母序。
        public static int Cmp(Candidate a, Candidate b)
        {
            int c = b.HitKind.CompareTo(a.HitKind); if (c != 0) return c;
            c = b.Score.CompareTo(a.Score); if (c != 0) return c;
            c = Locality(b).CompareTo(Locality(a)); if (c != 0) return c;
            c = a.Name.Length.CompareTo(b.Name.Length); if (c != 0) return c;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        // 图标档位：过程 > 类型 > 变量。
        public string Classify(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string kind = _poolScope == null ? null : _poolScope.KindOf(name);
            if (kind == null && _others != null)
                foreach (var ms in _others)
                {
                    kind = ms.KindOf(name);
                    if (kind != null) break;
                }
            if (kind == "property") return "prop";
            if (kind == "type" || kind == "enum") return "type";
            if (kind == "sub" || kind == "function") return "func";
            return "var";
        }

        // ---- 选择 ----

        public void Move(int delta)
        {
            if (!Visible || _items.Count == 0) return;
            _sel = (_sel + delta) % _items.Count;
            if (_sel < 0) _sel += _items.Count;
            ScrollToSelected();
            OnMove?.Invoke();
        }

        public void SetSelected(int index)
        {
            if (_items.Count == 0) return;
            _sel = Math.Max(0, Math.Min(_items.Count - 1, index));
            ScrollToSelected();
            OnMove?.Invoke();
        }

        public int PageSize => Math.Max(1, ViewRows - 1);

        public void ScrollToSelected()
        {
            if (_sel < _top) _top = _sel;
            else if (_sel >= _top + ViewRows) _top = _sel - ViewRows + 1;
            int maxTop = Math.Max(0, _items.Count - ViewRows);
            if (_top > maxTop) _top = maxTop;
            if (_top < 0) _top = 0;
        }

        public void ScrollBy(int lines)
        {
            if (!Visible) return;
            int maxTop = Math.Max(0, _items.Count - ViewRows);
            _top = Math.Max(0, Math.Min(maxTop, _top + lines));
            OnMove?.Invoke();
        }

        public void SetTop(int top)
        {
            if (!Visible) return;
            int maxTop = Math.Max(0, _items.Count - ViewRows);
            _top = Math.Max(0, Math.Min(maxTop, top));
            if (_sel < _top) _sel = _top;
            else if (_sel >= _top + ViewRows) _sel = _top + ViewRows - 1;
            OnMove?.Invoke();
        }

        public void Hide()
        {
            if (!Visible) return;
            Visible = false;
            _items = new List<Candidate>();
            _sel = 0;
            _top = 0;
            OnHide?.Invoke();
        }

        public bool Accept()
        {
            if (!Visible || _items.Count == 0) return false;
            return AcceptName(_items[_sel].Name);
        }

        public bool AcceptName(string name)
        {
            if (string.IsNullOrEmpty(name) || Backend == null) { Hide(); return false; }

            // 写之前重新取快照：候选算出来后用户可能又敲了字。
            var snap = Backend.Snapshot();
            if (snap == null) { Hide(); return false; }

            int wordStart;
            string line = snap.Text ?? "";
            int caret = Rules.CharColFromDisp(line, snap.CaretCol);
            Rules.WordBefore(line, caret, out wordStart);
            int endCol = caret;
            if (wordStart > endCol) wordStart = endCol;
            // 整个标识符一起替换，光标停在词中间时右侧也要吃掉。
            while (endCol <= line.Length && Rules.IsIdentChar(line[endCol - 1]))
                endCol++;

            // 复核位置：只比行号 / 取词起点 / 模块。
            if (_ctxModule != null &&
                (snap.Line != _ctxLine ||
                 wordStart != _ctxWordStart ||
                 !string.Equals(snap.Module ?? "", _ctxModule, StringComparison.OrdinalIgnoreCase)))
            {
                Hide();
                return false;
            }

            bool ok = Backend.ApplyCompletion(snap.Line, wordStart, endCol, name);
            Hide();
            if (ok) InvalidatePool();
            return ok;
        }
    }
}

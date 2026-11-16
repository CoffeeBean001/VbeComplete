// 编辑手感相关的纯判据：块结构识别、缩进推算、自动配对、退格接管。
// 一行 COM 都不许有（这些函数在键盘钩子线程里跑）；只吃"一行文本 + 一个光标列"，缩进单位也不靠工程反推 TabWidth。

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace VbeComplete
{
    internal static class Editing
    {
        // 可以出现在声明关键字前面的修饰词（`Private Sub Foo()` / `Static Sub`）
        private static readonly string[] ModFirst = { "public", "private", "friend", "static" };

        private static readonly string[] BlockFirst =
            { "sub", "function", "property", "type", "enum", "for", "do", "while", "with" };

        // 前者没有收尾，后者必须由用户写 `#End If`（替它写裸 `End If` 反而是错的）。
        private static readonly Dictionary<string, string> Closers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "sub",      "End Sub" },
                { "function", "End Function" },
                { "property", "End Property" },
                { "type",     "End Type" },
                { "enum",     "End Enum" },
                { "if",       "End If" },
                { "with",     "End With" },
                { "select",   "End Select" },
                { "for",      "Next" },
                { "do",       "Loop" },
                { "while",    "Wend" },
            };

        // 开符号 -> 收符号。半角与全角（中文标点输入法）都要能配对。
        private static readonly Dictionary<char, char> PairClose =
            new Dictionary<char, char>
            {
                { '(', ')' }, { '"', '"' },
                { '（', '）' }, { '“', '”' },
                // 收符号必须自己占一个键：InsertPair 进来先查这张表，查不到就 return，缺了它"敲 ) 时跨过去"走不到。
                { ')', ')' }, { '）', '）' },
            };

        // 退格"一次收一对"认的左符号（左边开、右边闭）
        private static readonly Dictionary<char, char> PairDeleteOpen =
            new Dictionary<char, char>
            {
                { '(', ')' }, { '"', '"' }, { '（', '）' }, { '“', '”' },
            };

        private static readonly Regex ReTailThen =
            new Regex(@"(?:^|\s)then\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReLabel =
            new Regex(@"^\s*(?:[A-Za-z_\u4e00-\u9fff]\w*|\d+)\s*:", RegexOptions.Compiled);

        // ---- 基础 ----

        // 行首空白（空格 + Tab）。
        public static string LeadingWs(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = 0;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            return s.Substring(0, i);
        }

        // 行首空白占几列（Tab 按 4 列算，与 VBE 一致）。
        public static int IndentWidth(string s)
        {
            int n = 0;
            foreach (char c in s ?? "")
            {
                if (c == ' ') n += 1;
                else if (c == '\t') n += 4;
                else break;
            }
            return n;
        }

        // 去掉字符串与注释之后的"代码形状"部分（行尾空白也去掉）。
        public static string CodePart(string line)
        {
            if (string.IsNullOrEmpty(line)) return "";
            if (line.Trim().Length == 0) return "";
            return Parser.CodeOnly(line).TrimEnd();
        }

        // 整行是不是注释（`'...` 或 `Rem ...`）—— 整行没有任何代码。
        public static bool IsCommentOnly(string line)
        {
            string t = (line ?? "").Trim();
            if (t.Length == 0) return false;
            if (t[0] == '\'') return true;
            string low = t.ToLowerInvariant();
            return low == "rem" || low.StartsWith("rem ") || low.StartsWith("rem\t");
        }

        // 行标签（`1:` / `Retry:` / `10: x = 1`）—— 惯例顶格，缩进不代表层级。
        public static bool IsLabelLine(string line)
        {
            string code = CodePart(line);
            if (code.Length == 0) return false;
            return ReLabel.IsMatch(code);
        }

        public static string DirectiveWord(string line)
        {
            string code = CodePart(line);
            if (code.Length == 0) return null;
            string low = Collapse(code).ToLowerInvariant();
            if (!low.StartsWith("#")) return null;
            string rest = low.Substring(1).Trim();
            if (rest.Length == 0) return null;
            return rest.Split(' ')[0];
        }

        private static string Collapse(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool sp = false;
            foreach (char c in s)
            {
                if (c == ' ' || c == '\t')
                {
                    if (!sp) { sb.Append(' '); sp = true; }
                    continue;
                }
                sb.Append(c);
                sp = false;
            }
            return sb.ToString().Trim();
        }

        // 块结构

        public static string BlockKind(string line)
        {
            string code = CodePart(line);
            if (code.Length == 0) return null;
            if (code.EndsWith("_", StringComparison.Ordinal)) return null;   // 续行符：语句没写完

            string low = Collapse(code).ToLowerInvariant();
            bool hashed = low.StartsWith("#");
            string[] words = low.Split(' ');
            int i = 0;
            while (i < words.Length && IsModWord(words[i])) i++;
            string first = i < words.Length ? words[i].TrimStart('#') : "";

            if (first == "else" || first == "elseif" || first == "case")
            {
                // `ElseIf x` 没写 Then —— 不算块头
                if (first == "elseif" && !ReTailThen.IsMatch(low)) return null;
                return hashed ? "directive" : "branch";
            }
            if (ReTailThen.IsMatch(low))
            {
                // 结尾，天然被排除。
                return hashed ? "directive" : "if";
            }
            if (first == "select")
            {
                bool ok = words.Length > i + 1 && words[i + 1] == "case";
                if (!ok) return null;
                return hashed ? "directive" : "select";
            }
            foreach (var w in BlockFirst)
                if (w == first) return hashed ? "directive" : first;
            return null;
        }

        private static bool IsModWord(string w)
        {
            foreach (var m in ModFirst) if (m == w) return true;
            return false;
        }

        public static bool OpensBlock(string line) => BlockKind(line) != null;

        public static string BlockCloser(string line)
        {
            string kind = BlockKind(line);
            if (kind == null) return null;
            string closer;
            return Closers.TryGetValue(kind, out closer) ? closer : null;
        }

        public static bool IsBlockCloserLine(string line)
        {
            foreach (var c in Closers.Values)
                if (IsCloserText(line, c)) return true;
            return false;
        }

        public static bool IsCloserText(string line, string closer)
        {
            if (string.IsNullOrEmpty(closer)) return false;
            string s = CodePart(line).Trim().ToLowerInvariant();
            if (s.Length == 0) return false;
            string want = closer.Trim().ToLowerInvariant();
            return s == want || s.StartsWith(want + " ", StringComparison.Ordinal);
        }

        // 缩进推算

        public static string NextLineIndent(string curLine, IList<string> above, string unit)
        {
            string reference = null;
            if (!string.IsNullOrEmpty(curLine) && curLine.Trim().Length > 0 && !IsCommentOnly(curLine))
            {
                reference = curLine;
            }
            else if (above != null)
            {
                for (int i = above.Count - 1; i >= 0; i--)
                {
                    string t = above[i];
                    if (!string.IsNullOrEmpty(t) && t.Trim().Length > 0 && !IsCommentOnly(t))
                    {
                        reference = t;
                        break;
                    }
                }
            }
            if (reference == null) return LeadingWs(curLine);
            string baseWs = LeadingWs(reference);
            return OpensBlock(reference) ? baseWs + unit : baseWs;
        }

        public static bool BranchAlign(string line, IList<string> above, string unit,
            out string mine, out string next)
        {
            mine = next = null;
            string word = BranchWord(line);
            if (word == null) return false;

            bool hashed = CodePart(line).TrimStart().StartsWith("#", StringComparison.Ordinal);
            string kind = hashed ? "directive" : (word == "case" ? "select" : "if");
            string owner = UnclosedOwnerAbove(above, kind);
            if (owner == null) return false;

            mine = word == "case" ? owner + unit : owner;
            next = mine + unit;
            return true;
        }

        public static string BranchWord(string line)
        {
            string code = CodePart(line);
            if (code.Length == 0) return null;
            if (code.EndsWith("_", StringComparison.Ordinal)) return null;
            string low = Collapse(code).ToLowerInvariant();
            string[] words = low.Split(' ');
            int i = 0;
            while (i < words.Length && IsModWord(words[i])) i++;
            string first = i < words.Length ? words[i].TrimStart('#') : "";
            if (first == "elseif") return ReTailThen.IsMatch(low) ? "elseif" : null;
            if (first == "else" || first == "case") return first;
            return null;
        }

        private static string UnclosedOwnerAbove(IList<string> above, string kind)
        {
            string closer = kind == "directive" ? "#End If" : null;
            if (closer == null && !Closers.TryGetValue(kind, out closer)) return null;
            if (above == null) return null;

            int depth = 0;
            for (int i = above.Count - 1; i >= 0; i--)
            {
                string raw = above[i];
                if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0) continue;

                if (IsCloserText(raw, closer)) { depth++; continue; }
                if (kind == "directive")
                {
                    if (DirectiveWord(raw) != "if") continue;
                }
                else if (!string.Equals(BlockKind(raw), kind, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (depth == 0) return LeadingWs(raw);
                depth--;
            }
            return null;
        }

        public static string IndentUnitFor(IEnumerable<string> lines)
        {
            if (lines != null)
            {
                foreach (var t in lines)
                    if (LeadingWs(t).IndexOf('\t') >= 0) return "\t";
            }
            return "    ";
        }

        public static bool CloserNeeded(IList<string> below, string closer, int baseIndent)
            => CloserNeeded(below, closer, baseIndent, null, null);

        public static bool CloserNeeded(IList<string> below, string closer, int baseIndent,
            string kind, IList<string> above)
        {
            if (string.IsNullOrEmpty(closer) || below == null) return false;

            string first = null;
            foreach (var raw in below)
            {
                if (!string.IsNullOrEmpty(raw) && raw.Trim().Length > 0) { first = raw; break; }
            }

            if (kind == null)
            {
                // ---- 旧口径（v79b）：不看 kind，只看"下方第一个非空行" ----
                foreach (var raw in below)
                {
                    if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0) continue;
                    if (CodePart(raw).Trim().Length == 0) continue;      // 整行注释

                    int w = IndentWidth(raw);
                    if (w > baseIndent) return false;                    // 块里已经有内容
                    if (IsCloserText(raw, closer)) return false;         // 本块的收尾已在下面
                    if (!IsBlockCloserLine(raw)) return false;           // 同缩进的别的东西 = 块已结束
                }
                return true;
            }

            // ---- 完整口径（v80a）----
            if (OwnCloserBelow(below, closer, kind, baseIndent)) return false;
            if (first != null)
            {
                int w = IndentWidth(first);
                if (IsCloserText(first, closer) && w < baseIndent)
                    return OwnerAboveAt(above, kind, w);
            }
            return true;
        }

        private static bool OwnCloserBelow(IList<string> below, string closer, string kind, int baseIndent)
        {
            int depth = 0;      // 同缩进的同类块头（本块下面嵌着的同类块）
            int deeper = 0;     // 真正嵌套（缩进更深）的同类块头

            foreach (var raw in below)
            {
                if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0) continue;
                if (IsCommentOnly(raw) || BranchWord(raw) != null || IndentBlind(raw)) continue;

                int w = IndentWidth(raw);
                if (w < baseIndent) return false;                 // 出了这一层

                if (IsCloserText(raw, closer))
                {
                    if (w == baseIndent)
                    {
                        if (depth == 0) return true;              // 这个收尾就是本块的
                        depth--;
                    }
                    else if (deeper > 0) deeper--;                // 闭合的是更深的那个同类块
                    else return true;                             // 孤零零一个更深的同类收尾：别再补
                    continue;
                }

                if (string.Equals(BlockKind(raw), kind, StringComparison.OrdinalIgnoreCase))
                {
                    if (w == baseIndent) depth++;
                    else deeper++;
                    continue;
                }

                if (w == baseIndent) return false;                // 同缩进的其它东西 -> 本块已结束
            }
            return false;
        }

        private static bool OwnerAboveAt(IList<string> above, string kind, int indent)
        {
            string closer;
            if (!Closers.TryGetValue(kind, out closer)) return false;
            if (above == null) return false;

            int depth = 0;
            for (int i = above.Count - 1; i >= 0; i--)
            {
                string raw = above[i];
                if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0) continue;

                if (IsCloserText(raw, closer)) { depth++; continue; }
                if (!string.Equals(BlockKind(raw), kind, StringComparison.OrdinalIgnoreCase)) continue;
                if (depth == 0) return IndentWidth(raw) == indent;
                depth--;
            }
            return false;
        }

        private static bool IndentBlind(string line)
            => IsLabelLine(line) || DirectiveWord(line) != null;

        // 钩子线程用的三个"要不要接管"判据

        public static bool LooksLikeBlockHeaderPrefix(string line)
        {
            string code = CodePart(line);
            if (code.Length == 0) return IsCommentOnly(line);
            if (code.EndsWith("_", StringComparison.Ordinal)) return false;

            string low = Collapse(code).ToLowerInvariant();
            string[] words = low.Split(' ');
            int i = 0;
            while (i < words.Length && IsModWord(words[i])) i++;
            string first = i < words.Length ? words[i].TrimStart('#') : "";
            switch (first)
            {
                case "if": case "for": case "do": case "while": case "with":
                case "select": case "sub": case "function": case "property":
                case "type": case "enum":
                    return true;
                // v184：块内分支行（`Else` / `ElseIf … Then` / `Case …`）也必须算进来。
                // 用户敲完 `Then` 立刻回车，快照往往还少最后一格 ⇒ 严格判据
                case "else": case "elseif": case "case":
                    return true;
            }
            return false;
        }

        public static bool EnterIndentWanted(string line, int caretDisp)
        {
            string t = line ?? "";
            if (t.Trim().Length == 0) return false;
            if (t.IndexOf('\t') >= 0) return false;
            if (caretDisp <= 0) return false;
            if (caretDisp != Rules.DispCol(t, t.Length)) return false;
            return IsCommentOnly(t) || OpensBlock(t);
        }

        public static bool BlankLineDeleteWanted(string line, int caretDisp)
        {
            string t = line ?? "";
            if (t.Length == 0) return false;
            if (t.Trim().Length > 0) return false;
            if (caretDisp <= 0) return false;
            return caretDisp == t.Length + 1 || caretDisp == Rules.DispCol(t, t.Length);
        }

        public static bool PairDeleteSpan(string line, int caretDisp, out int left, out int right)
        {
            left = right = -1;
            string t = line ?? "";
            if (t.Length == 0) return false;
            if (caretDisp <= 0) return false;

            int cc = Rules.CharColFromDisp(t, caretDisp);
            left = cc - 2;
            right = left + 1;
            if (left < 0 || right >= t.Length) { left = right = -1; return false; }

            char close;
            if (PairDeleteOpen.TryGetValue(t[left], out close) && t[right] == close) return true;
            left = right = -1;
            return false;
        }

        // 自动配对

        public static string PairInsertion(string line, int col0, char open, char close,
            out int caretOff, out bool paired)
        {
            line = line ?? "";
            if (col0 < 0) col0 = 0;
            if (col0 > line.Length) col0 = line.Length;
            caretOff = col0 + 1;
            paired = false;

            bool isQuote = open == '"' || open == '“';
            if (isQuote)
            {
                if (col0 < line.Length && line[col0] == close) return null;   // 跨过去
                string pre = line.Substring(0, col0);
                bool inside = open == '“'
                    ? CountOf(pre, '“') > CountOf(pre, '”')
                    : CountOf(pre, '"') % 2 == 1;
                if (inside) return line.Substring(0, col0) + close + line.Substring(col0);
                paired = true;
                return line.Substring(0, col0) + open + close + line.Substring(col0);
            }
            if (open == '(' || open == '（')
            {
                if (line.Substring(col0).Trim().Length > 0 && EnclosingCallParen(line, col0) < 0)
                    return line.Substring(0, col0) + open + line.Substring(col0);   // 只插左半边
                paired = true;
                return line.Substring(0, col0) + open + close + line.Substring(col0);
            }
            paired = true;
            return line.Substring(0, col0) + open + close + line.Substring(col0);
        }

        public static char CloseOf(char open)
        {
            char c;
            return PairClose.TryGetValue(open, out c) ? c : '\0';
        }

        private static int CountOf(string s, char c)
        {
            int n = 0;
            foreach (char x in s) if (x == c) n++;
            return n;
        }

        public static int EnclosingCallParen(string line, int col0)
        {
            line = line ?? "";
            int end = Math.Max(0, Math.Min(col0, line.Length));
            if (end <= 0) return -1;

            var stack = new List<int>();
            bool inStr = false;
            for (int i = 0; i < end; i++)
            {
                char ch = line[i];
                if (inStr)
                {
                    if (ch == '"') inStr = false;
                    continue;
                }
                if (ch == '"') { inStr = true; continue; }
                if (ch == '\'') break;                    // 注释开始，后面的不算代码
                if (ch == '(' || ch == '（') stack.Add(i);
                else if (ch == ')' || ch == '）')
                {
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                }
            }
            if (stack.Count == 0) return -1;

            int k = stack[stack.Count - 1];
            int j = k - 1;
            while (j >= 0 && (line[j] == ' ' || line[j] == '\t')) j--;
            if (j < 0) return -1;
            char p = line[j];
            if (Rules.IsIdentChar(p) || p == '%' || p == '&' || p == '@' || p == '$' || p == '!')
                return k;
            return -1;
        }

        public static bool UnterminatedString(string line)
        {
            string t = line ?? "";
            bool inStr = false;
            int i = 0, n = t.Length;
            while (i < n)
            {
                char c = t[i];
                if (c == '\'' && !inStr) break;
                if (c == '"')
                {
                    if (inStr && i + 1 < n && t[i + 1] == '"') { i += 2; continue; }
                    inStr = !inStr;
                }
                i++;
            }
            return inStr;
        }

        // 其它判据

        // 过程内跳转（Ctrl+↑ / Ctrl+↓）

        private static readonly Regex ReDimDeclHead =
            new Regex(@"^Dim\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static int LastDimEndLine(IList<string> lines, int startLine, int endLine)
        {
            if (lines == null || lines.Count == 0) return 0;
            int end = Math.Min(endLine, lines.Count);
            for (int i = end - 1; i > startLine; i--)      // 过程头 / 收尾行本身不扫
            {
                string code = Parser.CodeOnly(lines[i - 1]).Trim();
                if (code.Length == 0) continue;
                if (!ReDimDeclHead.IsMatch(code)) continue;

                int last = i;
                while (last < end && IsContinuation(Parser.CodeOnly(lines[last - 1]).Trim()))
                    last++;
                return last >= end ? 0 : last;            // 续行拖到收尾行：不猜
            }
            return 0;
        }

        public static bool IsContinuation(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length < 2) return false;
            if (code[code.Length - 1] != '_') return false;
            char prev = code[code.Length - 2];
            return prev == ' ' || prev == '\t';
        }

        public static int ProcLastCodeLine(IList<string> lines, int startLine, int endLine)
        {
            if (lines == null || lines.Count == 0) return 0;
            int end = Math.Min(endLine, lines.Count);
            int target = end - 1;
            while (target > startLine &&
                   Parser.CodeOnly(lines[target - 1]).Trim().Length == 0)
                target--;
            return target <= startLine ? 0 : target;
        }

        public static bool IsTextKey(int vk)
        {
            if (vk == 0x20) return true;                       // Space
            if (vk >= 0x30 && vk <= 0x39) return true;         // 0-9
            if (vk >= 0x41 && vk <= 0x5A) return true;         // A-Z
            if (vk >= 0x60 && vk <= 0x69) return true;         // 小键盘 0-9
            if (vk >= 0xBA && vk <= 0xC0) return true;         // ;=,-./`
            if (vk >= 0xDB && vk <= 0xDE) return true;         // [\]='
            return false;
        }

        public static int AfterCommentCharCol(string oldLine, int oldCharCol, bool allCommented)
        {
            if (oldCharCol < 1) oldCharCol = 1;
            if (!allCommented) return oldCharCol + 1;          // 插在行首，必然在光标之前
            int idx = (oldLine ?? "").IndexOf('\'');
            return idx >= 0 && idx + 1 < oldCharCol ? oldCharCol - 1 : oldCharCol;
        }
    }
}

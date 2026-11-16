// VBA 源码解析：只吃字符串、不碰 COM，产出过程骨架（名字 / 种类 / 起止行）与类型名。
// 变量 / 参数的归档在 Scope.cs（那边要按"出现在哪个过程"分档）。

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace VbeComplete
{
    internal sealed class ProcInfo
    {
        public string Name;
        public string Kind;
        public int StartLine;      // 1-based，等于 VBE 行号
        public int EndLine;
        public string ScopeKey;    // 同名过程带 #序号

        public override string ToString() => $"{Kind} {Name} [{StartLine}-{EndLine}]";
    }

    internal static class Parser
    {
        // 标识符：必须用 \p{L} 而不是 [A-Za-z]，否则中文变量名 / 过程名整段被跳过、永远不提示。
        private const string IDENT = @"[\p{L}_][\p{L}\p{N}_]*[\%\&\#\@\$]?";

        private static readonly Regex ReSub = new Regex(
            @"^[ \t]*(?:(?:Public|Private|Friend|Global)[ \t]+)?(?:Static[ \t]+)?Sub[ \t]+(" + IDENT + @")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReFunc = new Regex(
            @"^[ \t]*(?:(?:Public|Private|Friend|Global)[ \t]+)?(?:Static[ \t]+)?Function[ \t]+(" + IDENT + @")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReProp = new Regex(
            @"^[ \t]*(?:(?:Public|Private|Friend|Global)[ \t]+)?(?:Static[ \t]+)?Property[ \t]+(?:Get|Let|Set)[ \t]+(" + IDENT + @")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReEndProc = new Regex(
            @"^[ \t]*End[ \t]+(?:Sub|Function|Property)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 单行过程：过程头之后同一行里还有 `: End Sub`（VBE 允许 `Sub A(): x = 1: End Sub`）。
        // ReEndProc 锚在行首，认不出行尾那个 End，所以必须单独匹配。
        private static readonly Regex ReInlineEnd = new Regex(
            @":[ \t]*End[ \t]+(?:Sub|Function|Property)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReEnumHead = new Regex(
            @"^[ \t]*(?:(?:Public|Private)[ \t]+)?Enum[ \t]+(" + IDENT + ")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReTypeHead = new Regex(
            @"^[ \t]*(?:(?:Public|Private)[ \t]+)?Type[ \t]+(" + IDENT + ")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 按物理行切分（VBE 行号就是物理行号，不能把续行合并掉）。
        public static string[] SplitLines(string text)
        {
            if (text == null) return Array.Empty<string>();
            string t = text.Replace("\r\n", "\n").Replace('\r', '\n');
            // 末尾的换行会切出一个空元素，VBE 的 CountOfLines 也算这一行，保留
            return t.Split('\n');
        }

        // 去掉注释与字符串内容，只留"代码形状"的部分。
        public static string CodeOnly(string line)
        {
            if (string.IsNullOrEmpty(line)) return "";
            var sb = new StringBuilder(line.Length);
            bool inStr = false;
            bool stmtStart = true;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inStr)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { i++; continue; }
                        inStr = false;
                    }
                    sb.Append(' ');
                    continue;
                }
                if (c == '"') { inStr = true; sb.Append(' '); continue; }
                if (c == '\'') break;

                // Rem 只在语句起点（行首或冒号后）算注释，否则 `x = 1: Rem ...` 的后半截会被当成代码。
                if (stmtStart && IsRemAt(line, i))
                {
                    sb.Append(' ', line.Length - i);
                    break;
                }

                if (c == ':') { stmtStart = true; sb.Append(c); continue; }
                if (c != ' ' && c != '\t') stmtStart = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        // line[i] 起是不是一个独立的 `Rem`（后面不能紧跟字母数字）。
        private static bool IsRemAt(string line, int i)
        {
            if (i + 2 >= line.Length) return false;
            if ((line[i] != 'R' && line[i] != 'r') ||
                (line[i + 1] != 'E' && line[i + 1] != 'e') ||
                (line[i + 2] != 'M' && line[i + 2] != 'm')) return false;
            int after = i + 3;
            return after >= line.Length || !char.IsLetterOrDigit(line[after]);
        }

        public static List<ProcInfo> ExtractProcedures(string text)
        {
            var list = new List<ProcInfo>();
            var lines = SplitLines(text);
            var open = new List<ProcInfo>();
            var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < lines.Length; i++)
            {
                string code = CodeOnly(lines[i]);
                if (code.Length == 0) continue;

                Match m = ReSub.Match(code);
                string kind = "sub";
                if (!m.Success) { m = ReFunc.Match(code); kind = "function"; }
                if (!m.Success) { m = ReProp.Match(code); kind = "property"; }

                if (m.Success)
                {
                    string name = m.Groups[1].Value.TrimEnd('%', '&', '!', '#', '@', '$');
                    int n;
                    used.TryGetValue(name, out n);
                    used[name] = ++n;

                    // 新过程头出现时，上一个还没等到 End Sub 的过程必须在这里收尾，否则其 EndLine 会一路吃到模块末尾。
                    if (open.Count > 0)
                    {
                        foreach (var prev in open)
                            if (prev.EndLine > i) prev.EndLine = Math.Max(prev.StartLine, i);
                        open.Clear();
                    }

                    var p = new ProcInfo
                    {
                        Name = name,
                        Kind = kind,
                        StartLine = i + 1,
                        EndLine = lines.Length,
                        ScopeKey = n > 1 ? name + "#" + n : name,
                    };
                    list.Add(p);

                    // 单行过程（`Sub A(): x = 1: End Sub`）：行尾的 End 不在行首，这里直接收尾、不进 open。
                    if (ReInlineEnd.IsMatch(code))
                    {
                        p.EndLine = i + 1;
                        continue;
                    }

                    open.Add(p);
                    continue;
                }

                if (ReEndProc.IsMatch(code) && open.Count > 0)
                {
                    open[open.Count - 1].EndLine = i + 1;
                    open.RemoveAt(open.Count - 1);
                }
            }
            return list;
        }

        // 第 line 行（1-based）落在哪个过程里；不在任何过程里返回 null。
        public static ProcInfo ProcOfLine(List<ProcInfo> procs, int line)
        {
            if (procs == null) return null;
            foreach (var p in procs)
                if (line >= p.StartLine && line <= p.EndLine) return p;
            return null;
        }

        // 按顶层逗号切分（不切括号里面的逗号）。
        public static List<string> SplitTopLevel(string s)
        {
            var outp = new List<string>();
            if (s == null) return outp;
            int depth = 0;
            var cur = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                if (c == ',' && depth == 0) { outp.Add(cur.ToString()); cur.Clear(); continue; }
                cur.Append(c);
            }
            outp.Add(cur.ToString());
            return outp;
        }

        private static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.TrimEnd('%', '&', '!', '#', '@', '$').Trim();
        }

        // 模块里声明的类型名（Enum X / Type X），用于图标档位。
        public static HashSet<string> ExtractTypeNames(string text)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (text == null) return set;
            foreach (var raw in SplitLines(text))
            {
                string code = CodeOnly(raw);
                if (code.Length == 0) continue;
                var m = ReEnumHead.Match(code);
                if (!m.Success) m = ReTypeHead.Match(code);
                if (m.Success) set.Add(m.Groups[1].Value);
            }
            return set;
        }

        public static Dictionary<string, string> ProcKinds(List<ProcInfo> procs)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (procs == null) return d;
            foreach (var p in procs) d[p.Name] = p.Kind;
            return d;
        }
    }
}

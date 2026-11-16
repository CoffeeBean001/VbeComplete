// 纯工具函数：列换算 / 取词 / 替换 / 取词终结。
// 这里不判"该不该弹候选"——候选可见性只看作用域，见 Scope.cs 与 Engine.BuildRankMap。

using System;

namespace VbeComplete
{
    internal static class Rules
    {
        public static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        // 取光标左边那个词。caretCol 是 1-based 字符列，返回词的起始列（1-based）。
        public static string WordBefore(string line, int caretCol, out int wordStartCol)
        {
            wordStartCol = caretCol;
            if (string.IsNullOrEmpty(line)) return "";
            int end = Math.Min(caretCol - 1, line.Length);
            if (end <= 0) return "";
            int i = end - 1;
            if (!IsIdentChar(line[i])) return "";
            int j = i;
            while (j >= 0 && IsIdentChar(line[j])) j--;
            wordStartCol = j + 2;
            return line.Substring(j + 1, i - j);
        }

        // 把 [wordStartCol, endCol) 换成 completion（列都是 1-based）。
        public static string ReplaceWord(string line, int wordStartCol, int endCol, string completion)
        {
            line = line ?? "";
            int start = Math.Max(0, Math.Min(wordStartCol - 1, line.Length));
            int end = Math.Max(start, Math.Min(line.Length, endCol - 1));
            return line.Substring(0, start) + completion + line.Substring(end);
        }

        // VBE 重排整行后，按词边界找补全词真正的结束下标（0-based 排他），取最靠近 near0 的那次。
        public static int FindCompletionEnd(string actual, string completion, int near0, int fallback0)
        {
            int clamped = Math.Max(0, Math.Min(actual == null ? 0 : actual.Length, fallback0));
            if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(completion)) return clamped;
            int L = completion.Length;
            int best = -1;
            int i = 0;
            while (i <= actual.Length - L)
            {
                int hit = actual.IndexOf(completion, i, StringComparison.OrdinalIgnoreCase);
                if (hit < 0) break;
                bool okL = hit == 0 || !IsIdentChar(actual[hit - 1]);
                bool okR = hit + L >= actual.Length || !IsIdentChar(actual[hit + L]);
                if (okL && okR && (best < 0 || Math.Abs(hit - near0) < Math.Abs(best - near0))) best = hit;
                i = hit + L;
            }
            return best >= 0 ? best + L : clamped;
        }

        // VBE 里一个 Tab 等于几个显示列（出厂 4）；列换算全链都依赖它。
        public static int TabWidth = Settings.Int("TAB_WIDTH", 4, 1, 16);

        public static bool IsWide(char c) =>
            (c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0xA4CF) ||
            (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF) ||
            (c >= 0xFE30 && c <= 0xFE6F) || (c >= 0xFF00 && c <= 0xFF60) ||
            (c >= 0xFFE0 && c <= 0xFFE6);

        // 字符下标 -> VBE 认的显示列（1-based，Tab 按 TabWidth 展开、全角占 2）。
        public static int DispCol(string text, int charIndex, int tabw = -1)
        {
            if (tabw <= 0) tabw = TabWidth;
            if (string.IsNullOrEmpty(text)) return 1;
            if (charIndex > text.Length) charIndex = text.Length;
            if (charIndex < 0) charIndex = 0;
            int col = 0;
            for (int i = 0; i < charIndex; i++)
            {
                char c = text[i];
                if (c == '\t') col += tabw - (col % tabw);
                else if (IsWide(c)) col += 2;
                else col += 1;
            }
            return col + 1;
        }

        // "第 charCol 个字符之前" -> VBE 认的显示列（SetCaret 用）。
        public static int CaretColForCharCol(string text, int charCol, int tabw = -1)
        {
            if (charCol < 1) charCol = 1;
            return DispCol(text, charCol - 1, tabw);
        }

        // 反向换算：VBE 给的显示列 -> 1-based 字符列。
        public static int CharColFromDisp(string text, int dispCol, int tabw = -1)
        {
            if (tabw <= 0) tabw = TabWidth;
            if (string.IsNullOrEmpty(text) || dispCol <= 1) return 1;
            int col = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                int w = c == '\t' ? tabw - (col % tabw) : (IsWide(c) ? 2 : 1);
                if (col + w >= dispCol) return i + 1;
                col += w;
            }
            return text.Length + 1;
        }

        // 两行去掉全部空白后是否相同（VBE 保存时会重排空白 / 智能大小写）。
        public static bool SameCodeIgnoringSpace(string a, string b)
        {
            a = a ?? "";
            b = b ?? "";
            int i = 0, j = 0;
            while (true)
            {
                while (i < a.Length && char.IsWhiteSpace(a[i])) i++;
                while (j < b.Length && char.IsWhiteSpace(b[j])) j++;
                if (i >= a.Length || j >= b.Length)
                    return i >= a.Length && j >= b.Length;
                if (char.ToUpperInvariant(a[i]) != char.ToUpperInvariant(b[j])) return false;
                i++; j++;
            }
        }

        public static int SpaceCount(string s)
        {
            int n = 0;
            if (s != null) foreach (char c in s) if (char.IsWhiteSpace(c)) n++;
            return n;
        }

        // 宿主重排空白后，反算保存前光标在旧行的哪个显示列。
        public static int RespaceOldDisp(string oldText, int prevDisp, int snapDisp)
        {
            oldText = oldText ?? "";
            int endDisp = DispCol(oldText, oldText.Length);
            return snapDisp >= 1 && snapDisp <= endDisp ? snapDisp : prevDisp;
        }

        // 把光标从旧行搬到新文本的等价位置（锚 = 非空白字符的序号）。
        public static int RemapCaretOverSpaces(string oldText, int oldCharCol, string newText)
        {
            oldText = oldText ?? "";
            newText = newText ?? "";
            int cut = Math.Max(0, Math.Min(oldCharCol - 1, oldText.Length));
            int k = 0;
            for (int i = 0; i < cut; i++) if (!char.IsWhiteSpace(oldText[i])) k++;
            if (k == 0) return 0;
            int seen = 0;
            for (int i = 0; i < newText.Length; i++)
            {
                if (char.IsWhiteSpace(newText[i])) continue;
                if (++seen == k) return i + 2;
            }
            return newText.Length + 1;
        }

        // 把正在输入的词挖成空格，免得半截名字被当成声明、在池子里提示自己；位置对不上就原样返回（缓存可能慢一拍）。
        public static string BlankToken(string moduleText, int line1, int startCol1, string token)
        {
            if (string.IsNullOrEmpty(moduleText) || string.IsNullOrEmpty(token) ||
                line1 < 1 || startCol1 < 1)
                return moduleText;
            string unified = moduleText.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = unified.Split('\n');
            if (line1 > lines.Length) return moduleText;
            string ln = lines[line1 - 1];
            int s = startCol1 - 1;
            int n = token.Length;
            if (s < 0 || s + n > ln.Length) return moduleText;
            if (string.Compare(ln, s, token, 0, n, StringComparison.OrdinalIgnoreCase) != 0)
                return moduleText;
            lines[line1 - 1] = ln.Substring(0, s) + new string(' ', n) + ln.Substring(s + n);
            return string.Join("\n", lines);
        }

        // 光标是不是在注释里（字符串内不算）—— 自动配对引号要用这一版。
        public static bool InCommentOnly(string line, int caretCol)
        {
            if (string.IsNullOrEmpty(line)) return false;
            int end = Math.Max(0, Math.Min(caretCol - 1, line.Length));
            bool inStr = false;
            bool stmtStart = true;
            for (int i = 0; i < end; i++)
            {
                char c = line[i];
                if (inStr)
                {
                    if (c == '"')
                    {
                        if (i + 1 < end && line[i + 1] == '"') { i++; continue; }
                        inStr = false;
                    }
                    continue;
                }
                if (c == '"') { inStr = true; stmtStart = false; continue; }
                if (c == '\'') return true;
                if (c == ':') { stmtStart = true; continue; }
                if (c == ' ' || c == '\t') continue;
                if (!stmtStart) { stmtStart = false; continue; }
                if (i + 3 <= line.Length &&
                    string.Compare(line, i, "Rem", 0, 3, StringComparison.OrdinalIgnoreCase) == 0 &&
                    (i + 3 == line.Length || line[i + 3] == ' ' || line[i + 3] == '\t'))
                    return true;
                stmtStart = false;
            }
            return false;
        }

        // 这个键打出的字符会不会终结光标前那个标识符（arr= / MsgBox(）；0xBA~0xDE 是符号键，Shift+数字也是。
        public static bool EndsWordKey(int vk, bool shift)
        {
            switch (vk)
            {
                case 0xBA:
                case 0xBB:
                case 0xBC:
                case 0xBF:
                case 0xC0:
                case 0xDB:
                case 0xDD:
                case 0xDE:
                    return true;
            }
            return shift && vk >= 0x30 && vk <= 0x39;
        }

        public static string Rstrip(string s) => (s ?? "").TrimEnd(' ', '\t', '\r', '\n');

        // 自动弹窗：最近按过键、行号没变。
        public static bool AutoTriggerWanted(bool keyTriggered, bool lineJumped)
            => keyTriggered && !lineJumped;
    }
}

// 会改用户代码，所以统一返回 bool —— 钩子靠它决定要不要把键还回去。

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace VbeComplete
{
    internal static class Commands
    {
        public static bool InsertLineAbove(VbeBackend be)
        {
            var snap = be.Snapshot();
            if (snap == null) return false;
            string indent = LeadingWs(snap.Text ?? "");
            if (!be.InsertLines(snap.Line, indent)) return false;
            be.SetCaret(snap.Line, indent.Length + 1);
            return true;
        }

        public static bool DuplicateLine(VbeBackend be)
        {
            var snap = be.Snapshot();
            if (snap == null) return false;
            string line = Rules.Rstrip(snap.Text ?? "");
            if (!be.InsertLines(snap.Line + 1, line)) return false;
            be.SetCaret(snap.Line + 1, CharColOf(snap, line));
            return true;
        }

        // Ctrl+K：删整行，光标落到上一行行尾；模块只剩一行则清空该行。
        public static bool DeleteLine(VbeBackend be)
        {
            var snap = be.Snapshot();
            if (snap == null) return false;
            int count = be.LineCount();
            if (count <= 0) return false;

            if (count == 1) return be.ReplaceLineRaw(1, "");

            if (!be.DeleteLines(snap.Line, 1)) return false;

            if (snap.Line <= 1) be.SetCaret(1, 1);
            else be.CaretToLineEnd(snap.Line - 1);
            return true;
        }

        // Ctrl+/：整行注释开关，有选区就逐行处理。
        public static bool ToggleComment(VbeBackend be)
        {
            // 不能用 Snapshot()：它对多行选区一律返回 null，而 Ctrl+/ 的典型用法正是批量注释一段。
            int sl, sc, el, ec;
            if (!be.TryGetSelection(out sl, out sc, out el, out ec)) return false;
            if (sl < 1 || el < sl) return false;

            // 选区跨行且停在行首（EndCol<=1）时，最后一行其实没被选中，排除掉。
            int from = sl, to = el;
            if (to > from && ec <= 1) to--;

            // 模块末尾有虚拟空行（行号 = CountOfLines + 1），夹一下别越界。
            int count = be.LineCount();
            if (count > 0 && to > count) to = count;
            if (to < from) return false;

            // 整段一次读回：逐行 LineAt 是 N 次跨进程往返，稍长一点就明显卡。
            int n = to - from + 1;
            string block = be.LinesBlock(from, n);
            if (block == null) return false;
            var lines = SplitBlock(block, n);
            if (lines.Count == 0) return false;

            bool allCommented = true, anyContent = false;
            foreach (var l in lines)
            {
                string tr = l.TrimStart();
                if (tr.Length == 0) continue;
                anyContent = true;
                if (!tr.StartsWith("'", StringComparison.Ordinal)) { allCommented = false; break; }
            }
            if (!anyContent) return false;

            // 只有拿不到控件时才走下面的手写兜底。
            if (be.RunVbeCommand(allCommented
                    ? VbeBackend.CmdUncommentBlock
                    : VbeBackend.CmdCommentBlock))
            {
                // 文档是 VBE 自己改的，我们这层缓存（快照 / 模块全文 / 作用域）不知道，
                // 必须作废重读，否则补全会拿旧行去算、把首字符吃掉或重复。
                be.OnWeWrote();

                return true;
            }


            bool wrote = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i];
                string nl;
                if (allCommented)
                {
                    int idx = l.IndexOf('\'');
                    nl = idx >= 0 ? l.Remove(idx, 1) : l;
                }
                else
                {
                    // 撇号加在行首（缩进之前），与 VBE 原生"注释块"的产出完全一致。
                    nl = "'" + l;
                }
                if (be.ReplaceLineRaw(from + i, nl)) wrote = true;
            }
            if (!wrote) return false;

            // 行数没变，原行范围仍有效；原来有选区就重新选中同样几行（连按两次可原地取消）。
            bool hadSel = sl != el || sc != ec;
            if (hadSel)
            {
                string last = lines[lines.Count - 1];
                int lastLen = allCommented ? Math.Max(1, last.Length) : last.Length + 1;
                // 列给足，让 VBE 自己钳到行尾 —— 绝不自己算显示列
                be.SetSelectionRange(from, 1, to, lastLen + 100);
            }
            else
            {
                // 注释/解除改动了光标前的字符数，必须按旧行先换算字符列，不能直接用改前的显示列。
                string oldLine = lines[0];
                int oldCharCol = Rules.CharColFromDisp(oldLine, Math.Max(1, sc));
                int newCharCol = Editing.AfterCommentCharCol(oldLine, oldCharCol, allCommented);
                string here = be.LineAt(from) ?? "";
                be.SetCaret(from, Rules.CaretColForCharCol(here, newCharCol));
            }
            return true;
        }

        // 不满足条件一律返回 false，调用方把键原样还给系统。
        public static bool CtrlProcJump(VbeBackend be, int dir)
        {
            var snap = be.Snapshot();
            if (snap == null) return false;
            string text = be.ModuleText();
            if (text == null) return false;

            var lines = Parser.SplitLines(text);
            var procs = Parser.ExtractProcedures(text);
            var proc = Parser.ProcOfLine(procs, snap.Line);
            if (proc == null) return false;

            int start = proc.StartLine, end = proc.EndLine;
            if (start < 1 || end > lines.Length || end <= start) return false;

            int target = dir < 0
                ? Editing.LastDimEndLine(lines, start, end)
                : Editing.ProcLastCodeLine(lines, start, end);
            return target > 0 && be.CaretToLineEnd(target);
        }

        private static int CharColOf(Snapshot s, string line)
        {
            // 快照里的 CaretCol 是 VBE 的显示列（全角占 2 格、Tab 展开），不能当字符下标用。
            return Rules.CharColFromDisp(line, s.CaretCol);
        }

        internal static string LeadingWs(string s)
        {
            int i = 0;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            return s.Substring(0, i);
        }

        // 末尾是空行时 Split 会少一个元素，必须按 n 补齐，否则撇号会加到错的偏移上。
        private static List<string> SplitBlock(string block, int n)
        {
            var parts = (block ?? "").Split(new[] { "\r\n" }, StringSplitOptions.None);
            var list = new List<string>(n);
            for (int i = 0; i < n; i++)
                list.Add(i < parts.Length ? Rules.Rstrip(parts[i]) : "");
            return list;
        }
    }
}

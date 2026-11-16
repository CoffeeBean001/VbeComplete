// 模糊匹配：先试整块连续命中，否则逐字符贪心（优先词边界）。
// 命中位置是逐字符下标集合（UI 用它逐字符上色）；Kind：4 相等 / 3 前缀 / 2 词首缩略 / 1 连续块 / 0 分散。

using System;
using System.Collections.Generic;

namespace VbeComplete
{
    internal struct FuzzyHit
    {
        public int[] Positions;
        public int Kind;
        public int First;
        public int Span;
        public int Run;
        // 排序口径：WordStarts = 命中字符里落在词边界的个数（逐字符计数，非全有全无）；CaseHits = 大小写完全一致的个数。
        public int WordStarts;
        public int CaseHits;
    }

    internal static class Fuzzy
    {
        // 名字里出现过哪些字符（a-z / 0-9 / _ 各占一位，大小写合并）。
        // 子序列匹配的前提是"名字包含查询的全部字符"，所以先比掩码能挡掉绝大多数名字，
        // 代价只是数组取数 + 一次与运算，连字符串都不用碰。
        public static ulong Mask(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            ulong m = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 'a' && c <= 'z') m |= 1UL << (c - 'a');
                else if (c >= 'A' && c <= 'Z') m |= 1UL << (c - 'A');
                else if (c >= '0' && c <= '9') m |= 1UL << (26 + c - '0');
                else if (c == '_') m |= 1UL << 36;
            }
            return m;
        }

        // 词边界 = 首字符 / 下划线后 / 小写转大写；不分配，匹配热路径每个键要问几百次。
        public static bool IsBoundaryAt(string name, int i)
        {
            if (string.IsNullOrEmpty(name) || i < 0 || i >= name.Length) return false;
            if (i == 0) return true;
            char prev = name[i - 1];
            return prev == '_' || (char.IsLower(prev) && char.IsUpper(name[i]));
        }

        // 小写化但长度不变——返回的下标要回头索引原始名字（高亮 / Kind / Span）。
        public static string LowerKeepLen(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var buf = new char[s.Length];
            for (int i = 0; i < s.Length; i++) buf[i] = char.ToLowerInvariant(s[i]);
            return new string(buf);
        }

        // 无法整块命中时逐字符挑位置（档位小者优先、同档取靠前）：0 接续+词边界 / 1 词边界 / 2 接续 / 3 其它。
        // maxpos 反向上界保证"真是子序列就一定凑得出"；直接在原始 name 上做大小写无关比较，不造小写副本。
        public static int[] GreedyPositions(string name, string query)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(query)) return null;
            int n = name.Length, k = query.Length;
            if (k > n) return null;

            // 反向可行性上界：maxpos[t] = query[t] 最靠右的合法位置
            var maxpos = new int[k];
            int limit = n - 1;
            for (int t = k - 1; t >= 0; t--)
            {
                if (limit < 0) return null;
                int j = LastIndexOfIgnoreCase(name, query[t], limit);
                if (j < 0) return null;
                maxpos[t] = j;
                limit = j - 1;
            }

            var pos = new int[k];
            int i = 0;
            for (int t = 0; t < k; t++)
            {
                int hi = maxpos[t];
                int bestJ = -1, bestKey = int.MaxValue;
                for (int j = i; j <= hi; j++)
                {
                    if (!SameChar(name[j], query[t])) continue;
                    bool isB = IsBoundaryAt(name, j);
                    int key = (j == i && isB) ? 0 : isB ? 1 : (j == i) ? 2 : 3;
                    if (bestJ < 0 || key < bestKey || (key == bestKey && j < bestJ))
                    {
                        bestKey = key; bestJ = j;
                    }
                }
                if (bestJ < 0) return null;
                pos[t] = bestJ;
                i = bestJ + 1;
            }
            return pos;
        }

        // query 是不是 name 的子序列（大小写无关）。纯指针推进，不分配。
        public static bool IsSubsequence(string name, string query)
        {
            int n = name.Length, k = query.Length, j = 0;
            for (int i = 0; i < n && j < k; i++)
                if (SameChar(name[i], query[j])) j++;
            return j >= k;
        }

        private static bool SameChar(char a, char b)
        {
            if (a == b) return true;
            return char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
        }

        // 从 limit 往前找 query 字符（大小写无关），找不到返回 -1。
        private static int LastIndexOfIgnoreCase(string name, char q, int limit)
        {
            for (int j = Math.Min(limit, name.Length - 1); j >= 0; j--)
                if (SameChar(name[j], q)) return j;
            return -1;
        }

        public static int LongestRun(int[] positions)
        {
            if (positions == null || positions.Length == 0) return 0;
            int best = 1, cur = 1;
            for (int i = 1; i < positions.Length; i++)
            {
                cur = positions[i] == positions[i - 1] + 1 ? cur + 1 : 1;
                if (cur > best) best = cur;
            }
            return best;
        }

        // 空 query = 全都要（手动 Ctrl+Space），此时 Kind=0、Positions 为空；Kind 越大越像用户要的。
        public static bool Match(string name, string query, out FuzzyHit hit)
        {
            hit = default(FuzzyHit);
            hit.Span = 0;
            if (string.IsNullOrEmpty(name)) return false;
            if (string.IsNullOrEmpty(query))
            {
                hit.Positions = Array.Empty<int>();
                hit.Kind = 0; hit.First = 0; hit.Span = 0; hit.Run = 0;
                hit.WordStarts = 0; hit.CaseHits = 0;
                return true;
            }
            if (query.Length > name.Length) return false;
            // 免分配预判：绝大多数名字在第一轮就被否掉，这里不能产生任何分配
            if (!IsSubsequence(name, query)) return false;

            bool contiguous = false;
            int[] positions;
            int at = IndexOfIgnoreCase(name, query, 0);
            if (at >= 0)
            {
                // 整块连续：取"最靠前、且尽量落在词边界"的那一次出现
                int best = -1, bestRank = int.MaxValue;
                int scan = at;
                while (scan >= 0)
                {
                    int rank = scan == 0 ? 0 : (IsBoundaryAt(name, scan) ? 1 : 2);
                    if (rank < bestRank || (rank == bestRank && scan < best))
                    {
                        bestRank = rank; best = scan;
                    }
                    scan = IndexOfIgnoreCase(name, query, scan + 1);
                }
                positions = new int[query.Length];
                for (int i = 0; i < query.Length; i++) positions[i] = best + i;
                contiguous = true;
            }
            else
            {
                positions = GreedyPositions(name, query);
                if (positions == null) return false;
            }

            int kind;
            if (name.Length == query.Length && IndexOfIgnoreCase(name, query, 0) == 0) kind = 4;
            else if (at == 0) kind = 3;
            else
            {
                bool allBound = true;
                foreach (int p in positions) if (!IsBoundaryAt(name, p)) { allBound = false; break; }
                kind = allBound ? 2 : (contiguous ? 1 : 0);
            }

            hit.Positions = positions;
            hit.Kind = kind;
            hit.First = positions[0];
            hit.Span = positions[positions.Length - 1] - positions[0] + 1;
            hit.Run = LongestRun(positions);

            int ws = 0, cs = 0;
            for (int i = 0; i < positions.Length; i++)
            {
                int p = positions[i];
                if (IsBoundaryAt(name, p)) ws++;
                // 比的是原始大小写（positions[i] 对应 query 的第 i 个字符）
                if (name[p] == query[i]) cs++;
            }
            hit.WordStarts = ws;
            hit.CaseHits = cs;
            return true;
        }

        // 从 from 开始找 query（大小写无关），返回首个命中下标，找不到返回 -1。
        private static int IndexOfIgnoreCase(string name, string query, int from)
        {
            int last = name.Length - query.Length;
            for (int i = Math.Max(0, from); i <= last; i++)
            {
                int j = 0;
                while (j < query.Length && SameChar(name[i + j], query[j])) j++;
                if (j == query.Length) return i;
            }
            return -1;
        }
    }
}

// 代码片段库：SQLite 两列表 snippets(title 主键, code)，外加标题模糊匹配。
// 库文件 snippets-cs.db；纯数据操作，不碰 COM / WinForms。

using System;
using System.Collections.Generic;
using System.IO;

namespace VbeComplete
{
    internal sealed class SnippetHit
    {
        public string Title;
        public string Code;
        public List<int> Hits = new List<int>();
    }

    internal sealed class SnippetStore : IDisposable
    {
        private const string Schema =
            "CREATE TABLE IF NOT EXISTS snippets (title TEXT PRIMARY KEY, code TEXT NOT NULL)";

        private readonly SqliteDb _db = new SqliteDb();
        private string _lastError = "";

        public string LastError() => string.IsNullOrEmpty(_lastError) ? _db.LastError() : _lastError;
        public string Path_ => DbPath();

        // 库路径：SNIPPET_DB（settings.ini / 环境变量）优先，否则程序目录下的 snippets-cs.db。
        public static string DbPath()
        {
            string p = Settings.Raw("SNIPPET_DB");
            if (!string.IsNullOrEmpty(p)) return p;
            return DefaultPath();
        }

        private static string DefaultPath() => System.IO.Path.Combine(Settings.Home, "snippets-cs.db");

        private bool Open()
        {
            if (_db.IsOpen) return true;
            _lastError = "";
            if (!_db.Open(DbPath()))
            {
                _lastError = _db.LastError();

                return false;
            }
            if (!_db.Exec(Schema))
            {
                _lastError = "建表失败：" + _db.LastError();
            }
            return true;
        }

        // ---- 读 ----

        public List<KeyValuePair<string, string>> All()
        {
            var rows = new List<KeyValuePair<string, string>>();
            if (!Open()) return rows;
            _lastError = "";
            _db.ResetError();
            const string sql = "SELECT title, code FROM snippets ORDER BY title COLLATE NOCASE";
            foreach (var r in _db.Query(sql, 0))
                rows.Add(new KeyValuePair<string, string>(
                    r.Length > 0 ? r[0] : "", r.Length > 1 ? r[1] : ""));
            if (!string.IsNullOrEmpty(_db.LastError())) _lastError = _db.LastError();
            return rows;
        }

        public string Get(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;
            if (!Open()) return null;
            var rows = _db.Query("SELECT code FROM snippets WHERE title=?", 1, title);
            return rows.Count == 0 ? null : (rows[0].Length > 0 ? rows[0][0] : "");
        }

        public bool Exists(string title) => Get(title) != null;

        public int Count()
        {
            if (!Open()) return 0;
            return (int)_db.Scalar("SELECT COUNT(*) FROM snippets");
        }

        // ---- 写 ----

        public bool Add(string title, string code)
        {
            title = (title ?? "").Trim();
            if (title.Length == 0) return false;
            if (!Open()) return false;
            // INSERT OR IGNORE：标题重复时 sqlite3_changes 为 0 -> 返回 false
            return _db.Write("INSERT OR IGNORE INTO snippets (title, code) VALUES (?, ?)",
                title, code ?? "");
        }

        public bool Update(string title, string code)
        {
            title = (title ?? "").Trim();
            if (title.Length == 0) return false;
            if (!Open()) return false;
            return _db.Write("UPDATE snippets SET code=? WHERE title=?", code ?? "", title);
        }

        // 新标题已被占用时不动任何数据、返回 false。
        public bool Rename(string oldTitle, string newTitle)
        {
            oldTitle = (oldTitle ?? "").Trim();
            newTitle = (newTitle ?? "").Trim();
            if (oldTitle.Length == 0 || newTitle.Length == 0) return false;
            if (oldTitle == newTitle) return true;
            if (!Open()) return false;
            if (Exists(newTitle)) return false;
            return _db.Write("UPDATE snippets SET title=? WHERE title=?", newTitle, oldTitle);
        }

        public bool Delete(string title)
        {
            title = (title ?? "").Trim();
            if (title.Length == 0) return false;
            if (!Open()) return false;
            return _db.Write("DELETE FROM snippets WHERE title=?", title);
        }

        public void Dispose() => _db.Dispose();
    }

    // ---- 标题模糊匹配（纯函数，强度递减）：整标题相等 > 前缀 > 连续子串 > 子序列；多词 AND ----
    // ⚠️ 大小写归一必须保持长度（"İ".ToLower() 变 2 字符），否则拿它当下标切原串会整体错位。
    internal static class SnippetMatch
    {
        private const string BoundaryChars = " _-.[](){}+*/\\,;:=<>\"'";

        private static string LowerKeepLen(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char ch in text)
            {
                string low = ch.ToString().ToLowerInvariant();
                sb.Append(low.Length == 1 ? low[0] : ch);
            }
            return sb.ToString();
        }

        private static bool IsBoundary(string title, int idx)
        {
            if (idx <= 0) return true;
            char prev = title[idx - 1], cur = title[idx];
            if (BoundaryChars.IndexOf(prev) >= 0) return true;
            if (char.IsUpper(cur) && char.IsLower(prev)) return true;
            if (char.IsDigit(cur) && !char.IsDigit(prev)) return true;
            return false;
        }

        private static int? TermScore(string termLow, string title, string titleLow,
            List<int> hitsOut)
        {
            int n = termLow.Length;
            if (n <= 0) return null;

            int pos = titleLow.IndexOf(termLow, StringComparison.Ordinal);
            if (pos >= 0)
            {
                for (int i = pos; i < pos + n; i++) hitsOut.Add(i);
                if (titleLow == termLow) return 100 + n * 4;
                if (pos == 0) return 80 + n * 4;
                return 60 + n * 4;
            }

            // 子序列：贪心从左往右扫
            var hits = new List<int>();
            int qi = 0;
            for (int idx = 0; idx < titleLow.Length && qi < n; idx++)
            {
                if (titleLow[idx] != termLow[qi]) continue;
                hits.Add(idx);
                qi++;
            }
            if (qi < n) return null;

            int score = 20 + n * 2;
            foreach (int h in hits) if (IsBoundary(title, h)) score += 3;
            for (int i = 0; i + 1 < hits.Count; i++) if (hits[i + 1] == hits[i] + 1) score += 1;
            hitsOut.AddRange(hits);
            return score;
        }

        public static int? Score(string query, string title, out List<int> hits)
        {
            hits = new List<int>();
            string q = (query ?? "").Trim();
            if (q.Length == 0) return 0;
            title = title ?? "";
            string titleLow = LowerKeepLen(title);
            int total = 0;
            var all = new List<int>();
            foreach (var term in q.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var got = new List<int>();
                int? s = TermScore(LowerKeepLen(term), title, titleLow, got);
                if (s == null) { hits.Clear(); return null; }
                total += s.Value;
                all.AddRange(got);
            }
            all.Sort();
            var uniq = new List<int>();
            foreach (int h in all) if (uniq.Count == 0 || uniq[uniq.Count - 1] != h) uniq.Add(h);
            hits = uniq;
            return total;
        }

        // 空 query 原样返回全部（保持库里的顺序）；否则按"分数高 -> 标题短 -> 字母序"排。
        public static List<SnippetHit> Filter(List<KeyValuePair<string, string>> rows, string query)
        {
            var output = new List<SnippetHit>();
            var src = rows ?? new List<KeyValuePair<string, string>>();
            bool empty = string.IsNullOrWhiteSpace(query);

            if (empty)
            {
                foreach (var r in src)
                    output.Add(new SnippetHit { Title = r.Key, Code = r.Value });
                return output;
            }

            var scored = new List<Tuple<int, int, string, SnippetHit>>();
            foreach (var r in src)
            {
                List<int> hits;
                int? s = Score(query, r.Key, out hits);
                if (s == null) continue;
                scored.Add(Tuple.Create(s.Value, (r.Key ?? "").Length,
                    LowerKeepLen(r.Key),
                    new SnippetHit { Title = r.Key, Code = r.Value, Hits = hits }));
            }
            scored.Sort((a, b) =>
            {
                int c = b.Item1.CompareTo(a.Item1); if (c != 0) return c;
                c = a.Item2.CompareTo(b.Item2); if (c != 0) return c;
                return string.CompareOrdinal(a.Item3, b.Item3);
            });
            foreach (var t in scored) output.Add(t.Item4);
            return output;
        }
    }
}

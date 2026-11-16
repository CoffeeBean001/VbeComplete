// 极薄 SQLite 封装：P/Invoke 系统自带的 winsqlite3.dll，不引 NuGet、不带原生二进制。
// 只负责跑 SQL / 读列；写出的必须是真·SQLite 文件（用户会用 DB Browser 编辑）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace VbeComplete
{
    internal sealed class SqliteDb : IDisposable
    {
        private const int SQLITE_OK = 0;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_DONE = 101;
        private static readonly IntPtr SQLITE_TRANSIENT = new IntPtr(-1);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Unicode)]
        private static extern int sqlite3_open16(string filename, out IntPtr db);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Unicode)]
        private static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int nBytes,
            out IntPtr stmt, IntPtr pzTail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Unicode)]
        private static extern int sqlite3_bind_text16(IntPtr stmt, int index, string value,
            int nBytes, IntPtr destructor);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text16(IntPtr stmt, int col);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_count(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr db);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg16(IntPtr db);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_changes(IntPtr db);

        private IntPtr _db = IntPtr.Zero;
        private string _path;
        private string _lastError = "";

        public string LastError() => _lastError;
        public string Path => _path ?? "";

        public bool IsOpen => _db != IntPtr.Zero;

        // 打开（不存在就建）。失败返回 false，原因在 LastError。
        public bool Open(string path)
        {
            if (_db != IntPtr.Zero) return true;
            _path = path;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                IntPtr db;
                int rc = sqlite3_open16(path, out db);
                if (rc != SQLITE_OK || db == IntPtr.Zero)
                {
                    _lastError = "打开数据库失败 rc=" + rc + " 路径=" + path;

                    if (db != IntPtr.Zero) sqlite3_close(db);
                    return false;
                }
                _db = db;
                // 用户可能同时用 DB Browser 打开片段库，没有 busy_timeout 时对端持写锁会立刻 SQLITE_BUSY，
                // 表现为"保存片段没反应"。
                Exec("PRAGMA busy_timeout = 2000");
                ResetError();
                return true;
            }
            catch (Exception e)
            {
                // DllNotFound（老系统没有 winsqlite3.dll）也走这里，绝不抛给用户
                _lastError = "数据库不可用：" + e.Message;

                return false;
            }
        }

        private string ErrMsg()
        {
            if (_db == IntPtr.Zero) return _lastError;
            try
            {
                IntPtr p = sqlite3_errmsg16(_db);
                return p == IntPtr.Zero ? _lastError : Marshal.PtrToStringUni(p);
            }
            catch { return _lastError; }
        }

        // 执行一条不带参数、也不读结果的 SQL（建表 / 建索引 / 触发器）。
        public bool Exec(string sql)
        {
            if (_db == IntPtr.Zero) return false;
            IntPtr stmt;
            try
            {
                int rc = sqlite3_prepare16_v2(_db, sql, -1, out stmt, IntPtr.Zero);
                if (rc != SQLITE_OK || stmt == IntPtr.Zero)
                {
                    _lastError = "SQL 准备失败：" + ErrMsg();
                    return false;
                }
                try
                {
                    rc = sqlite3_step(stmt);
                    bool ok = rc == SQLITE_DONE || rc == SQLITE_ROW;
                    if (!ok) _lastError = "SQL 执行失败：" + ErrMsg();
                    return ok;
                }
                finally { sqlite3_finalize(stmt); }
            }
            catch (Exception e) { _lastError = e.Message; return false; }
        }

        // 写语句，返回是否真的改到了行。
        public bool Write(string sql, params object[] args)
        {
            bool changed;
            return Step(sql, args, out changed) && changed;
        }

        private bool Step(string sql, object[] args, out bool changed)
        {
            changed = false;
            if (_db == IntPtr.Zero) return false;
            IntPtr stmt;
            try
            {
                int rc = sqlite3_prepare16_v2(_db, sql, -1, out stmt, IntPtr.Zero);
                if (rc != SQLITE_OK || stmt == IntPtr.Zero)
                {
                    _lastError = "SQL 准备失败：" + ErrMsg();
                    return false;
                }
                try
                {
                    for (int i = 0; i < args.Length; i++) BindOne(stmt, i + 1, args[i]);
                    rc = sqlite3_step(stmt);
                    if (rc != SQLITE_DONE && rc != SQLITE_ROW)
                    {
                        _lastError = "SQL 执行失败：" + ErrMsg();
                        return false;
                    }
                    changed = sqlite3_changes(_db) > 0;
                    return true;
                }
                finally { sqlite3_finalize(stmt); }
            }
            catch (Exception e) { _lastError = e.Message; return false; }
        }

        // 参数只支持 string / int / bool / null。int 故意走 bind_text16 转十进制文本，
        // 列的 INTEGER 亲和性会转回去，少一套原生签名就少一处写错的机会。
        private void BindOne(IntPtr stmt, int index, object v)
        {
            if (v == null) { sqlite3_bind_text16(stmt, index, "", 0, SQLITE_TRANSIENT); return; }
            string s;
            if (v is bool) s = (bool)v ? "1" : "0";
            else if (v is int) s = ((int)v).ToString();
            else s = Convert.ToString(v) ?? "";
            sqlite3_bind_text16(stmt, index, s, s.Length * 2, SQLITE_TRANSIENT);
        }

        // 跑一条查询，每行读成 string[]，最多 maxRows 行（0 = 不限）。
        public List<string[]> Query(string sql, int maxRows, params object[] args)
        {
            var rows = new List<string[]>();
            if (_db == IntPtr.Zero) return rows;
            IntPtr stmt;
            try
            {
                int rc = sqlite3_prepare16_v2(_db, sql, -1, out stmt, IntPtr.Zero);
                if (rc != SQLITE_OK || stmt == IntPtr.Zero)
                {
                    _lastError = "SQL 准备失败：" + ErrMsg();
                    return rows;
                }
                try
                {
                    for (int i = 0; i < args.Length; i++) BindOne(stmt, i + 1, args[i]);
                    int ncol = ColumnCount(stmt);
                    // 不能写成 while (step == SQLITE_ROW)：BUSY / ERROR 会被当成"没有更多行"，
                    // 读操作静默返回空表或半张表（典型场景：DB Browser 持有写锁）。
                    int rc2 = SQLITE_DONE;
                    while ((rc2 = sqlite3_step(stmt)) == SQLITE_ROW)
                    {
                        var row = new string[ncol];
                        for (int c = 0; c < ncol; c++) row[c] = ColText(stmt, c);
                        rows.Add(row);
                        if (maxRows > 0 && rows.Count >= maxRows) break;
                    }
                    if (rc2 != SQLITE_ROW && rc2 != SQLITE_DONE)
                        _lastError = "读失败（rc=" + rc2 + "）：" + ErrMsg();
                }
                finally { sqlite3_finalize(stmt); }
            }
            catch (Exception e) { _lastError = e.Message; }
            return rows;
        }

        // 单值查询（COUNT(*) 之类）；出错或没行返回 def。
        public long Scalar(string sql, long def = 0, params object[] args)
        {
            var rows = Query(sql, 1, args);
            if (rows.Count == 0 || rows[0].Length == 0) return def;
            long n;
            return long.TryParse(rows[0][0], out n) ? n : def;
        }

        public void ResetError() => _lastError = "";

        private static int ColumnCount(IntPtr stmt)
        {
            try { return sqlite3_column_count(stmt); } catch { return 0; }
        }

        private static string ColText(IntPtr stmt, int col)
        {
            IntPtr p = sqlite3_column_text16(stmt, col);
            return p == IntPtr.Zero ? "" : (Marshal.PtrToStringUni(p) ?? "");
        }

        public void Dispose()
        {
            if (_db != IntPtr.Zero)
            {
                try { sqlite3_close(_db); } catch { }
                _db = IntPtr.Zero;
            }
        }
    }
}

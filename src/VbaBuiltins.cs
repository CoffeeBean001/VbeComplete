// 语言自带的关键字 / 函数 / 类型：只给解析层把"不是你的名字"剔掉用。

using System;
using System.Collections.Generic;

namespace VbeComplete
{
    internal static class VbaBuiltins
    {
        private static readonly HashSet<string> _kw = Mk(BuiltinTables.Keywords);
        private static readonly HashSet<string> _all = Mk(BuiltinTables.Keywords, BuiltinTables.Functions,
                                                            BuiltinTables.TypeNames);

        private static HashSet<string> Mk(params string[][] src)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var arr in src) foreach (var n in arr) set.Add(n);
            return set;
        }

        public static bool IsKeyword(string name) =>
            !string.IsNullOrEmpty(name) && _kw.Contains(name);

        // 关键字、内置函数、内置类型，都算语言自带的。
        public static bool IsBuiltin(string name) =>
            !string.IsNullOrEmpty(name) && _all.Contains(name);
    }
}

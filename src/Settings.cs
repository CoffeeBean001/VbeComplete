// 配置中心：只读，不回写 settings.ini。
// 优先级：环境变量 > settings.ini > 代码默认值；认不出的值继续往下走。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VbeComplete
{
    internal static class Settings
    {
        public const string PREFIX = "VBECOMPLETE_";

        private static readonly Dictionary<string, string> _ini =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string _home;

        public static string Home
        {
            get
            {
                if (_home != null) return _home;
                string env = Environment.GetEnvironmentVariable(PREFIX + "HOME");
                if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) { _home = env; return _home; }
                // exe 在 dist\ 里，配置与片段库在上一级；按"有 settings.ini 或片段库"认程序目录。
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "settings.ini")) ||
                        File.Exists(Path.Combine(dir.FullName, "snippets-cs.db")))
                    {
                        _home = dir.FullName;
                        return _home;
                    }
                }
                _home = AppContext.BaseDirectory;
                return _home;
            }
        }

        public static string IniPath()
        {
            string path = Environment.GetEnvironmentVariable(PREFIX + "SETTINGS");
            return string.IsNullOrEmpty(path) ? Path.Combine(Home, "settings.ini") : path;
        }

        public static void Load()
        {
            string path = IniPath();
            _ini.Clear();
            if (!File.Exists(path)) return;
            try
            {
                string section = "";
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        section = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!string.Equals(section, "settings", StringComparison.OrdinalIgnoreCase) &&
                        section.Length != 0)
                        continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    // 行尾注释：`AUTO_SPACE = 1   ; 注释`
                    int sc = v.IndexOf(';');
                    if (sc >= 0 && (sc == 0 || v[sc - 1] == ' ' || v[sc - 1] == '\t'))
                        v = v.Substring(0, sc).Trim();
                    if (k.Length > 0) _ini[k] = v;
                }
            }
            catch (Exception e)
            {
                // 读不了就按全默认跑，当面告诉用户，否则他只会看到"配置没生效"。
                try
                {
                    System.Windows.Forms.MessageBox.Show(
                        "读 settings.ini 失败，配置全部按默认值走：\r\n\r\n" + e.Message,
                        "VbeComplete", System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Warning);
                }
                catch { }
            }
        }

        public static string Raw(string key)
        {
            // 顺序必须与文件头一致：写反了，临时用环境变量换库就换不动。
            string v = Environment.GetEnvironmentVariable(PREFIX + key);
            if (!string.IsNullOrEmpty(v)) return v;
            // 也认不带前缀的环境变量（老用户的 set AUTO_SPACE=1）
            v = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(v)) return v;
            if (_ini.TryGetValue(key, out v) && v.Length > 0) return v;
            return null;
        }

        private static bool? _boolOf(string s)
        {
            if (s == null) return null;
            switch (s.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on": return true;
                case "0": case "false": case "no": case "off": return false;
                default: return null;   // 认不出 -> 继续往下找
            }
        }

        public static bool Flag(string key, bool def)
        {
            var b = _boolOf(Raw(key));
            return b ?? def;
        }

        public static int Int(string key, int def, int min = int.MinValue, int max = int.MaxValue)
        {
            string s = Raw(key);
            int v;
            if (s != null && int.TryParse(s.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out v))
                return Math.Max(min, Math.Min(max, v));
            return def;
        }

        public static double Double(string key, double def)
        {
            string s = Raw(key);
            double v;
            if (s != null && double.TryParse(s.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out v))
                return v;
            return def;
        }
    }
}

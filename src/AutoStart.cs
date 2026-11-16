// 开机自启：在 HKCU\...\Run 下登记一个值（免 UAC、可逆）。
// 注册表是"结果"、autostart.txt 是"意图"：每次启动按意图修一遍，换目录/被清理都能自愈。

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace VbeComplete
{
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private const string ValueName = "VbeComplete-CS";

        // 历史值名：清理 / 识别时一并算作"我们自己"。
        private static readonly string[] LegacyNames = { "VbeComplete" };

        private const string StateFile = "autostart.txt";

        private static string StatePath => Path.Combine(Settings.Home, StateFile);

        private static string[] AllNames()
        {
            var list = new List<string>(1 + LegacyNames.Length);
            list.Add(ValueName);
            list.AddRange(LegacyNames);
            return list.ToArray();
        }

        // 要登记的完整命令行（带引号，防路径里有空格）。
        public static string CommandLine
        {
            get
            {
                string exe = ExePath();
                if (string.IsNullOrEmpty(exe)) return null;
                return "\"" + exe + "\"";
            }
        }

        public static string ExePath()
        {
            try
            {
                string p = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(p) &&
                    p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // dotnet run / dotnet xxx.dll 时 ProcessPath 是 dotnet.exe，登记它开机只会闪一下就没了。
                    string stem = Path.GetFileNameWithoutExtension(p);
                    if (!string.Equals(stem, "dotnet", StringComparison.OrdinalIgnoreCase))
                        return p;
                }
            }
            catch { }
            try { return System.Windows.Forms.Application.ExecutablePath; } catch { }
            return null;
        }

        private static string ExeIn(string cmdline)
        {
            if (string.IsNullOrWhiteSpace(cmdline)) return "";
            string s = cmdline.Trim();
            if (s[0] == '"')
            {
                int q = s.IndexOf('"', 1);
                if (q > 0) return s.Substring(1, q - 1).Trim();
            }
            // 没引号：退到第一个 ".exe" 为止（路径里可能有空格，不能简单按空格切）
            int e = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (e > 0) return s.Substring(0, e + 4).Trim();
            return s;
        }

        // 判据：同目录同名，或不同目录但文件已不存在（失效项）。别家的同名程序文件还在的，绝不许动。
        internal static bool IsOurCommand(string value)
        {
            string exe = ExeIn(value);
            if (string.IsNullOrEmpty(exe)) return false;
            string me = ExePath();
            if (string.IsNullOrEmpty(me)) return false;
            if (!string.Equals(Path.GetFileName(exe), Path.GetFileName(me),
                               StringComparison.OrdinalIgnoreCase))
                return false;
            if (SameDir(exe, me)) return true;
            try { return !File.Exists(exe); } catch { return false; }
        }

        internal static List<KeyValuePair<string, string>> Ours()
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return list;
                    foreach (var name in AllNames())
                    {
                        string v = k.GetValue(name) as string;
                        if (string.IsNullOrWhiteSpace(v)) continue;
                        if (!IsOurCommand(v)) continue;
                        list.Add(new KeyValuePair<string, string>(name, v));
                    }
                }
            }
            catch { }
            return list;
        }

        public static bool IsRegistered(out string value)
        {
            value = null;
            var ours = Ours();
            if (ours.Count == 0) return false;
            foreach (var kv in ours)
            {
                if (kv.Key == ValueName) { value = kv.Value; return true; }
            }
            value = ours[0].Value;
            return true;
        }

        // 用户意图：读 marker，没有就按配置（默认开）。
        public static bool Desired
        {
            get
            {
                try
                {
                    if (File.Exists(StatePath))
                    {
                        string s = File.ReadAllText(StatePath).Trim();
                        if (s == "0") return false;
                        if (s == "1") return true;
                    }
                }
                catch { }
                return Settings.Flag("AUTO_START", true);
            }
        }

        // 用户从托盘菜单切换：先记住意图，再立刻落到注册表。
        public static bool SetEnabled(bool on)
        {
            try
            {
                File.WriteAllText(StatePath, on ? "1" : "0");
            }
            catch { }
            return Apply(on);
        }

        // 启动时调用：按意图把注册表修正到该有的样子；已一致就一个字节都不写。
        public static void Sync()
        {
            var ours = Ours();

            if (!Desired)
            {
                // 没勾：值还在就删掉（别的工具写回来的、早期版本留下的旧值名，一并清掉）
                if (ours.Count > 0)
                {
                    Apply(false);
                }
                return;
            }

            string cmd = CommandLine;
            if (cmd == null) { return; }

            // 已经是"恰好一条、主名、内容对得上"就不用动
            if (ours.Count == 1 &&
                ours[0].Key == ValueName &&
                string.Equals(ours[0].Value, cmd, StringComparison.OrdinalIgnoreCase))
                return;

            // 不能"路径不一样就直接改写"：从 bin\Release、临时目录运行会顶掉用户装好的正式版。
            // 规则：主名指向的文件还在且不在同一目录 ⇒ 只记着不动；旧值失效或同目录改名才自动修。
            string mainVal = null;
            foreach (var kv in ours)
            {
                if (kv.Key == ValueName) { mainVal = kv.Value; break; }
            }
            if (mainVal != null)
            {
                string oldExe = ExeIn(mainVal);
                bool oldAlive = !string.IsNullOrEmpty(oldExe) && File.Exists(oldExe);
                if (oldAlive && !SameDir(oldExe, ExeIn(cmd)))
                {
                    // 注册表指向别的目录的 exe（文件还在）：不覆盖，让用户在托盘里关一次再打开。
                    return;
                }
            }

            Apply(true);
        }

        private static bool SameDir(string a, string b)
        {
            try
            {
                string da = Path.GetDirectoryName(Path.GetFullPath(a));
                string db = Path.GetDirectoryName(Path.GetFullPath(b));
                return string.Equals(da, db, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // 写 / 删注册表。on=true 时顺手把历史值名归一（只留主名一条）。
        private static bool Apply(bool on)
        {
            string cmd = CommandLine;
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey, true))
                {
                    if (k == null) { return false; }
                    if (on)
                    {
                        if (cmd == null) return false;
                        k.SetValue(ValueName, cmd, RegistryValueKind.String);
                        // 归一：指向我们这份 exe 的旧值名删掉，免得开机被拉起两次
                        foreach (var n in LegacyNames)
                        {
                            string v = k.GetValue(n) as string;
                            if (string.IsNullOrWhiteSpace(v)) continue;
                            if (IsOurCommand(v))
                            {
                                k.DeleteValue(n, false);
                            }
                        }
                        return true;
                    }

                    // 关闭：所有"我们的"值名一起删；同名但指向别处有效程序的键不动
                    foreach (var n in AllNames())
                    {
                        string v = k.GetValue(n) as string;
                        if (v == null) continue;
                        if (IsOurCommand(v))
                        {
                            k.DeleteValue(n, false);
                        }
                        else
                        {
                        }
                    }
                    return true;
                }
            }
            catch (Exception e)
            {
                LastError = e.Message;
                return false;
            }
        }

        // 上一次 Apply 失败的原因，直接摆给用户看。
        public static string LastError { get; private set; } = "";
    }
}

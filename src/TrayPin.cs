// 按 ExecutablePath 精确找到自己那条，只把 IsPromoted 置 1，绝不碰别人的。

using System;
using Microsoft.Win32;

namespace VbeComplete
{
    internal static class TrayPin
    {
        private const string Root = @"Control Panel\NotifyIconSettings";

        public static void EnsureVisible()
        {
            string exe = AutoStart.ExePath();
            if (string.IsNullOrEmpty(exe)) return;

            int seen = 0, changed = 0;
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(Root, true))
                {
                    if (root == null) return;          // 老系统 / 精简版没有这个键
                    foreach (string id in root.GetSubKeyNames())
                    {
                        try
                        {
                            using (var k = root.OpenSubKey(id, true))
                            {
                                if (k == null) continue;
                                string p = k.GetValue("ExecutablePath") as string;
                                if (string.IsNullOrEmpty(p)) continue;
                                if (!string.Equals(p, exe, StringComparison.OrdinalIgnoreCase)) continue;

                                seen++;
                                object raw = k.GetValue("IsPromoted");
                                int cur = raw is int i ? i : (raw is long l ? (int)l : 0);
                                if (cur != 1)
                                {
                                    k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                                    changed++;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }
}

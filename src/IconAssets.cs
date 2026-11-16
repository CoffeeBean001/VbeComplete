// ② 托盘 / 任务栏 / 窗口必须是同一张图；③ .ico 里放了 16~256 九帧，new Icon(stream, size)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace VbeComplete
{
    internal static class IconAssets
    {
        private const string ResourceName = "VbeComplete.app.ico";

        // 按尺寸缓存，且**永不替换已交出去的实例**：托盘 / 窗口 / 片段窗都直接
        private static readonly Dictionary<int, Icon> _cache = new Dictionary<int, Icon>();
        private static readonly object _lock = new object();

        public static Icon Get(int size)
        {
            if (size <= 0) size = 32;
            lock (_lock)
            {
                Icon hit;
                if (_cache.TryGetValue(size, out hit)) return hit;

                try
                {
                    var asm = typeof(IconAssets).Assembly;
                    using (Stream s = asm.GetManifestResourceStream(ResourceName))
                    {
                        if (s != null)
                        {
                            var ic = new Icon(s, new Size(size, size));
                            _cache[size] = ic;
                            return ic;
                        }
                    }

                }
                catch { }

                // 必须 Clone：SystemIcons.Application 是**进程共享**对象，交出去
                var fallback = (Icon)SystemIcons.Application.Clone();
                _cache[size] = fallback;
                return fallback;
            }
        }

        public static Icon Tray() => Get(32);

        public static Icon Window() => Get(32);
    }
}

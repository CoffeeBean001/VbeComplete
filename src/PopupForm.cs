// 候选窗：无边框、置顶、全部自绘。两条铁律：绝不抢焦点（抢一下用户在 VBE 的输入就断了），
// 以及一个 OnPaint 画完（每行一个 Control 的话滚一下要重排几十个句柄）。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VbeComplete
{
    internal sealed class PopupForm : Form
    {
        // ---- 配色 ----
        private static readonly Color CBg = UiTheme.Hex("#FFFFFF");
        private static readonly Color CFg = UiTheme.Hex("#000000");
        private static readonly Color CHoverBg = UiTheme.Hex("#F0F0F0");
        private static readonly Color CDetailBg = UiTheme.Hex("#F0F0F0");
        private static readonly Color CDetailFg = UiTheme.Hex("#333333");
        private static readonly Color CDetailSep = UiTheme.Hex("#A0A0A0");
        private static readonly Color CTrack = UiTheme.Hex("#F0F0F0");
        private static readonly Color CThumb = UiTheme.Hex("#C0C0C0");
        // 立体边框：上/左取浅色、下/右取深色（VBE 的 NameListWndClass 就是这个配色）
        private static readonly Color CEdgeLight = UiTheme.Hex("#F0F0F0");
        private static readonly Color CEdgeDark = UiTheme.Hex("#646464");
        private static readonly Color CScrollEdge = UiTheme.Hex("#808080");

        // 选中行用 VBE"选中文本"的深蓝；蓝底上黑字看不见，命中字母改用亮黄。
        private static readonly Color CSelBg = UiTheme.Hex("#0A62B0");
        private static readonly Color CSelFg = UiTheme.Hex("#FFFFFF");
        private static readonly Color CSelHit = UiTheme.Hex("#FFD97A");

        // 候选字体链：等宽是关键，宽度参差不齐比 VBE 还乱。
        private static readonly string[] PrettyFaces =
        {
            "JetBrains Mono", "Cascadia Mono", "Cascadia Code", "Consolas",
            "Source Code Pro", "Fira Code", "DejaVu Sans Mono",
            "Microsoft YaHei UI", "Segoe UI",
        };

        // 这些出厂字体又细又扁，VBE 拿它当默认不代表我们也该跟。
        private static readonly string[] UglyFaces =
        {
            "Courier New", "Courier", "Fixedsys", "Terminal", "SimSun", "宋体",
            "NSimSun", "新宋体", "MS Sans Serif", "Microsoft Sans Serif",
        };

        // 字体名里可以去掉的字重/字形后缀（"JetBrains Mono Light" -> "JetBrains Mono"）
        private static readonly string[] WeightSuffix =
        {
            "extra light", "extralight", "semi light", "semilight",
            "light", "thin", "medium", "semibold", "semi bold", "bold", "italic",
            "regular", "book",
        };

        private static readonly Dictionary<string, Color> IconColor =
            new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
            {
                { "func", UiTheme.Hex("#1F8A3B") },
                { "prop", UiTheme.Hex("#7A3FA8") },
                { "var",  UiTheme.Hex("#2A5CAA") },
                { "type", UiTheme.Hex("#B26A00") },
                { "kw",   UiTheme.Hex("#707070") },
            };

        private const int PadX = 2;
        private const int IconGap = 3;
        private const int ScrollW = 12;
        private const int MinChars = 10;
        private const string Ellipsis = "...";
        private const int MaxWidth = 640;
        private const int PopupGap = 2;
        private const int PopupTryMax = 4;

        private IReadOnlyList<Candidate> _items = Array.Empty<Candidate>();
        private int _sel;
        private int _top;
        private int _rows = 8;
        private int _rowH = 17;
        private int _iconW = 14;
        private int _hover = -1;
        private int _lastX = int.MinValue, _lastY = int.MinValue, _lastW, _lastH;

        private bool _dragScroll;
        private int _dragOffset;

        private Font _font;
        private Font _fontBold;
        private int _charW;          // 一个字符的像素宽（按 "x"*16 量）
        private int _minTextW;       // 至少留这么多字的宽度
        private Dictionary<string, int> _widthCache =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly GdiText _gdi = new GdiText();
        private readonly GdiText _gdiBold = new GdiText();

        public event Action<int> RowClicked;      // 单击某行（index 为绝对下标）
        public event Action<int> Wheel;           // 滚轮行数
        public event Action<int> ScrollTo;        // 滚动条拖动：目标 top

        public PopupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = CBg;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            ReloadStyle();
            TabStop = false;
        }

        // 重读字体并重算行高/字宽；旧字体必须自己 Dispose（Control 不拥有 Font，
        // 且 _fontBold 在粗体建失败时就是 _font 本身，不能删两次）。
        public void ReloadStyle()
        {
            var oldFont = _font;
            var oldBold = _fontBold;

            _font = BuildFont();
            try
            {
                _fontBold = new Font(_font, FontStyle.Bold);
            }
            catch
            {
                _fontBold = _font;
            }
            // 行高与图标尺寸都跟着字体走，改字号必须重算
            _rowH = Math.Max(16, _font.Height + 4);
            _iconW = Math.Max(12, Math.Min(16, (int)(_font.Height * 0.8f)));
            _gdi.UseFont(_font);
            _gdiBold.UseFont(_fontBold);
            // 量 16 个字符再除：量单个字符会恒 < 1，被兜成 4px。
            _charW = _gdi.MonoW > 0 ? _gdi.MonoW : Math.Max(4, _gdi.Width(new string('x', 16)) / 16);
            _minTextW = _charW * MinChars;
            _widthCache.Clear();
            _gdi.ResetCache();
            _gdiBold.ResetCache();

            if (oldFont != null && !ReferenceEquals(oldFont, _font)) oldFont.Dispose();
            if (oldBold != null && !ReferenceEquals(oldBold, oldFont) &&
                !ReferenceEquals(oldBold, _font)) oldBold.Dispose();
        }

        // 字体跟 VBE 编辑器完全一致（字体名去掉字重后缀，字号不动）；读不到就退到等宽链 / 10pt。
        private static Font BuildFont()
        {
            float vbePt;
            string vbeFace = ReadVbeFont(out vbePt);

            float pt = vbePt > 0 ? vbePt : 10f;
            if (pt < 8) pt = 8;
            if (pt > 24) pt = 24;

            return CreateFont(PickFace(vbeFace).Trim(), pt);
        }

        // 必须按 7.1 → 7.0 → 6.0 的顺序找：只认 6.0 的话，在只写了 7.1 的机器上永远读不到。
        private static string ReadVbeFont(out float pt)
        {
            pt = 0;
            string face = null;
            foreach (var ver in new[] { "7.1", "7.0", "6.0" })
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\VBA\" + ver + @"\Common"))
                    {
                        if (k == null) continue;
                        if (string.IsNullOrEmpty(face)) face = k.GetValue("FontFace") as string;
                        if (pt <= 0)
                        {
                            object h = k.GetValue("FontHeight");
                            // FontHeight 是像素不是点，必须换算，否则字体比 VBE 编辑器大一圈。
                            try { if (h != null) pt = PxToPt(Convert.ToSingle(h)); } catch { }
                        }
                        if (!string.IsNullOrEmpty(face) && pt > 0) break;
                    }
                }
                catch { }
            }
            return face;
        }

        // 像素 → 点：96dpi 下 1pt = 4/3 px，直接当点用会大 33%（按屏幕 DPI 换算，取不到按 96 算）。
        private static float PxToPt(float px)
        {
            float dpi = 96f;
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                    dpi = g.DpiY > 0 ? g.DpiY : 96f;
            }
            catch { }
            return px * 72f / dpi;
        }

        // 挑字体名：先用 VBE 编辑器字体（去掉字重后缀），再退到等宽候选链。
        private static string PickFace(string vbeFace)
        {
            if (!string.IsNullOrEmpty(vbeFace))
            {
                string raw = vbeFace.Trim();
                // 必须先去掉字重后缀：GDI+ 对 "… Light" 这类名字时灵时不灵，
                // 解析不到就会 fallback 成比例字体，候选窗里所有名字立刻歪掉。
                string baseFace = StripWeight(raw);
                if (!IsUgly(baseFace) && FamilyExists(baseFace)) return baseFace;
                if (!IsUgly(raw) && FamilyExists(raw)) return raw;
            }
            foreach (var f in PrettyFaces) if (FamilyExists(f)) return f;
            return "Courier New";
        }

        private static string StripWeight(string face)
        {
            foreach (var w in WeightSuffix)
            {
                if (!face.EndsWith(w, StringComparison.OrdinalIgnoreCase)) continue;
                int cut = face.Length - w.Length;
                if (cut <= 0) continue;
                char prev = face[cut - 1];
                if (prev != ' ' && prev != '-' && prev != '_') continue;
                string trimmed = face.Substring(0, cut).TrimEnd(' ', '-', '_');
                if (trimmed.Length > 0) return trimmed;
            }
            return face;
        }

        private static bool IsUgly(string face)
        {
            foreach (var u in UglyFaces)
                if (string.Equals(face, u, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool FamilyExists(string face)
        {
            if (string.IsNullOrWhiteSpace(face)) return false;
            try
            {
                using (var ff = new FontFamily(face)) { return true; }
            }
            catch { return false; }
        }

        private static Font CreateFont(string face, float pt)
        {
            try
            {
                var f = new Font(face, pt, FontStyle.Regular, GraphicsUnit.Point);
                if (string.Equals(f.Name, face, StringComparison.OrdinalIgnoreCase)) return f;
                f.Dispose();
            }
            catch { }
            return new Font("Courier New", pt, FontStyle.Regular, GraphicsUnit.Point);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：点了也不抢焦点
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不出现在 Alt-Tab
                return cp;
            }
        }

        public IReadOnlyList<Candidate> Items => _items;

        public void SetData(IReadOnlyList<Candidate> items, int sel, int top, int rows)
        {
            _items = items ?? Array.Empty<Candidate>();
            _sel = sel;
            _top = top;
            _rows = Math.Max(1, rows);
            _hover = -1;
            Invalidate();
        }

        public void UpdateView(int sel, int top)
        {
            _sel = sel;
            _top = top;
            Invalidate();
        }

        public int VisibleRows => Math.Max(1, Math.Min(_rows, _items.Count - _top));
        public int ListHeight => VisibleRows * _rowH;
        public int TotalHeight => ListHeight + DetailHeight;

        // 别叫 PreferredSize —— 那是 Control 上的属性，同名方法会把它隐藏掉。
        public Size DesiredSize() => new Size(ComputeWidth(), TotalHeight + 2);

        // 只有"活动行的名字被截断"时才显示详情行。
        public string DetailText()
        {
            int active = _hover >= 0 ? _hover : _sel;
            if (active < 0 || active >= _items.Count) return null;
            string name = _items[active] == null ? null : _items[active].Name;
            if (string.IsNullOrEmpty(name)) return null;
            return _gdi.Width(name) > ContentWidth ? name : null;
        }

        public int DetailHeight
        {
            get { return DetailText() == null ? 0 : _font.Height + 4; }
        }

        private int ContentWidth => Math.Max(MeasureLongest(), _minTextW);

        // 不许看 DetailText：详情行的宽度按这里算，互相引用就成了无限递归。
        private int MeasureLongest()
        {
            int longest = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                string name = _items[i] == null ? null : _items[i].Name;
                if (string.IsNullOrEmpty(name)) continue;
                int w;
                if (!_widthCache.TryGetValue(name, out w))
                {
                    w = _gdi.Width(name);
                    if (_widthCache.Count < 4096) _widthCache[name] = w;
                }
                if (w > longest) longest = w;
            }
            return longest;
        }

        // 宽度 = 最长候选 + 图标 + 内边距 + 滚动条，至少 MinChars 个字符宽，上限 MaxWidth。
        private int ComputeWidth()
        {
            int w2 = PadX + _iconW + IconGap + ContentWidth + ScrollW + PadX;
            string detail = DetailText();
            if (detail != null)
            {
                int dw = PadX + _gdi.Width(detail) + PadX;
                if (dw > w2) w2 = dw;
            }
            return Math.Min(MaxWidth, Math.Max(120, w2));
        }

        // 贴着光标显示；下方放不下就翻到光标上方，若与 VBE 的参数信息窗重叠再整体下移。
        public void PlaceAt(int x, int top, int bottom)
        {
            int w = ComputeWidth();
            int detailH = DetailHeight;
            int listH = VisibleRows * _rowH;
            int h = listH + detailH;

            var scr = Screen.FromPoint(new Point(x, Math.Max(0, bottom)));
            Rectangle wa = scr.WorkingArea;

            int px = x;
            int py = bottom + 2;
            if (py + h > wa.Bottom) py = top - h - 2;

            // 避让 VBE 提示窗（参数信息 / 成员列表）：只在水平也相交时才躲。
            var rects = Native.VbePopupRects();
            if (rects.Length > 0)
            {
                for (int guard = 0; guard < PopupTryMax; guard++)
                {
                    bool moved = false;
                    foreach (var r in rects)
                    {
                        var me = new Native.RECT { Left = px, Top = py, Right = px + w, Bottom = py + h };
                        if (!Native.Overlap(me, r)) continue;
                        py = r.Bottom + PopupGap;
                        moved = true;
                    }
                    if (!moved) break;
                }
                if (py + h > wa.Bottom)
                {
                    int alt = top - h - PopupGap;
                    bool clash = false;
                    foreach (var r in rects)
                    {
                        var me = new Native.RECT { Left = px, Top = alt, Right = px + w, Bottom = alt + h };
                        if (Native.Overlap(me, r)) { clash = true; break; }
                    }
                    py = (!clash && alt >= wa.Top) ? alt : Math.Max(wa.Top, wa.Bottom - h);
                }
            }

            if (py < wa.Top) py = wa.Top;
            if (px + w > wa.Right) px = wa.Right - w;
            if (px < wa.Left) px = wa.Left;

            // 位置没变就别再 SetWindowPos（每帧都调会让窗口轻微闪）
            if (_lastX == px && _lastY == py && _lastW == w && _lastH == h && Visible) return;
            _lastX = px; _lastY = py; _lastW = w; _lastH = h;

            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, px, py, w, h, Native.SWP_NOACTIVATE);
            Width = w; Height = h;
        }

        public void ResetPlacement()
        {
            _lastX = int.MinValue; _lastY = int.MinValue;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.SmoothingMode = SmoothingMode.None;      // 点线框 / 1px 边要锐利

            int visible = VisibleRows;
            int listH = visible * _rowH;
            string detail = DetailText();

            using (var b = new SolidBrush(CBg)) g.FillRectangle(b, 0, 0, Width, listH);
            if (detail != null)
                using (var b = new SolidBrush(CDetailBg)) g.FillRectangle(b, 0, listH, Width, Height - listH);

            // 选中条不能压在滚动条底下（跟 VBE 一样，滚动条是一条独立的长条）
            int barW = _items.Count > _rows ? ScrollW : 0;
            int rowW = Math.Max(1, Width - 1 - barW);

            // 先铺底色和图标，文字统一留到后面一次 HDC 里画完
            for (int i = 0; i < visible; i++)
            {
                int idx = _top + i;
                if (idx >= _items.Count) break;
                var c = _items[idx];
                if (c == null) continue;
                int y = i * _rowH;
                bool selected = idx == _sel;

                if (selected)
                    using (var b = new SolidBrush(CSelBg)) g.FillRectangle(b, 1, y, rowW, _rowH);
                else if (idx == _hover)
                    using (var b = new SolidBrush(CHoverBg)) g.FillRectangle(b, 1, y, rowW, _rowH);

                int ix = PadX;
                int iy = y + (_rowH - _iconW) / 2;
                DrawIcon(g, c.Kind, ix, iy, _iconW, selected);
            }

            // 候选名字：整帧只跟 GDI 要一次 HDC、选一次字体
            if (visible > 0 && _gdi.Hfont != IntPtr.Zero)
            {
                int lc = -1;
                IntPtr hdc = g.GetHdc();
                try
                {
                    IntPtr old = Native.SelectObject(hdc, _gdi.Hfont);
                    Native.SetBkMode(hdc, Native.TRANSPARENT);
                    for (int i = 0; i < visible; i++)
                    {
                        int idx = _top + i;
                        if (idx >= _items.Count) break;
                        var c = _items[idx];
                        if (c == null) continue;
                        DrawName(hdc, c, PadX + _iconW + IconGap, i * _rowH, idx == _sel, ref lc);
                    }
                    Native.SelectObject(hdc, old);
                }
                finally { g.ReleaseHdc(hdc); }
            }

            // 下面这些必须压在文字上面，顺序与改之前一致
            if (detail != null)
            {
                using (var pen = new Pen(CDetailSep)) g.DrawLine(pen, 0, listH, Width, listH);
                using (var b = new SolidBrush(CDetailFg))
                {
                    var rf = new RectangleF(PadX + 1, listH + (Height - listH - _font.Height) / 2f,
                        Width - PadX * 2, _font.Height + 2);
                    using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter })
                        g.DrawString(detail, _font, b, rf, sf);
                }
            }

            // 滚动条（只有真需要时才画）
            if (_items.Count > _rows)
            {
                int trackX = Width - ScrollW;
                using (var b = new SolidBrush(CTrack)) g.FillRectangle(b, trackX, 0, ScrollW, listH);
                using (var pen = new Pen(CScrollEdge)) g.DrawLine(pen, trackX, 0, trackX, listH - 1);
                int total = _items.Count;
                int thumbH = Math.Max(16, (int)((long)listH * Math.Min(_rows, total) / total));
                int maxTop = Math.Max(1, total - _rows);
                int thumbY = (int)((long)(listH - thumbH) * Math.Min(_top, maxTop) / maxTop);
                var thumb = new Rectangle(trackX + 2, thumbY, ScrollW - 3, thumbH - 1);
                using (var b = new SolidBrush(CThumb)) g.FillRectangle(b, thumb);
                using (var pen = new Pen(Color.White)) g.DrawLine(pen, thumb.Left, thumb.Top, thumb.Right, thumb.Top);
                using (var pen = new Pen(CEdgeDark))
                {
                    g.DrawLine(pen, thumb.Left, thumb.Bottom - 1, thumb.Right, thumb.Bottom - 1);
                    g.DrawLine(pen, thumb.Right - 1, thumb.Top, thumb.Right - 1, thumb.Bottom - 1);
                }
            }

            // 1px 立体边：上/左浅、下/右深（与 VBE 列表一致）
            using (var pen = new Pen(CEdgeLight))
            {
                g.DrawLine(pen, 0, 0, Width - 1, 0);
                g.DrawLine(pen, 0, 0, 0, Height - 1);
            }
            using (var pen = new Pen(CEdgeDark))
            {
                g.DrawLine(pen, 0, Height - 1, Width - 1, Height - 1);
                g.DrawLine(pen, Width - 1, 0, Width - 1, Height - 1);
            }
        }

        // 按"命中/未命中"把名字切成若干段（相邻同类合并）。
        public static List<KeyValuePair<string, bool>> SplitByHits(string name, int[] positions)
        {
            var outp = new List<KeyValuePair<string, bool>>();
            if (string.IsNullOrEmpty(name)) return outp;
            var hit = new HashSet<int>();
            if (positions != null) foreach (var p in positions) hit.Add(p);

            int i = 0, n = name.Length;
            while (i < n)
            {
                bool isHit = hit.Contains(i);
                int j = i + 1;
                while (j < n && hit.Contains(j) == isHit) j++;
                outp.Add(new KeyValuePair<string, bool>(name.Substring(i, j - i), isHit));
                i = j;
            }
            return outp;
        }

        // 命中字母只加粗不涂红（VBE 列表全是黑字）；选中行是深蓝底，改白字 + 亮黄才看得见。
        private void DrawName(IntPtr hdc, Candidate c, int x, int y, bool selected, ref int lastColor)
        {
            string name = c.Name ?? "";
            int baselineY = y + (_rowH - _font.Height) / 2;

            int fg = Native.ColorRef(selected ? CSelFg : CFg);
            int hitFg = Native.ColorRef(selected ? CSelHit : CFg);

            string disp = Truncate(name, ContentWidth, out int prefixLen);
            var hits = new List<int>();
            if (c.Hits != null)
                foreach (var p in c.Hits) if (p >= 0 && p < prefixLen) hits.Add(p);

            if (hits.Count == 0)
            {
                DrawChunk(hdc, disp, x, baselineY, false, fg, ref lastColor);
                return;
            }

            int cx = x;
            foreach (var seg in SplitByHits(disp, hits.ToArray()))
                cx = DrawChunk(hdc, seg.Key, cx, baselineY, seg.Value,
                    seg.Value ? hitFg : fg, ref lastColor);
        }

        private int DrawChunk(IntPtr hdc, string s, int x, int y, bool bold, int color, ref int lastColor)
        {
            if (string.IsNullOrEmpty(s)) return x;
            if (color != lastColor) { Native.SetTextColor(hdc, color); lastColor = color; }
            // 粗体段要换成粗体字型再画（同一张 HDC 上换字体，画完换回来）
            IntPtr hf = bold && _gdiBold.Hfont != IntPtr.Zero ? _gdiBold.Hfont : IntPtr.Zero;
            if (hf != IntPtr.Zero)
            {
                IntPtr old = Native.SelectObject(hdc, hf);
                Native.TextOutW(hdc, x, y, s, s.Length);
                Native.SelectObject(hdc, old);
            }
            else Native.TextOutW(hdc, x, y, s, s.Length);
            return x + (bold ? _gdiBold.Width(s) : _gdi.Width(s));
        }

        private int MeasureText(string s, Font font, int maxW)
        {
            return MeasureText(s, font, maxW, _rowH);
        }

        private static int MeasureText(string s, Font font, int maxW, int h)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var sz = TextRenderer.MeasureText(s, font,
                new Size(maxW > 0 ? maxW : int.MaxValue, Math.Max(8, h)),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return sz.Width;
        }

        // 超过 maxW 用 ... 省略；prefixLen = 还能完整显示几个字符（超出范围的命中不再上色）。
        public string Truncate(string name, int maxW, out int prefixLen)
        {
            prefixLen = name == null ? 0 : name.Length;
            if (string.IsNullOrEmpty(name)) return "";
            if (_gdi.Width(name) <= maxW) return name;

            int budget = Math.Max(0, maxW - _gdi.Width(Ellipsis));
            int cut = Math.Min(name.Length, Math.Max(0, budget / Math.Max(1, _charW)));
            while (cut > 0 && _gdi.Width(name.Substring(0, cut)) > budget) cut--;
            if (cut == 0 && name.Length > 0) cut = 1;

            prefixLen = cut;
            return name.Substring(0, cut) + Ellipsis;
        }

        // 五档图标全部现画（不发位图）；拿不到档位就不画，选中行统一改白。
        private void DrawIcon(Graphics g, string kind, int x, int y, int w, bool selected)
        {
            if (string.IsNullOrEmpty(kind)) return;
            Color color;
            if (!IconColor.TryGetValue(kind, out color)) return;
            if (selected) color = CSelFg;

            using (var b = new SolidBrush(color))
            using (var pen = new Pen(color))
            {
                switch (kind)
                {
                    case "func":   // 绿箭头（VBE 的方法图标就是一支箭头）
                        {
                            var pts = new[]
                            {
                                new Point(x, y + w / 4),
                                new Point(x + w / 2, y + w / 4),
                                new Point(x + w / 2, y),
                                new Point(x + w, y + w / 2),
                                new Point(x + w / 2, y + w),
                                new Point(x + w / 2, y + w * 3 / 4),
                                new Point(x, y + w * 3 / 4),
                            };
                            g.FillPolygon(b, pts);
                            break;
                        }
                    case "prop":   // 紫色实心方块
                        g.FillRectangle(b, x + 1, y + w / 4, w - 2, w / 2);
                        break;
                    case "type":   // 琥珀菱形
                        {
                            var pts = new[]
                            {
                                new Point(x + w / 2, y),
                                new Point(x + w, y + w / 2),
                                new Point(x + w / 2, y + w),
                                new Point(x, y + w / 2),
                            };
                            g.FillPolygon(b, pts);
                            break;
                        }
                    case "kw":     // 灰色横线
                        g.FillRectangle(b, x + 1, y + w / 2 - 1, w - 2, 2);
                        break;
                    default:       // var：蓝色小竖条
                        g.FillRectangle(b, x + w / 3, y + 1, Math.Max(2, w / 4), w - 2);
                        break;
                }
            }
        }

        // ---- 鼠标 ----

        private int RowAt(int y)
        {
            if (y < 0 || y >= ListHeight) return -1;
            int i = y / Math.Max(1, _rowH);
            int idx = _top + i;
            return idx < _items.Count ? idx : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragScroll)
            {
                int listH = ListHeight;
                int total = _items.Count;
                int maxTop = Math.Max(0, total - _rows);
                if (maxTop > 0 && listH > 0)
                {
                    int thumbH = Math.Max(16, (int)((long)listH * Math.Min(_rows, total) / total));
                    int span = Math.Max(1, listH - thumbH);
                    int y = Math.Max(0, Math.Min(span, e.Y - _dragOffset));
                    int top = (int)((long)y * maxTop / span);
                    ScrollTo?.Invoke(top);
                }
                return;
            }

            // 滚动条区域不参与悬停高亮
            int hover = e.X >= Width - ScrollW && _items.Count > _rows ? -1 : RowAt(e.Y);
            if (hover != _hover)
            {
                _hover = hover;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _dragScroll = false;
            if (_hover != -1) { _hover = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int listH = ListHeight;
            if (e.X >= Width - ScrollW && _items.Count > _rows && e.Y < listH)
            {
                int total = _items.Count;
                int thumbH = Math.Max(16, (int)((long)listH * Math.Min(_rows, total) / total));
                int maxTop = Math.Max(1, total - _rows);
                int thumbY = (int)((long)(listH - thumbH) * Math.Min(_top, maxTop) / maxTop);
                if (e.Y >= thumbY && e.Y <= thumbY + thumbH)
                {
                    _dragScroll = true;
                    _dragOffset = e.Y - thumbY;
                    // 必须取鼠标捕获，否则拖出窗口松手收不到 MouseUp，_dragScroll 永远卡在 true。
                    Capture = true;
                }
                else
                {
                    Wheel?.Invoke(e.Y < thumbY ? -PageRows : PageRows);
                }
                return;
            }

            int i = RowAt(e.Y);
            if (i >= 0) RowClicked?.Invoke(i);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragScroll = false;
            if (Capture) Capture = false;      // 与 OnMouseDown 的 Capture 成对
        }

        private int PageRows => Math.Max(1, _rows - 1);

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Wheel?.Invoke(e.Delta > 0 ? -3 : 3);
        }

        // 先定位再显示，否则首次 Show 会在屏幕左上角闪一下（Manual 且没设过 Location）。
        public void ShowAt(int x, int top, int bottom)
        {
            PlaceAt(x, top, bottom);       // 内部会碰 Handle，句柄在这里建出来
            if (!Visible) Show();
        }

        // 预热：把首次弹窗的隐性开销（建句柄 / 建字体 / 首帧自绘）提前付掉，屏幕外走一帧再藏。
        public void WarmUp()
        {
            try
            {
                if (Handle == IntPtr.Zero) return;      // 句柄建过一次就够
                using (var g = CreateGraphics())
                    g.MeasureString("WarmUp", _font);   // GDI+ 首次排版（要加载字体族）
            }
            catch { }

            // 首帧自绘（有候选与空列表画法不同，得走一次真的）
            try
            {
                var keep = _items; _lastX = _lastY = int.MinValue;
                SetData(new[]
                {
                    new Candidate
                    {
                        Name = "WarmUp", Hits = new[] { 0, 1 }, HitKind = 3,
                        Span = 2, ScopeKind = 3,
                    },
                }, 0, 0, 1);
                var sz = DesiredSize();
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, -32000, -32000,
                    sz.Width, sz.Height, Native.SWP_NOACTIVATE);
                if (!Visible) Show();
                Update();                                // 立刻把 WM_PAINT 走完
                Hide();
                _items = keep;
                ResetPlacement();
            }
            catch { try { Hide(); } catch { } }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 字体是本类自己 new 的，不释放就每个实例漏一个 HFONT（_fontBold 可能就是 _font）。
                if (!ReferenceEquals(_fontBold, _font)) _fontBold?.Dispose();
                _font?.Dispose();
                _gdi.Dispose();
                _gdiBold.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            const int WM_MOUSEACTIVATE = 0x0021;
            const int MA_NOACTIVATE = 3;
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }

        // 候选窗一帧要画几十段文字。TextRenderer 每调一次都会自己 GetHdc/ReleaseHdc，
        // 一帧 1.7ms 里绝大部分就花在这个来回上。这里自己持一个内存 DC 量宽，
        // 绘制时用 OnPaint 传进来的窗口 DC，整帧只选一次字体。
        private sealed class GdiText : IDisposable
        {
            private readonly IntPtr _dc;
            private IntPtr _hfont, _old;
            private readonly Dictionary<string, int> _w =
                new Dictionary<string, int>(512, StringComparer.Ordinal);
            // > 0 表示等宽字体：宽度就是 长度 × MonoW，连量都不用量
            public int MonoW { get; private set; }

            public GdiText()
            {
                _dc = Native.CreateCompatibleDC(IntPtr.Zero);
                Native.SetBkMode(_dc, Native.TRANSPARENT);
            }

            public IntPtr Hfont => _hfont;

            public void UseFont(Font f)
            {
                if (_hfont != IntPtr.Zero)
                {
                    Native.SelectObject(_dc, _old);
                    Native.DeleteObject(_hfont);
                    _hfont = IntPtr.Zero;
                }
                MonoW = 0;
                if (f == null || _dc == IntPtr.Zero) return;
                try
                {
                    IntPtr hf = f.ToHfont();
                    if (hf == IntPtr.Zero) return;
                    IntPtr old = Native.SelectObject(_dc, hf);
                    if (old == IntPtr.Zero || old == (IntPtr)(-1)) { Native.DeleteObject(hf); return; }
                    _hfont = hf;
                    _old = old;
                }
                catch { return; }   // 字型拿不到就退回无字状态，绝不把绘制整条打断
                // 四个样本宽度全等才认定等宽：i / M / x / 0 在比例字体里一定不等
                int wi = Raw("i"), wm = Raw("M"), wx = Raw("x"), w0 = Raw("0");
                if (wi > 0 && wi == wm && wm == wx && wx == w0) MonoW = wi;
            }

            public void ResetCache() => _w.Clear();

            private int Raw(string s)
            {
                Native.SIZE sz;
                Native.GetTextExtentPoint32W(_dc, s, s.Length, out sz);
                return sz.cx;
            }

            // 等宽只对纯 ASCII 成立：中文标识符会掉进回退字体，宽度不是 MonoW 的整数倍。
            private static bool Ascii(string s)
            {
                for (int i = 0; i < s.Length; i++) if (s[i] > 0x7E) return false;
                return true;
            }

            public int Width(string s)
            {
                if (string.IsNullOrEmpty(s)) return 0;
                if (MonoW > 0 && Ascii(s)) return s.Length * MonoW;
                int w;
                if (_w.TryGetValue(s, out w)) return w;
                w = Raw(s);
                if (_w.Count >= 8192) _w.Clear();
                _w[s] = w;
                return w;
            }

            public void Dispose()
            {
                if (_hfont != IntPtr.Zero)
                {
                    Native.SelectObject(_dc, _old);
                    Native.DeleteObject(_hfont);
                    _hfont = IntPtr.Zero;
                }
                if (_dc != IntPtr.Zero) Native.DeleteDC(_dc);
            }
        }
    }
}

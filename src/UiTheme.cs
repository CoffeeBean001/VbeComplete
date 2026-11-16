// 集中创建等于把释放责任藏起来。

using System;
using System.Drawing;
using System.Windows.Forms;

namespace VbeComplete
{
    internal static class UiTheme
    {
        public static readonly Color Body = Hex("#F5F7FA");
        public static readonly Color Card = Hex("#FFFFFF");
        public static readonly Color Line = Hex("#DCE3EC");
        public static readonly Color Text = Hex("#1F2937");
        public static readonly Color Muted = Hex("#6B7280");
        public static readonly Color Accent = Hex("#2563EB");
        public static readonly Color AccentDark = Hex("#1D4ED8");
        public static readonly Color SelectBg = Hex("#E8F0FE");
        public static readonly Color Err = Hex("#B91C1C");
        public static readonly Color Ok = Hex("#15803D");

        public static Color Hex(string s)
        {
            try { return ColorTranslator.FromHtml(s); } catch { return Color.Black; }
        }

        public static Button Primary(string text)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = false,
                FlatStyle = FlatStyle.Flat,
                BackColor = Accent,
                ForeColor = Color.White,
                Size = new Size(96, 32),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = AccentDark;
            return b;
        }

        public static Button Plain(string text, int w = 96)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = false,
                FlatStyle = FlatStyle.Flat,
                BackColor = Card,
                ForeColor = Text,
                Size = new Size(w, 32),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderColor = Line;
            b.FlatAppearance.MouseOverBackColor = SelectBg;
            return b;
        }

        public static CheckBox Check(string text, bool on, bool enabled = true)
            => new CheckBox
            {
                Text = text,
                Checked = on,
                AutoSize = true,
                Enabled = enabled,
                ForeColor = Text,
                // 这边整页都是同一个平色，直接填实最稳。
                BackColor = Body,
                FlatStyle = FlatStyle.System,
            };

        public static Label Label(string text, Color color, Font font = null, bool wrap = false)
        {
            var l = new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = color,
                BackColor = Body,
            };
            if (wrap) l.MaximumSize = new Size(560, 0);
            if (font != null) l.Font = font;
            return l;
        }
    }
}

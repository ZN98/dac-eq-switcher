using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DacEqSwitcher
{
    /// The dark "glass" look: colours, fonts and drawing helpers shared by every control.
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(0x1B, 0x1A, 0x20);
        public static readonly Color Rail = Color.FromArgb(0x1B, 0x1A, 0x20);
        public static readonly Color Line = Color.FromArgb(0x2A, 0x28, 0x32);
        public static readonly Color Card = Color.FromArgb(0x26, 0x24, 0x2D);
        public static readonly Color CardBorder = Color.FromArgb(0x35, 0x32, 0x41);
        public static readonly Color Inset = Color.FromArgb(0x1F, 0x1D, 0x25);
        public static readonly Color Hover = Color.FromArgb(0x34, 0x30, 0x3F);
        public static readonly Color Selected = Color.FromArgb(0x3E, 0x3A, 0x4B);
        public static readonly Color Dashed = Color.FromArgb(0x4A, 0x46, 0x57);
        public static readonly Color Track = Color.FromArgb(0x3A, 0x37, 0x46);
        public static readonly Color Text = Color.FromArgb(0xF3, 0xF1, 0xF7);
        public static readonly Color Muted = Color.FromArgb(0xB9, 0xB5, 0xC4);
        public static readonly Color Accent = Color.FromArgb(0xC3, 0xA6, 0xFF);
        public static readonly Color OnAccent = Color.FromArgb(0x1B, 0x1A, 0x20);
        public static readonly Color Good = Color.FromArgb(0x6E, 0xE7, 0xA0);
        public static readonly Color Warn = Color.FromArgb(0xF2, 0xB2, 0x6B);
        public static readonly Color Bad = Color.FromArgb(0xFF, 0x8F, 0x8F);
        public static readonly Color Loud = Color.FromArgb(0xC7, 0x78, 0x3A);

        static readonly string Family = PickFamily();

        /// Screen scale (1.0 at 96 dpi). The app is system-DPI aware, so one value fits every window.
        public static readonly float Scale = ReadScale();

        static float ReadScale()
        {
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }
            catch { return 1f; }
        }

        static string PickFamily()
        {
            try
            {
                var names = new InstalledFontCollection().Families.Select(f => f.Name).ToList();
                foreach (var want in new[] { "Segoe UI Variable Text", "Segoe UI", "Inter" })
                    if (names.Contains(want)) return want;
            }
            catch { }
            return SystemFonts.MessageBoxFont.FontFamily.Name;
        }

        static readonly System.Collections.Generic.Dictionary<string, Font> fonts = new System.Collections.Generic.Dictionary<string, Font>();

        /// Shared font instances (never dispose them).
        public static Font F(float size, FontStyle style = FontStyle.Regular)
        {
            string key = size + ":" + style;
            Font f;
            if (!fonts.TryGetValue(key, out f)) fonts[key] = f = new Font(Family, size, style, GraphicsUnit.Point);
            return f;
        }

        public static Font Body { get { return F(10f); } }
        public static Font Small { get { return F(9f); } }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, Color c, RectangleF r, float radius)
        {
            using (var b = new SolidBrush(c)) using (var p = Round(r, radius)) g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, Color c, RectangleF r, float radius, float width = 1, bool dashed = false)
        {
            using (var pen = new Pen(c, width)) using (var p = Round(r, radius))
            {
                if (dashed) pen.DashPattern = new[] { 4f, 3f };
                g.DrawPath(pen, p);
            }
        }

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        public static void DrawText(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, s, f, r, c, flags | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        // ------------------------------------------------------------ Windows bits --
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string idList);

        /// Dark title bar (Windows 10 20H1+ and 11).
        public static void DarkTitleBar(Form f)
        {
            if (!Native.OnWindows) return;
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
                int caption = ColorTranslator.ToWin32(Bg);
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);   // caption colour (Windows 11)
            }
            catch { }
        }

        /// Dark scroll bars for standard text boxes and lists.
        public static void DarkScrollBars(Control c)
        {
            if (!Native.OnWindows) return;
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
        }

        public static ToolStripRenderer MenuRenderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false };

        public static ContextMenuStrip Menu()
        {
            return new ContextMenuStrip { Renderer = MenuRenderer, BackColor = Card, ForeColor = Text, ShowImageMargin = false, Font = Body };
        }

        sealed class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Card; } }
            public override Color MenuBorder { get { return CardBorder; } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color MenuItemPressedGradientBegin { get { return Selected; } }
            public override Color MenuItemPressedGradientEnd { get { return Selected; } }
            public override Color ImageMarginGradientBegin { get { return Card; } }
            public override Color ImageMarginGradientMiddle { get { return Card; } }
            public override Color ImageMarginGradientEnd { get { return Card; } }
            public override Color SeparatorDark { get { return CardBorder; } }
            public override Color SeparatorLight { get { return CardBorder; } }
            public override Color CheckBackground { get { return Selected; } }
            public override Color CheckSelectedBackground { get { return Selected; } }
            public override Color CheckPressedBackground { get { return Selected; } }
        }
    }

    /// Icons drawn as strokes, matching the design (16-unit grid, scaled to the box).
    static class Icons
    {
        public delegate void Draw(Graphics g, Pen p);

        public static void Paint(Graphics g, Rectangle box, Color c, Draw d, float stroke = 1.3f)
        {
            var state = g.Save();
            float s = Math.Min(box.Width, box.Height) / 16f;
            g.TranslateTransform(box.X + (box.Width - 16 * s) / 2, box.Y + (box.Height - 16 * s) / 2);
            g.ScaleTransform(s, s);
            using (var p = new Pen(c, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) d(g, p);
            g.Restore(state);
        }

        public static readonly Draw Home = (g, p) => g.DrawLines(p, new[] { new PointF(2, 7), new PointF(8, 2), new PointF(14, 7), new PointF(14, 14), new PointF(10, 14), new PointF(10, 10), new PointF(6, 10), new PointF(6, 14), new PointF(2, 14), new PointF(2, 7) });
        public static readonly Draw Wave = (g, p) =>
        {
            using (var path = new GraphicsPath())
            {
                path.AddBezier(1.5f, 10, 3.5f, 4, 5, 4, 6.5f, 10);
                path.AddBezier(6.5f, 10, 8, 16, 9.5f, 16, 11.5f, 10);
                path.AddBezier(11.5f, 10, 12.5f, 7, 13.5f, 6.5f, 14.5f, 7);
                g.DrawPath(p, path);
            }
        };
        public static readonly Draw Rules = (g, p) => { g.DrawLine(p, 3, 4, 13, 4); g.DrawLine(p, 3, 8, 13, 8); g.DrawLine(p, 3, 12, 9, 12); };
        public static readonly Draw Gear = (g, p) =>
        {
            g.DrawEllipse(p, 5.8f, 5.8f, 4.4f, 4.4f);
            foreach (var a in new[] { 0, 45, 90, 135, 180, 225, 270, 315 })
            {
                double r = a * Math.PI / 180;
                g.DrawLine(p, 8 + (float)(4.6 * Math.Cos(r)), 8 + (float)(4.6 * Math.Sin(r)), 8 + (float)(6.4 * Math.Cos(r)), 8 + (float)(6.4 * Math.Sin(r)));
            }
        };
        public static readonly Draw Power = (g, p) => { g.DrawLine(p, 8, 2, 8, 7.5f); g.DrawArc(p, 2.7f, 3.2f, 10.6f, 10.6f, -50, 280); };
        public static readonly Draw Speaker = (g, p) =>
        {
            g.DrawPolygon(p, new[] { new PointF(2, 6), new PointF(5, 6), new PointF(9, 3), new PointF(9, 13), new PointF(5, 10), new PointF(2, 10) });
            g.DrawArc(p, 8.5f, 5.5f, 4, 5, -60, 120);
        };
        public static readonly Draw Muted = (g, p) =>
        {
            g.DrawPolygon(p, new[] { new PointF(2, 6), new PointF(5, 6), new PointF(9, 3), new PointF(9, 13), new PointF(5, 10), new PointF(2, 10) });
            g.DrawLine(p, 11.5f, 6, 14.5f, 10); g.DrawLine(p, 14.5f, 6, 11.5f, 10);
        };
        public static readonly Draw Plus = (g, p) => { g.DrawLine(p, 8, 3, 8, 13); g.DrawLine(p, 3, 8, 13, 8); };
        public static readonly Draw Minus = (g, p) => g.DrawLine(p, 3, 8, 13, 8);
        public static readonly Draw Chevron = (g, p) => g.DrawLines(p, new[] { new PointF(4.5f, 6.5f), new PointF(8, 10), new PointF(11.5f, 6.5f) });
        public static readonly Draw Up = (g, p) => g.DrawLines(p, new[] { new PointF(4.5f, 10), new PointF(8, 6.5f), new PointF(11.5f, 10) });
        public static readonly Draw Down = Chevron;
        public static readonly Draw Check = (g, p) => g.DrawLines(p, new[] { new PointF(4, 8.3f), new PointF(6.8f, 11), new PointF(12, 5.5f) });
    }
}

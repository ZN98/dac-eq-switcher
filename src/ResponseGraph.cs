using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DacEqSwitcher
{
    /// Frequency-response plot of an EQ (left/right).
    sealed class ResponseGraph : Control
    {
        EqText.Response data;

        public ResponseGraph()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = SystemColors.Window;
        }

        public EqText.Response Data { get { return data; } set { data = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            float sc = g.DpiX / 96f;
            float L = 34 * sc, T = 8 * sc, Rm = 8 * sc, B = 18 * sc;
            float W = Width - L - Rm, H = Height - T - B;
            if (W < 20 || H < 20) return;

            double lo = -18, hi = 6;
            if (data != null)
            {
                foreach (var arr in new[] { data.L, data.R })
                    foreach (var v in arr) { lo = Math.Min(lo, Math.Floor(v / 6) * 6); hi = Math.Max(hi, Math.Ceiling(v / 6) * 6); }
                lo = Math.Max(lo, -60); hi = Math.Min(hi, 30);
            }
            Func<double, float> X = f => L + W * (float)(Math.Log10(f / 20) / 3);
            Func<double, float> Y = db => T + H * (float)((hi - Math.Max(lo, Math.Min(hi, db))) / (hi - lo));

            using (var grid = new Pen(Color.FromArgb(60, ForeColor)))
            using (var zero = new Pen(Color.FromArgb(150, ForeColor)))
            using (var txt = new SolidBrush(Color.FromArgb(150, ForeColor)))
            using (var font = new Font(Font.FontFamily, 7.5f))
            {
                int step = hi - lo > 36 ? 12 : 6;
                for (double db = lo; db <= hi + 0.01; db += step)
                {
                    g.DrawLine(db == 0 ? zero : grid, L, Y(db), L + W, Y(db));
                    var s = (db > 0 ? "+" : "") + db.ToString("0");
                    var sz = g.MeasureString(s, font);
                    g.DrawString(s, font, txt, L - sz.Width - 2, Y(db) - sz.Height / 2);
                }
                foreach (var f in new[] { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
                {
                    g.DrawLine(grid, X(f), T, X(f), T + H);
                    var s = f >= 1000 ? (f / 1000) + "k" : f.ToString();
                    var sz = g.MeasureString(s, font);
                    g.DrawString(s, font, txt, Math.Min(X(f) - sz.Width / 2, L + W - sz.Width), T + H + 1);
                }
            }
            if (data == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Draw(g, data.L, Color.FromArgb(37, 99, 235), X, Y, sc);
            if (!data.Identical) Draw(g, data.R, Color.FromArgb(220, 60, 50), X, Y, sc);
        }

        void Draw(Graphics g, double[] v, Color c, Func<double, float> X, Func<double, float> Y, float sc)
        {
            var pts = new List<PointF>(v.Length);
            for (int i = 0; i < v.Length; i++) pts.Add(new PointF(X(data.Freqs[i]), Y(v[i])));
            using (var p = new Pen(c, 2 * sc)) g.DrawLines(p, pts.ToArray());
        }
    }
}

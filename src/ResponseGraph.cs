using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DacEqSwitcher
{
    /// Frequency-response plot of an EQ (left/right), optionally with a second, dashed curve
    /// (the EQ with loudness compensation added).
    sealed class ResponseGraph : Control
    {
        EqText.Response data, overlay;
        public bool Simple;   // Home: no grid labels, zero line only

        public ResponseGraph()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            ForeColor = Theme.Muted;
            Font = Theme.F(7.5f);
        }

        public EqText.Response Data { get { return data; } set { data = value; Invalidate(); } }
        public EqText.Response Overlay { get { return overlay; } set { overlay = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float sc = g.DpiX / 96f;
            float L = (Simple ? 4 : 34) * sc, T = 8 * sc, Rm = (Simple ? 4 : 8) * sc, B = (Simple ? 6 : 18) * sc;
            float W = Width - L - Rm, H = Height - T - B;
            if (W < 20 || H < 20) return;

            // Home: one line per curve (left/right averaged); the loudness curve is lined up with
            // the EQ at 1 kHz, so its anti-clipping preamp doesn't make it look quieter
            double[] eqMean = Simple && data != null ? Mean(data) : null, ovMean = null;
            if (Simple && overlay != null)
            {
                ovMean = Mean(overlay);
                if (eqMean != null)
                {
                    int k = Nearest(overlay.Freqs, 1000);
                    double shift = eqMean[k] - ovMean[k];
                    for (int i = 0; i < ovMean.Length; i++) ovMean[i] += shift;
                }
            }
            var series = Simple ? new[] { eqMean, ovMean } : new[] { data == null ? null : data.L, data == null ? null : data.R, overlay == null ? null : overlay.L, overlay == null ? null : overlay.R };
            double lo = -18, hi = 6;
            foreach (var arr in series)
                if (arr != null)
                    foreach (var v in arr) { lo = Math.Min(lo, Math.Floor(v / 6) * 6); hi = Math.Max(hi, Math.Ceiling(v / 6) * 6); }
            lo = Math.Max(lo, -60); hi = Math.Min(hi, 30);
            Func<double, float> X = f => L + W * (float)(Math.Log10(f / 20) / 3);
            Func<double, float> Y = db => T + H * (float)((hi - Math.Max(lo, Math.Min(hi, db))) / (hi - lo));

            using (var grid = new Pen(Theme.Line))
            using (var zero = new Pen(Theme.Track) { DashPattern = new[] { 3f, 5f } })
            using (var txt = new SolidBrush(ForeColor))
            {
                if (Simple) g.DrawLine(zero, L, Y(0), L + W, Y(0));
                else
                {
                    int step = hi - lo > 36 ? 12 : 6;
                    for (double db = lo; db <= hi + 0.01; db += step)
                    {
                        g.DrawLine(db == 0 ? zero : grid, L, Y(db), L + W, Y(db));
                        var s = (db > 0 ? "+" : "") + db.ToString("0");
                        var sz = g.MeasureString(s, Font);
                        g.DrawString(s, Font, txt, L - sz.Width - 2, Y(db) - sz.Height / 2);
                    }
                    foreach (var f in new[] { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
                    {
                        g.DrawLine(grid, X(f), T, X(f), T + H);
                        var s = f >= 1000 ? (f / 1000) + "k" : f.ToString();
                        var sz = g.MeasureString(s, Font);
                        g.DrawString(s, Font, txt, Math.Min(X(f) - sz.Width / 2, L + W - sz.Width), T + H + 1);
                    }
                }
            }
            if (Simple)
            {
                if (ovMean != null) Draw(g, overlay, ovMean, Color.FromArgb(150, Theme.Accent), X, Y, sc, true);
                if (eqMean != null) Draw(g, data, eqMean, Theme.Accent, X, Y, sc, false);
                return;
            }
            if (overlay != null) Draw(g, overlay, overlay.L, Color.FromArgb(150, Theme.Accent), X, Y, sc, true);
            if (data == null) return;
            Draw(g, data, data.L, Theme.Accent, X, Y, sc, false);
            if (!data.Identical) Draw(g, data, data.R, Color.FromArgb(0x7D, 0xD3, 0xFC), X, Y, sc, false);
        }

        static double[] Mean(EqText.Response r)
        {
            var m = new double[r.L.Length];
            for (int i = 0; i < m.Length; i++) m[i] = (r.L[i] + r.R[i]) / 2;
            return m;
        }

        static int Nearest(double[] f, double hz)
        {
            int best = 0;
            for (int i = 1; i < f.Length; i++) if (Math.Abs(f[i] - hz) < Math.Abs(f[best] - hz)) best = i;
            return best;
        }

        void Draw(Graphics g, EqText.Response r, double[] v, Color c, Func<double, float> X, Func<double, float> Y, float sc, bool dashed)
        {
            var pts = new List<PointF>(v.Length);
            for (int i = 0; i < v.Length; i++) pts.Add(new PointF(X(r.Freqs[i]), Y(v[i])));
            using (var p = new Pen(c, (dashed ? 2f : 2.5f) * sc))
            {
                if (dashed) p.DashPattern = new[] { 3f, 2.5f };
                g.DrawLines(p, pts.ToArray());
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace DacEqSwitcher
{
    /// A volume slider whose positions are exactly the DAC's volume steps (in dB, ascending).
    /// The DX1 II has 1 dB steps below -10 dB and 0.5 dB steps above, so each step gets the
    /// same width: the fine region near full volume is as easy to hit as the rest.
    /// Drag, click, mouse wheel (one step), arrow keys (one step), Page Up/Down (five steps).
    sealed class VolumeSlider : Control
    {
        double[] steps = { -99, 0 };
        int index;
        bool dragging;
        public double Warn = double.NaN;   // steps above this are drawn as the loud zone

        public event Action Changing;      // every change while the user moves it
        public event Action Committed;     // drag released, or a key/wheel/button step

        public VolumeSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Height = 46;
            Cursor = Cursors.Hand;
        }

        public bool Dragging { get { return dragging; } }

        public double[] Steps
        {
            get { return steps; }
            set
            {
                if (value == null || value.Length < 2 || value.SequenceEqual(steps)) return;
                double v = Value;
                steps = value;
                index = Nearest(v);
                Invalidate();
            }
        }

        public double Value
        {
            get { return steps[index]; }
            set { int i = Nearest(value); if (i != index) { index = i; Invalidate(); } }
        }

        public int Nearest(double db)
        {
            int best = 0;
            for (int i = 1; i < steps.Length; i++) if (Math.Abs(steps[i] - db) < Math.Abs(steps[best] - db)) best = i;
            return best;
        }

        /// Moves by n steps as a user action (buttons, wheel, keys).
        public void Step(int n)
        {
            int i = Math.Max(0, Math.Min(steps.Length - 1, index + n));
            if (i == index) return;
            index = i;
            Invalidate();
            Fire(true);
        }

        void Fire(bool commit)
        {
            var h = Changing; if (h != null) h();
            if (commit) { var c = Committed; if (c != null) c(); }
        }

        // ------------------------------------------------------------- layout --
        float scale = 1;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            Height = (int)(46 * scale);
        }
        RectangleF Track
        {
            get
            {
                float s = scale, pad = 10 * s;
                return new RectangleF(pad, 12 * s, Width - 2 * pad, 6 * s);
            }
        }
        float X(int i) { var t = Track; return t.Left + t.Width * i / (float)(steps.Length - 1); }
        int IndexAt(float x)
        {
            var t = Track;
            float f = (x - t.Left) / t.Width;
            return Math.Max(0, Math.Min(steps.Length - 1, (int)Math.Round(f * (steps.Length - 1))));
        }

        // -------------------------------------------------------------- input --
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            Focus();
            dragging = true;
            Capture = true;
            MoveTo(IndexAt(e.X));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) MoveTo(IndexAt(e.X));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            dragging = false;
            Capture = false;
            Invalidate();
            var c = Committed; if (c != null) c();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (dragging && !Capture) { dragging = false; var c = Committed; if (c != null) c(); }
        }

        void MoveTo(int i)
        {
            if (i == index) return;
            index = i;
            Invalidate();
            Fire(false);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Enabled) Step(Math.Sign(e.Delta));
            var h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;   // don't scroll the page as well
        }

        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: Step(-1); break;
                case Keys.Right: case Keys.Up: Step(1); break;
                case Keys.PageDown: Step(-5); break;
                case Keys.PageUp: Step(5); break;
                case Keys.Home: Step(-steps.Length); break;
                default: return;
            }
            e.Handled = true;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        // -------------------------------------------------------------- paint --
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : SystemColors.Control);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = g.DpiX / 96f;
            var t = Track;
            Color accent = Enabled ? Color.FromArgb(37, 99, 235) : SystemColors.GrayText;
            Color rail = Color.FromArgb(Enabled ? 70 : 40, ForeColor);

            using (var b = new SolidBrush(rail)) FillRound(g, b, t);
            if (!double.IsNaN(Warn))
            {
                int w = Array.FindIndex(steps, v => v > Warn);
                if (w > 0)
                    using (var b = new SolidBrush(Color.FromArgb(Enabled ? 90 : 40, 217, 120, 40)))
                        FillRound(g, b, new RectangleF(X(w - 1), t.Top, t.Right - X(w - 1), t.Height));
            }
            float x = X(index);
            using (var b = new SolidBrush(accent)) FillRound(g, b, new RectangleF(t.Left, t.Top, Math.Max(t.Height, x - t.Left), t.Height));

            // dB marks at their real positions
            using (var tick = new Pen(Color.FromArgb(110, ForeColor)))
            using (var txt = new SolidBrush(Color.FromArgb(Enabled ? 150 : 90, ForeColor)))
            using (var font = new Font(Font.FontFamily, 7.5f))
            {
                foreach (var db in new[] { -90.0, -60, -40, -30, -20, -10, -5, 0 })
                {
                    int i = Array.IndexOf(steps, db);
                    if (i < 0) continue;
                    float mx = X(i);
                    g.DrawLine(tick, mx, t.Bottom + 3 * s, mx, t.Bottom + 7 * s);
                    string lab = db == 0 ? "0" : db.ToString("0");
                    var sz = g.MeasureString(lab, font);
                    g.DrawString(lab, font, txt, Math.Max(0, Math.Min(Width - sz.Width, mx - sz.Width / 2)), t.Bottom + 8 * s);
                }
            }

            float r = (dragging ? 9 : 8) * s, cy = t.Top + t.Height / 2;
            using (var b = new SolidBrush(SystemColors.Window)) g.FillEllipse(b, x - r, cy - r, 2 * r, 2 * r);
            using (var p = new Pen(accent, 2.5f * s)) g.DrawEllipse(p, x - r, cy - r, 2 * r, 2 * r);
            if (Focused)
                using (var p = new Pen(Color.FromArgb(120, accent), 1.5f * s)) g.DrawEllipse(p, x - r - 3 * s, cy - r - 3 * s, 2 * r + 6 * s, 2 * r + 6 * s);
        }

        static void FillRound(Graphics g, Brush b, RectangleF r)
        {
            float d = r.Height;
            if (r.Width < d) { g.FillEllipse(b, r.Left, r.Top, d, d); return; }
            using (var p = new GraphicsPath())
            {
                p.AddArc(r.Left, r.Top, d, d, 90, 180);
                p.AddArc(r.Right - d, r.Top, d, d, 270, 180);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }
    }
}

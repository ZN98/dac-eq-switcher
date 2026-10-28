using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace DacEqSwitcher
{
    /// Rounded card: everything on Home sits on one of these.
    class Card : Panel
    {
        public Color Fill = Theme.Card, Border = Theme.CardBorder;
        public float Radius = 14;
        public bool DashedBorder;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Padding = new Padding(18);
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            Inherit(e.Control);
        }

        void Inherit(Control c)
        {
            if (c is Card) return;
            if (c.BackColor == Theme.Bg || c.BackColor == SystemColors.Control || c.BackColor == Color.Transparent) c.BackColor = Fill;
            foreach (Control k in c.Controls) Inherit(k);
            c.ControlAdded += (s, e) => Inherit(e.Control);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Theme.Smooth(g);
            float s = Theme.Scale;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.Fill(g, Fill, r, Radius * s);
            Theme.Stroke(g, Border, r, Radius * s, 1, DashedBorder);
        }
    }

    /// Flat button in the dark style.
    class Btn : Button
    {
        public enum Kinds { Normal, Ghost, Accent, Dashed, Inset, Link }
        Kinds kind = Kinds.Normal;
        bool hover, down;
        public Icons.Draw Icon;
        public bool IconOnly;
        public float Radius = 8;
        public Color? TextColor;

        public Btn(string text = "", Kinds k = Kinds.Normal)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Text = text;
            kind = k;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = Theme.Body;
            ForeColor = Theme.Text;
            Cursor = Cursors.Hand;
            Height = 34;
            Margin = new Padding(0);
            UseVisualStyleBackColor = false;
        }

        [DefaultValue(Kinds.Normal)]
        public Kinds Kind { get { return kind; } set { if (kind != value) { kind = value; Invalidate(); } } }

        /// For subclasses that pick their look while painting (no repaint request).
        protected void UseKind(Kinds k) { kind = k; }

        public Btn WithIcon(Icons.Draw icon, bool only = false) { Icon = icon; IconOnly = only; return this; }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override bool ShowFocusCues { get { return true; } }

        public override Size GetPreferredSize(Size proposed)
        {
            float s = Theme.Scale;
            if (IconOnly) return new Size((int)(34 * s), (int)(34 * s));
            var t = TextRenderer.MeasureText(Text, Font);
            int icon = Icon != null ? (int)(22 * s) : 0;
            return new Size(t.Width + icon + (int)((kind == Kinds.Link ? 4 : 28) * s), (int)(34 * s));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var parentBg = Parent != null ? Parent.BackColor : Theme.Bg;
            g.Clear(parentBg);
            Theme.Smooth(g);
            float s = Theme.Scale;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill = Color.Empty, border = Color.Empty, text = TextColor ?? Theme.Text;
            switch (kind)
            {
                case Kinds.Normal: fill = down ? Theme.Selected : hover ? Theme.Hover : Theme.Card; border = Theme.CardBorder; break;
                case Kinds.Inset: fill = down ? Theme.Selected : hover ? Theme.Hover : Theme.Inset; border = Theme.CardBorder; break;
                case Kinds.Ghost: fill = down ? Theme.Selected : hover ? Theme.Hover : Color.Empty; break;
                case Kinds.Accent: fill = down ? ControlPaint.Dark(Theme.Accent, 0.1f) : hover ? ControlPaint.Light(Theme.Accent, 0.2f) : Theme.Accent; text = Theme.OnAccent; break;
                case Kinds.Dashed: fill = hover ? Theme.Hover : Color.Empty; border = Theme.Dashed; break;
                case Kinds.Link: text = hover ? ControlPaint.Light(Theme.Accent, 0.4f) : Theme.Accent; break;
            }
            if (!Enabled) { text = Color.FromArgb(110, text); if (fill != Color.Empty) fill = Color.FromArgb(140, fill); }
            if (fill != Color.Empty) Theme.Fill(g, fill, r, Radius * s);
            if (border != Color.Empty) Theme.Stroke(g, border, r, Radius * s, 1, kind == Kinds.Dashed);
            if (Focused && ShowFocusCues && kind != Kinds.Link)
                Theme.Stroke(g, Color.FromArgb(160, Theme.Accent), new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), Radius * s, 1.5f);

            if (IconOnly && Icon != null)
            {
                int sz = (int)(Math.Min(Width, Height) * 0.5f);
                Icons.Paint(g, new Rectangle((Width - sz) / 2, (Height - sz) / 2, sz, sz), text, Icon, 1.4f);
                return;
            }
            var ts = TextRenderer.MeasureText(Text, Font);
            int iconW = Icon != null ? (int)(22 * s) : 0;
            int x = kind == Kinds.Link ? 0 : (Width - ts.Width - iconW) / 2;
            if (Icon != null)
            {
                int sz = (int)(14 * s);
                Icons.Paint(g, new Rectangle(x, (Height - sz) / 2, sz, sz), text, Icon, 1.6f);
                x += iconW;
            }
            Theme.DrawText(g, Text, Font, text, new Rectangle(x, 0, Width - x, Height), TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }

    /// On/off switch.
    class Switch : CheckBox
    {
        public Switch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand;
            AutoSize = false;
            Size = new Size(44, 24);
            Margin = new Padding(0);
        }

        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Theme.Smooth(g);
            float h = Height - 2, w = Width - 2;
            var r = new RectangleF(1, 1, w, h);
            if (Checked) Theme.Fill(g, Enabled ? Theme.Accent : Color.FromArgb(120, Theme.Accent), r, h / 2);
            else { Theme.Fill(g, Theme.Inset, r, h / 2); Theme.Stroke(g, Theme.Dashed, r, h / 2, 1.2f); }
            float d = h - 6;
            float x = Checked ? r.Right - 3 - d : r.Left + 3;
            using (var b = new SolidBrush(Checked ? Theme.OnAccent : Theme.Muted)) g.FillEllipse(b, x, r.Top + 3, d, d);
            if (Focused) Theme.Stroke(g, Color.FromArgb(150, Theme.Text), new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), h / 2, 1);
        }
    }

    /// A row of mutually exclusive choices ("USB | Optical").
    class Segmented : Control
    {
        readonly List<string> items = new List<string>();
        int selected = -1, hover = -1;
        public event Action<int> Committed;

        public Segmented(params string[] options)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            items.AddRange(options);
            Height = 40;
            Font = Theme.Body;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public void SetItems(IEnumerable<string> opts) { items.Clear(); items.AddRange(opts); Invalidate(); }
        public int SelectedIndex { get { return selected; } set { if (value != selected) { selected = value; Invalidate(); } } }
        public int Count { get { return items.Count; } }

        RectangleF Slot(int i)
        {
            float pad = 3 * Theme.Scale, w = (Width - 2 * pad - (items.Count - 1) * pad) / Math.Max(1, items.Count);
            return new RectangleF(pad + i * (w + pad), pad, w, Height - 2 * pad);
        }

        int At(Point p)
        {
            for (int i = 0; i < items.Count; i++) if (Slot(i).Contains(p)) return i;
            return -1;
        }

        void Pick(int i)
        {
            if (i < 0 || i >= items.Count || !Enabled) return;
            selected = i;
            Invalidate();
            var h = Committed; if (h != null) h(i);
        }

        protected override void OnMouseMove(MouseEventArgs e) { int h = At(e.Location); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e) { Focus(); Pick(At(e.Location)); base.OnMouseClick(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) Pick(Math.Max(0, selected - 1));
            else if (e.KeyCode == Keys.Right) Pick(Math.Min(items.Count - 1, selected + 1));
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Theme.Smooth(g);
            float s = Theme.Scale;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.Fill(g, Theme.Card, r, 10 * s);
            Theme.Stroke(g, Focused ? Color.FromArgb(150, Theme.Accent) : Theme.CardBorder, r, 10 * s);
            for (int i = 0; i < items.Count; i++)
            {
                var slot = Slot(i);
                if (i == selected) Theme.Fill(g, Theme.Selected, slot, 7 * s);
                else if (i == hover && Enabled) Theme.Fill(g, Theme.Hover, slot, 7 * s);
                var c = i == selected ? Theme.Text : Theme.Muted;
                if (!Enabled) c = Color.FromArgb(110, c);
                Theme.DrawText(g, items[i], Font, c, Rectangle.Round(slot), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    /// A button that opens a dark menu of choices.
    class Dropdown : Btn
    {
        readonly List<string> items = new List<string>();
        int selected = -1;
        public event Action<int> Committed;
        public string Placeholder = "—";

        public Dropdown() : base("", Kinds.Normal)
        {
            Radius = 10;
            Height = 40;
            Click += (s, e) => Open();
        }

        public void SetItems(IEnumerable<string> opts) { items.Clear(); items.AddRange(opts); Invalidate(); }
        public int SelectedIndex { get { return selected; } set { selected = value; Invalidate(); } }

        void Open()
        {
            var m = Theme.Menu();
            for (int i = 0; i < items.Count; i++)
            {
                int k = i;
                var it = new ToolStripMenuItem(items[i]) { Checked = i == selected, ForeColor = Theme.Text };
                it.Click += (s, e) => { selected = k; Invalidate(); var h = Committed; if (h != null) h(k); };
                m.Items.Add(it);
            }
            m.Closed += (s, e) => BeginInvoke((Action)m.Dispose);
            m.MinimumSize = new Size(Width, 0);
            m.Show(this, new Point(0, Height + 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            string t = selected >= 0 && selected < items.Count ? items[selected] : Placeholder;
            base.OnPaint(e);   // Text stays empty: the value is drawn below
            var g = e.Graphics;
            float s = Theme.Scale;
            var c = Enabled ? Theme.Text : Color.FromArgb(110, Theme.Text);
            Theme.DrawText(g, t, Font, c, new Rectangle((int)(14 * s), 0, Width - (int)(40 * s), Height), TextFormatFlags.VerticalCenter);
            int sz = (int)(12 * s);
            Icons.Paint(g, new Rectangle(Width - (int)(14 * s) - sz, (Height - sz) / 2, sz, sz), Theme.Muted, Icons.Chevron, 1.6f);
        }
    }

    /// Icon-only button for the side rail.
    class RailButton : Btn
    {
        bool selectedPage;
        public RailButton(string name, Icons.Draw icon) : base("", Kinds.Ghost)
        {
            AccessibleName = name;
            Icon = icon;
            IconOnly = true;
            Size = new Size(44, 44);
            Radius = 10;
            TextColor = Theme.Muted;
            var tip = new ToolTip();
            tip.SetToolTip(this, name);
        }

        public bool Selected
        {
            get { return selectedPage; }
            set { selectedPage = value; UseKind(value ? Kinds.Inset : Kinds.Ghost); TextColor = value ? Theme.Accent : Theme.Muted; Invalidate(); }
        }
    }

    /// One rule on Home: its condition, its EQ, and a "Now" tag when it matches.
    class RuleItem : Btn
    {
        public string Condition = "", Profile = "";
        public bool Active;

        public RuleItem() : base("", Kinds.Ghost)
        {
            Radius = 10;
            Height = 58;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            UseKind(Active ? Kinds.Inset : Kinds.Normal);
            base.OnPaint(e);
            var g = e.Graphics;
            Theme.Smooth(g);
            float s = Theme.Scale;
            if (Active) Theme.Stroke(g, Theme.Accent, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius * s, 1.2f);
            int pad = (int)(14 * s), pill = 0;
            if (Active)
            {
                var f = Theme.F(8f, FontStyle.Bold);
                var ts = TextRenderer.MeasureText("Now", f);
                var pr = new RectangleF(Width - pad - ts.Width - 14 * s, (Height - 20 * s) / 2, ts.Width + 14 * s, 20 * s);
                Theme.Fill(g, Theme.Accent, pr, 8 * s);
                Theme.DrawText(g, "Now", f, Theme.OnAccent, Rectangle.Round(pr), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                pill = (int)(pr.Width + 8 * s);
            }
            int w = Width - 2 * pad - pill;
            Theme.DrawText(g, Condition, Theme.Small, Theme.Muted, new Rectangle(pad, (int)(10 * s), w, (int)(18 * s)), TextFormatFlags.Left);
            Theme.DrawText(g, Profile, Theme.F(11f, FontStyle.Bold), Profile == "No EQ" ? Theme.Muted : Theme.Text,
                           new Rectangle(pad, (int)(28 * s), w, (int)(22 * s)), TextFormatFlags.Left);
        }
    }

    /// Plain label on the dark background.
    static class Ui
    {
        public static Label Text(string t, Font f = null, Color? c = null)
        {
            return new Label { Text = t, AutoSize = true, Font = f ?? Theme.Body, ForeColor = c ?? Theme.Text, BackColor = Color.Transparent, Margin = new Padding(0), UseMnemonic = false };
        }

        public static FlowLayoutPanel Row(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0), BackColor = Color.Transparent };
            f.Controls.AddRange(cs);
            return f;
        }

        public static TableLayoutPanel Column()
        {
            var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), Padding = new Padding(0), BackColor = Color.Transparent };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        public static Control Spacer(int w, int h) { return new Panel { Size = new Size(w, h), Margin = new Padding(0), BackColor = Color.Transparent }; }
    }
}

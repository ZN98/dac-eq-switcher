using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DacEqSwitcher
{
    /// One page of the main window.
    abstract class Page : UserControl
    {
        protected readonly AppCore Core;
        protected readonly MainForm Shell;

        protected Page(AppCore core, MainForm shell)
        {
            Core = core;
            Shell = shell;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            Dock = DockStyle.Fill;
            DoubleBuffered = true;
        }

        /// Called when the DAC, the EQ or the settings change, while the page is showing.
        public abstract void RefreshState();
        public virtual bool CanLeave() { return true; }
        public virtual void Shown() { RefreshState(); }
    }

    /// The window: an icon rail on the left and one page at a time.
    sealed class MainForm : Form
    {
        readonly AppCore core;
        public readonly TrayApp App;
        public readonly DacActions Actions;
        readonly Panel host;
        readonly RailButton[] rail;
        readonly Page[] pages = new Page[4];
        int current = -1;

        public MainForm(AppCore core, TrayApp app)
        {
            this.core = core;
            App = app;
            Actions = new DacActions(core, System.Threading.SynchronizationContext.Current);
            Text = Program.Name;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1100, 720);
            MinimumSize = new Size(940, 660);
            KeyPreview = true;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(28, 20, 28, 16) };
            var railPanel = new RailPanel { Dock = DockStyle.Left, Width = 64 };
            rail = new[]
            {
                new RailButton("Home", Icons.Home), new RailButton("EQ profiles", Icons.Wave),
                new RailButton("Rules", Icons.Rules), new RailButton("Settings", Icons.Gear),
            };
            for (int i = 0; i < rail.Length; i++)
            {
                int k = i;
                rail[i].Click += (s, e) => ShowPage(k);
                railPanel.Controls.Add(rail[i]);
            }
            railPanel.Resize += (s, e) => LayoutRail(railPanel);
            Controls.Add(host);
            Controls.Add(railPanel);
            // build every page now, so the form's DPI scaling covers them all
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i] = Create(i);
                pages[i].Visible = false;
                host.Controls.Add(pages[i]);
            }

            KeyDown += (s, e) =>
            {
                var p = current >= 0 ? pages[current] as ProfilesPage : null;
                if (p != null && e.Control && e.KeyCode == Keys.S) { p.Save(); e.SuppressKeyPress = true; }
            };
            FormClosing += (s, e) => { if (current >= 0 && !pages[current].CanLeave()) e.Cancel = true; };
            ShowPage(0);
        }

        void LayoutRail(Panel p)
        {
            float s = Theme.Scale;
            int x = (p.Width - rail[0].Width) / 2, y = (int)(16 * s);
            for (int i = 0; i < 3; i++) { rail[i].Location = new Point(x, y); y += rail[i].Height + (int)(8 * s); }
            rail[3].Location = new Point(x, p.Height - rail[3].Height - (int)(16 * s));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ActiveControl = null;   // no focus ring on the first button until the keyboard is used
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        Page Create(int i)
        {
            switch (i)
            {
                case 0: return new HomePage(core, this);
                case 1: return new ProfilesPage(core, this);
                case 2: return new RulesPage(core, this);
                default: return new SettingsPage(core, this);
            }
        }

        public void ShowPage(int i)
        {
            if (i == current) return;
            if (current >= 0 && !pages[current].CanLeave()) return;
            SuspendLayout();
            if (current >= 0) pages[current].Visible = false;
            pages[i].Visible = true;
            current = i;
            for (int k = 0; k < rail.Length; k++) rail[k].Selected = k == i;
            ResumeLayout();
            pages[i].Shown();
        }

        public void EditProfile(string name)
        {
            ShowPage(1);
            var p = pages[1] as ProfilesPage;
            if (current == 1 && p != null) p.Select(name);
        }

        public void RefreshState()
        {
            if (current >= 0) pages[current].RefreshState();
        }

        public void Error(string msg) { MessageBox.Show(this, msg, Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        /// Small dark text prompt. Returns null when cancelled.
        public string Prompt(string label, string value)
        {
            using (var f = new Form
            {
                Text = Program.Name, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.Body, Padding = new Padding(16), AutoScaleMode = AutoScaleMode.Dpi,
            })
            {
                f.HandleCreated += (s, e) => Theme.DarkTitleBar(f);
                var t = new TextBox { Text = value, Width = 340, BackColor = Theme.Inset, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 6, 0, 0) };
                var ok = new Btn("OK", Btn.Kinds.Accent) { DialogResult = DialogResult.OK, Width = 90, Margin = new Padding(0, 14, 8, 0) };
                var cancel = new Btn("Cancel") { DialogResult = DialogResult.Cancel, Width = 90, Margin = new Padding(0, 14, 0, 0) };
                var col = Ui.Column();
                col.Controls.Add(Ui.Text(label));
                col.Controls.Add(t);
                var buttons = Ui.Row(ok, cancel);
                buttons.Anchor = AnchorStyles.Right;
                col.Controls.Add(buttons);
                f.Controls.Add(col);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog(this) == DialogResult.OK && t.Text.Trim().Length > 0 ? t.Text.Trim() : null;
            }
        }

        sealed class RailPanel : Panel
        {
            public RailPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Rail;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, Width - 1, 0, Width - 1, Height);
            }
        }
    }
}

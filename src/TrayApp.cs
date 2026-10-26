using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading;
using System.Windows.Forms;

namespace DacEqSwitcher
{
    /// Lives in the notification area. The window is only created while open.
    sealed class TrayApp : ApplicationContext
    {
        readonly AppCore core;
        readonly NotifyIcon tray;
        readonly SynchronizationContext ui;
        readonly ToolStripMenuItem miActive, miEq, miPause, miStartup, miUpdate;
        readonly DeviceChangeWindow deviceWindow;
        MainForm form;
        int queued;
        string iconKey;
        IntPtr iconHandle;

        public TrayApp(AppCore core, bool showWindow)
        {
            this.core = core;
            ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);

            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("Open " + Program.Name, null, (s, e) => ShowWindow());
            open.Font = new Font(open.Font, FontStyle.Bold);
            miActive = new ToolStripMenuItem("") { Enabled = false };
            miEq = new ToolStripMenuItem("EQ on", null, (s, e) => ToggleEq()) { ShortcutKeyDisplayString = "middle-click" };
            miPause = new ToolStripMenuItem("Pause (free the DAC for Topping Tune)", null, (s, e) => TogglePause());
            miStartup = new ToolStripMenuItem("Start with Windows", null, (s, e) => SetStartup(!Startup.Enabled));
            miUpdate = new ToolStripMenuItem("", null, (s, e) => InstallUpdate(false)) { Visible = false };
            menu.Items.AddRange(new ToolStripItem[] { open, miActive, miUpdate, new ToolStripSeparator(), miEq, miPause, miStartup,
                new ToolStripSeparator(), new ToolStripMenuItem("Exit", null, (s, e) => Exit()) });
            menu.Opening += (s, e) => miStartup.Checked = Startup.Enabled;

            tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowWindow();
                else if (e.Button == MouseButtons.Middle) ToggleEq();
            };
            tray.BalloonTipClicked += (s, e) => ShowWindow();

            core.StateChanged += Queue;
            deviceWindow = new DeviceChangeWindow(core.Monitor.Wake);
            core.Start();
            Refresh();
            if (showWindow) ShowWindow();
        }

        /// Called from any thread; coalesces bursts into one UI update.
        void Queue()
        {
            if (Interlocked.Exchange(ref queued, 1) == 0)
                ui.Post(_ => { Interlocked.Exchange(ref queued, 0); Refresh(); }, null);
        }

        public void PostShow() { ui.Post(_ => ShowWindow(), null); }

        public string ActiveText
        {
            get
            {
                if (!core.EqOn) return "Off";
                if (core.ActiveProfile == null) return "No EQ applied yet";
                return core.ActiveProfile == "" ? "No EQ" : core.ActiveProfile;
            }
        }

        void Refresh()
        {
            var m = core.Monitor;
            string key, letter;
            Color color;
            if (!core.EqOn) { key = "off"; color = Color.FromArgb(128, 128, 128); letter = null; }
            else if (m.Paused) { key = "p"; color = Color.FromArgb(217, 138, 28); letter = "II"; }
            else if (!m.Connected) { key = "d"; color = Color.FromArgb(128, 128, 128); letter = "–"; }
            else if (string.IsNullOrEmpty(core.ActiveProfile)) { key = "n"; color = Color.FromArgb(96, 112, 140); letter = "–"; }
            else { letter = core.ActiveProfile.Substring(0, 1).ToUpperInvariant(); key = "a" + letter; color = Color.FromArgb(37, 99, 235); }
            if (key != iconKey) { SetIcon(color, letter); iconKey = key; }

            string line = !core.EqOn ? "EQ off" : m.Paused ? "Paused" : m.Connected ? (m.Model != null ? m.Model.Name + ": " : "") + ActiveText : m.Status;
            string tip = Program.Name + "\n" + line;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
            miActive.Text = !core.EqOn ? "EQ is off" : m.Connected ? "Active EQ: " + ActiveText : m.Status;
            miEq.Checked = core.EqOn;
            miPause.Checked = m.Paused;
            var up = core.Updater.Available;
            miUpdate.Visible = up != null;
            if (up != null)
            {
                miUpdate.Text = "Install update " + up.Tag;
                if (core.Settings.NotifiedUpdate != up.Tag)
                {
                    core.Settings.NotifiedUpdate = up.Tag;
                    core.SaveSettings();
                    tray.ShowBalloonTip(8000, Program.Name + " " + up.Tag, "An update is available. Open the app or the tray menu to install it.", ToolTipIcon.Info);
                }
            }
            var eapo = core.Updater.EapoLatest;
            if (eapo != null && up == null && core.Settings.NotifiedEapo != eapo.ToString())
            {
                core.Settings.NotifiedEapo = eapo.ToString();
                core.SaveSettings();
                tray.ShowBalloonTip(8000, "Equalizer APO " + eapo + " is available",
                    "You have " + core.Updater.EapoInstalled + ". Open " + Program.Name + " for the download link.", ToolTipIcon.Info);
            }
            if (form != null && !form.IsDisposed) form.RefreshState();
        }

        void SetIcon(Color color, string letter)
        {
            int size = SystemInformation.SmallIconSize.Width;
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                if (letter == null)
                {
                    // EQ off: a hollow ring
                    using (var p = new Pen(color, Math.Max(2f, size / 7f))) g.DrawEllipse(p, size / 8f, size / 8f, size * 3 / 4f, size * 3 / 4f);
                }
                else
                {
                    using (var b = new SolidBrush(color)) g.FillEllipse(b, 0, 0, size - 1, size - 1);
                    using (var f = new Font("Segoe UI", size * (letter.Length > 1 ? 0.42f : 0.58f), FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(letter, f, Brushes.White, new RectangleF(0, 0, size, size + 1), sf);
                }
                IntPtr h = bmp.GetHicon();
                tray.Icon = Icon.FromHandle(h);
                if (iconHandle != IntPtr.Zero && Native.OnWindows) Native.DestroyIcon(iconHandle);
                iconHandle = h;
            }
        }

        public void ShowWindow()
        {
            if (form == null || form.IsDisposed)
            {
                form = new MainForm(core, this);
                form.FormClosed += (s, e) => { form = null; Native.TrimMemory(); };
            }
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
        }

        /// Installs the downloaded update and hands over to the new version.
        public void InstallUpdate(bool fromWindow)
        {
            var up = core.Updater.Available;
            if (up == null) return;
            Cursor.Current = Cursors.WaitCursor;
            string err = core.Updater.Install(fromWindow || (form != null && !form.IsDisposed && form.Visible));
            Cursor.Current = Cursors.Default;
            if (err == null) { Exit(); return; }
            if (MessageBox.Show(err + "\n\nOpen the release page?", Program.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                try { System.Diagnostics.Process.Start(up.Page); } catch { }
        }

        public void ToggleEq()
        {
            core.SetEqOn(!core.EqOn);
            Refresh();
        }

        public void TogglePause()
        {
            core.Monitor.SetPaused(!core.Monitor.Paused);
            Refresh();
        }

        public void SetStartup(bool on)
        {
            try { Startup.Enabled = on; }
            catch (Exception ex) { MessageBox.Show("Couldn't change the start-up setting: " + ex.Message, Program.Name); }
            if (form != null && !form.IsDisposed) form.RefreshState();
        }

        public void Exit()
        {
            tray.Visible = false;
            try
            {
                if (form != null && !form.IsDisposed) form.Close();
                deviceWindow.DestroyHandle();
            }
            catch (Exception ex) { Log.Write("exit: " + ex.Message); }
            finally { ExitThread(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tray.Dispose();
            base.Dispose(disposing);
        }
    }

    /// Hidden top-level window that receives Windows' device-arrival broadcasts,
    /// so the app reacts to the DAC being plugged in without polling.
    sealed class DeviceChangeWindow : NativeWindow
    {
        const int WM_DEVICECHANGE = 0x0219;
        readonly Action onChange;

        public DeviceChangeWindow(Action onChange)
        {
            this.onChange = onChange;
            CreateHandle(new CreateParams { Caption = "DacEqSwitcher.DeviceWatcher" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE) onChange();
            base.WndProc(ref m);
        }
    }
}

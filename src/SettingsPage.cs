using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DacEqSwitcher
{
    /// Settings: general, loudness, the DAC's less-used settings, Equalizer APO, about.
    sealed class SettingsPage : Page
    {
        readonly TableLayoutPanel col;
        readonly Switch swStartup, swUpdates, swMuteOpt;
        readonly Segmented segStrength, segBoost, segRef;
        readonly Label lblEapo, lblUpdate;
        readonly Btn btnEapoFix, btnCheck;
        readonly TableLayoutPanel dacRows;
        readonly Label lblDacNone;
        Protocol dacBuilt = (Protocol)(-1);
        readonly Dictionary<string, Dropdown> dx5Boxes = new Dictionary<string, Dropdown>();
        readonly HashSet<Control> busy = new HashSet<Control>();
        static readonly int[] Strengths = { 40, 60, 80, 100 }, Boosts = { 6, 9, 12, 15 }, Refs = { 70, 75, 80 };
        // shown on Home, so not repeated here
        static readonly HashSet<string> OnHome = new HashSet<string> { "highGain", "inputType", "pcmFilter", "muted" };

        public SettingsPage(AppCore core, MainForm shell) : base(core, shell)
        {
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
            scroll.HandleCreated += (s, e) => Theme.DarkScrollBars(scroll);
            col = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = new Padding(0), Padding = new Padding(0, 0, 12, 12) };
            col.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            col.Controls.Add(Ui.Text("Settings", Theme.F(15f, FontStyle.Bold)));

            // general
            var general = Section("General");
            swStartup = new Switch();
            swStartup.Click += (s, e) => Shell.App.SetStartup(swStartup.Checked);
            AddRow(general, "Start with Windows", swStartup);
            swUpdates = new Switch();
            swUpdates.Click += (s, e) =>
            {
                Core.Settings.NoUpdateCheck = !swUpdates.Checked;
                Core.SaveSettings();
                Core.Updater.SetEnabled(swUpdates.Checked);
                if (swUpdates.Checked) Core.Updater.CheckNow();
            };
            AddRow(general, "Check daily for updates to this app and Equalizer APO", swUpdates);
            lblUpdate = Ui.Text("", Theme.Body, Theme.Muted);
            btnCheck = new Btn("Check now");
            btnCheck.Width = btnCheck.GetPreferredSize(Size.Empty).Width;
            btnCheck.Click += (s, e) => { Core.Updater.CheckNow(); lblUpdate.Text = "Checking…"; };
            AddRow(general, lblUpdate, btnCheck);

            // loudness
            var loud = Section("Loudness compensation");
            segStrength = new Segmented(Strengths.Select(x => x + " %").ToArray()) { Width = 280 };
            segStrength.Committed += i => SetLoud(() => Core.Settings.LoudnessStrength = Strengths[i]);
            AddRow(loud, "Strength", segStrength);
            segBoost = new Segmented(Boosts.Select(x => "+" + x + " dB").ToArray()) { Width = 280 };
            segBoost.Committed += i => SetLoud(() => Core.Settings.LoudnessMaxBoost = Boosts[i]);
            AddRow(loud, "Largest boost", segBoost);
            segRef = new Segmented(Refs.Select(x => x + " phon").ToArray()) { Width = 280 };
            segRef.Committed += i => SetLoud(() => Core.Settings.LoudnessRefPhon = Refs[i]);
            AddRow(loud, "Reference loudness", segRef);
            var note = Ui.Text("ISO 226:2003 equal-loudness contours (100 % is the standard). The largest boost is also the headroom\n" +
                               "kept at every volume (less if your EQ already has some), so the level follows the DAC smoothly\n" +
                               "and nothing clips. The reference volume is set per rule on Home.", Theme.Small, Theme.Muted);
            note.Margin = new Padding(0, 4, 0, 0);
            loud.Controls.Add(note);

            // DAC
            var dac = Section("DAC");
            dacRows = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) };
            dacRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dacRows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dac.Controls.Add(dacRows);
            lblDacNone = Ui.Text("Connect your DAC to see its settings.", Theme.Body, Theme.Muted);
            swMuteOpt = new Switch();
            swMuteOpt.Click += (s, e) => { Core.Settings.MuteOptical = swMuteOpt.Checked; Core.SaveSettings(); };

            // Equalizer APO
            var eapo = Section("Equalizer APO");
            lblEapo = Ui.Text("", Theme.Body, Theme.Muted);
            lblEapo.MaximumSize = new Size(560, 0);
            btnEapoFix = new Btn("Set up", Btn.Kinds.Accent);
            btnEapoFix.Click += (s, e) => FixEapo();
            var cfg = new Btn("Open Configurator");
            cfg.Width = cfg.GetPreferredSize(Size.Empty).Width;
            cfg.Click += (s, e) => { try { Core.Eapo.OpenConfigurator(); } catch (Exception ex) { Shell.Error(ex.Message); } };
            AddRow(eapo, lblEapo, Ui.Row(btnEapoFix, Ui.Spacer(8, 1), cfg));

            // about
            var about = Section("About");
            var a = Ui.Text("DAC EQ Switcher " + Updater.Current + "\n" +
                            "DAC support from toppingctl (rev " + ToppingTables.Revision + ", Apache-2.0)\n" +
                            "Loudness: ISO 226:2003 equal-loudness contours\n" +
                            "Not affiliated with Topping", Theme.Body, Theme.Muted);
            about.Controls.Add(a);

            scroll.Controls.Add(col);
            scroll.Resize += (s, e) => col.Width = scroll.ClientSize.Width - (scroll.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0);
            Controls.Add(scroll);
        }

        TableLayoutPanel Section(string title)
        {
            var card = new Card { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 14, 0, 0), Padding = new Padding(20, 14, 20, 16) };
            var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var h = Ui.Text(title, Theme.F(10.5f, FontStyle.Bold));
            h.Margin = new Padding(0, 0, 0, 8);
            t.Controls.Add(h);
            card.Controls.Add(t);
            col.Controls.Add(card);
            return t;
        }

        static void AddRow(TableLayoutPanel section, string label, Control c) { AddRow(section, Ui.Text(label), c); }

        static void AddRow(TableLayoutPanel section, Control label, Control c)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 6) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            label.Anchor = AnchorStyles.Left;
            c.Anchor = AnchorStyles.Right;
            row.Controls.Add(label, 0, 0);
            row.Controls.Add(c, 1, 0);
            section.Controls.Add(row);
        }

        void SetLoud(Action change)
        {
            change();
            Core.SaveSettings();
            Core.Changed();
        }

        public override void RefreshState()
        {
            swStartup.Checked = Startup.Enabled;
            swUpdates.Checked = !Core.Settings.NoUpdateCheck;
            var up = Core.Updater.Available;
            lblUpdate.Text = up != null ? "Version " + up.Tag + " is available (you have " + Updater.Current + ")" : "Version " + Updater.Current;
            lblUpdate.ForeColor = up != null ? Theme.Accent : Theme.Muted;
            segStrength.SelectedIndex = Array.IndexOf(Strengths, Core.Settings.LoudnessStrength);
            segBoost.SelectedIndex = Array.IndexOf(Boosts, Core.Settings.LoudnessMaxBoost);
            segRef.SelectedIndex = Array.IndexOf(Refs, Core.Settings.LoudnessRefPhon);
            RefreshEapo();
            RefreshDac();
        }

        // ----------------------------------------------------------- Equalizer APO --
        void RefreshEapo()
        {
            var st = Core.Eapo.Check();
            string text, fix = null;
            if (!st.Installed) { text = "Equalizer APO isn't installed. Install it and select your DAC in its Configurator."; fix = "Download"; }
            else if (!st.Writable || !st.Included) { text = "One-time setup: lets this app change Equalizer APO's EQ (asks for administrator permission once) and adds one line to its config.txt."; fix = "Set up"; }
            else if (st.OtherLines.Count > 0) { text = "Its config.txt also applies other EQ, which adds to this app's EQ:\n" + string.Join("\n", st.OtherLines.Take(3)); fix = "Turn it off"; }
            else text = "Ready." + (Core.Updater.EapoLatest != null ? " Version " + Core.Updater.EapoLatest + " is available." : "");
            if (fix == null && Core.Updater.EapoLatest != null) fix = "Download";
            lblEapo.Text = text;
            lblEapo.ForeColor = fix != null && fix != "Download" ? Theme.Warn : Theme.Muted;
            btnEapoFix.Visible = fix != null;
            if (fix != null) { btnEapoFix.Text = fix; btnEapoFix.Width = btnEapoFix.GetPreferredSize(Size.Empty).Width; }
        }

        void FixEapo()
        {
            var st = Core.Eapo.Check();
            if (!st.Installed || btnEapoFix.Text == "Download") { try { System.Diagnostics.Process.Start(Updater.EapoPage); } catch { } }
            else if (!st.Writable || !st.Included) { var e = Core.Eapo.Setup(); if (e != null) Shell.Error(e); Core.Changed(); }
            else DisableOthers(Core, Shell);
            RefreshEapo();
        }

        public static void DisableOthers(AppCore core, MainForm shell)
        {
            var st = core.Eapo.Check();
            if (MessageBox.Show(shell, "These lines in Equalizer APO's config.txt will be turned into comments, so only this app's EQ applies:\n\n" +
                                string.Join("\n", st.OtherLines) + "\n\nA backup of config.txt is kept in the same folder.",
                                Program.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { Log.Write("disabled other config lines, backup " + core.Eapo.DisableOtherLines()); }
            catch (Exception ex) { shell.Error(ex.Message); }
            shell.RefreshState();
        }

        // --------------------------------------------------------------- DAC --
        void RefreshDac()
        {
            var m = Core.Monitor;
            var p = !m.Connected || m.Handler == null ? Protocol.None : m.Handler is Dx1Protocol ? Protocol.Dx1 : Protocol.Dx5;
            if (p != dacBuilt) BuildDac(p);
            swMuteOpt.Checked = Core.Settings.MuteOptical;
            if (p != Protocol.Dx5) return;
            var vals = m.Snapshot();
            foreach (var kv in dx5Boxes)
            {
                if (busy.Contains(kv.Value)) continue;
                int v;
                var f = Dx5Protocol.All[kv.Key];
                kv.Value.SelectedIndex = vals.TryGetValue(kv.Key, out v) ? Array.IndexOf(f.Values, v) : -1;
            }
        }

        Btn SceneButton(string text, int cmd)
        {
            var b = new Btn(text);
            b.Width = b.GetPreferredSize(Size.Empty).Width;
            b.Click += (s, e) =>
            {
                if (MessageBox.Show(Shell, text + "? A scene can change volume, input, output and the DAC's EQ at once.", Program.Name,
                                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                Shell.Actions.Send(Dx5Protocol.Recall(cmd), err => { if (err != null) Shell.Error(err); });
            };
            return b;
        }

        void BuildDac(Protocol p)
        {
            dacBuilt = p;
            dacRows.SuspendLayout();
            foreach (Control c in dacRows.Controls.Cast<Control>().ToList()) if (c != lblDacNone && c != swMuteOpt) c.Dispose();
            dacRows.Controls.Clear();
            dx5Boxes.Clear();
            if (p == Protocol.None) dacRows.Controls.Add(lblDacNone, 0, 0);
            else if (p == Protocol.Dx1)
            {
                var l = Ui.Text("Mute also mutes the optical output");
                l.Anchor = AnchorStyles.Left;
                swMuteOpt.Anchor = AnchorStyles.Right;
                dacRows.Controls.Add(l, 0, 0);
                dacRows.Controls.Add(swMuteOpt, 1, 0);
            }
            else
            {
                var reported = Core.Monitor.Snapshot();
                int row = 0;
                var scenes = Ui.Row(SceneButton("Recall C1", ToppingTables.RecallC1), Ui.Spacer(8, 1), SceneButton("Recall C2", ToppingTables.RecallC2));
                scenes.Anchor = AnchorStyles.Right;
                var sl = Ui.Text("Scenes");
                sl.Anchor = AnchorStyles.Left;
                dacRows.Controls.Add(sl, 0, row);
                dacRows.Controls.Add(scenes, 1, row);
                row++;
                foreach (var kv in ToppingTables.Dx5Settable.OrderBy(k => Dx5Protocol.All.ContainsKey("dx5." + k.Key) ? Dx5Protocol.All["dx5." + k.Key].Name : k.Key))
                {
                    string id = "dx5." + kv.Key;
                    FieldDef f;
                    if (OnHome.Contains(kv.Key) || !Dx5Protocol.All.TryGetValue(id, out f) || !reported.ContainsKey(id) || f.Values.Length == 0) continue;
                    bool unverified = kv.Value.Value == "unchecked" || kv.Value.Value == "register";
                    var label = Ui.Column();
                    label.Controls.Add(Ui.Text(f.Name));
                    if (unverified) label.Controls.Add(Ui.Text("Mapping not yet confirmed by toppingctl", Theme.Small, Theme.Muted));
                    label.Anchor = AnchorStyles.Left;
                    label.Margin = new Padding(0, 4, 0, 4);
                    var dd = new Dropdown { Width = 260, Anchor = AnchorStyles.Right, Margin = new Padding(0, 4, 0, 4) };
                    dd.SetItems(f.Values.Select(f.Format));
                    int cmd = kv.Value.Key;
                    var fd = f;
                    dd.Committed += i =>
                    {
                        int raw = fd.Values[i];
                        busy.Add(dd);
                        Shell.Actions.SendVerified(Dx5Protocol.Set(cmd, raw), id, raw, fd.Format, err =>
                        {
                            busy.Remove(dd);
                            if (err != null) Shell.Error(fd.Name + ": " + err);
                            RefreshDac();
                        });
                    };
                    dx5Boxes[id] = dd;
                    dacRows.Controls.Add(label, 0, row);
                    dacRows.Controls.Add(dd, 1, row);
                    row++;
                }
            }
            dacRows.ResumeLayout();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DacEqSwitcher
{
    /// Home: what's playing and why, the rules, volume and the DAC's settings.
    sealed class HomePage : Page
    {
        // header
        readonly Panel dot;
        readonly Label lblDevice, lblConn;
        readonly Btn btnPause, btnPower;
        // hero
        readonly Label lblPlayingCaption, lblProfile, lblRule;
        readonly Switch swEq, swLoud;
        readonly Panel loudStrip;
        readonly Label lblLoudValue, lblLoudDetail;
        readonly Btn btnSetRef;
        readonly ResponseGraph graph;
        readonly Panel legend;
        readonly Btn btnEditEq;
        // rules
        readonly FlowLayoutPanel ruleList;
        readonly Btn btnNewRule;
        // volume
        readonly ToolTip tipDetail = new ToolTip();
        readonly Btn btnMute, btnDown, btnUp;
        readonly VolumeSlider slider;
        readonly Label lblVolume;
        // settings strip
        readonly Label lblInput, lblGain, lblFilter;
        readonly Segmented segInput, segGain;
        readonly Dropdown ddFilter;
        // footer
        readonly Label lblStatus, lblVersion;
        readonly Btn btnStatusAction, btnUpdate;
        Action statusAction;

        string graphKey, rulesKey;
        Protocol builtFor = (Protocol)(-1);
        readonly HashSet<Control> busy = new HashSet<Control>();

        public HomePage(AppCore core, MainForm shell) : base(core, shell)
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 5, BackColor = Theme.Bg, Margin = new Padding(0), Padding = new Padding(0) };
            for (int i = 0; i < 3; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            // ---------------------------------------------------------- header
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Margin = new Padding(0, 0, 0, 12), BackColor = Theme.Bg };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dot = new Dot { Size = new Size(10, 10), Anchor = AnchorStyles.Left };
            lblDevice = Ui.Text("", Theme.F(10.5f, FontStyle.Bold));
            lblDevice.Anchor = AnchorStyles.Left;
            lblDevice.Margin = new Padding(0, 0, 10, 0);
            lblConn = Ui.Text("", Theme.Body, Theme.Muted);
            lblConn.Anchor = AnchorStyles.Left;
            btnPause = new Btn("Pause") { Width = 90, Anchor = AnchorStyles.Right, Margin = new Padding(0, 0, 8, 0) };
            btnPause.Click += (s, e) => Shell.App.TogglePause();
            btnPower = new Btn("", Btn.Kinds.Normal).WithIcon(Icons.Power, true);
            btnPower.AccessibleName = "Standby";
            btnPower.Anchor = AnchorStyles.Right;
            btnPower.Click += (s, e) => TogglePower();
            new ToolTip().SetToolTip(btnPower, "Standby / wake the DAC");
            header.Controls.Add(dot, 0, 0);
            header.Controls.Add(lblDevice, 1, 0);
            header.Controls.Add(lblConn, 2, 0);
            header.Controls.Add(btnPause, 4, 0);
            header.Controls.Add(btnPower, 5, 0);
            grid.Controls.Add(header, 0, 0);
            grid.SetColumnSpan(header, 3);

            // ------------------------------------------------------------ hero
            var hero = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 12), Padding = new Padding(22, 18, 22, 14) };
            var heroGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
            heroGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            heroGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            heroGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            heroGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var names = Ui.Column();
            lblPlayingCaption = Ui.Text("Playing through", Theme.Body, Theme.Muted);
            lblProfile = Ui.Text("", Theme.F(26f, FontStyle.Bold));
            lblProfile.AutoEllipsis = true;
            lblRule = Ui.Text("", Theme.Body, Theme.Muted);
            names.Controls.Add(lblPlayingCaption);
            names.Controls.Add(lblProfile);
            names.Controls.Add(lblRule);
            top.Controls.Add(names, 0, 0);
            var switches = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            swEq = new Switch { AccessibleName = "EQ" };
            swLoud = new Switch { AccessibleName = "Loudness compensation" };
            swEq.Click += (s, e) => { Core.SetEqOn(swEq.Checked); RefreshState(); };
            swLoud.Click += (s, e) => { Core.SetLoudnessOn(swLoud.Checked); RefreshState(); };
            AddSwitch(switches, "EQ", swEq, 0);
            AddSwitch(switches, "Loudness", swLoud, 1);
            top.Controls.Add(switches, 1, 0);
            heroGrid.Controls.Add(top, 0, 0);

            loudStrip = new Card { Fill = Theme.Inset, Radius = 10, Dock = DockStyle.Fill, Height = 50, Padding = new Padding(14, 8, 8, 8), Margin = new Padding(0, 10, 0, 6) };
            var loudRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = new Padding(0) };
            loudRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            loudRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            loudRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            loudRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            loudRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var lblLoud = Ui.Text("Loudness", Theme.Body, Theme.Muted);
            lblLoudValue = Ui.Text("", Theme.F(10f, FontStyle.Bold));
            lblLoudDetail = Ui.Text("", Theme.Body, Theme.Muted);
            foreach (var l in new[] { lblLoud, lblLoudValue, lblLoudDetail }) { l.Anchor = AnchorStyles.Left; l.Margin = new Padding(0, 0, 12, 0); }
            btnSetRef = new Btn("Set current volume as reference", Btn.Kinds.Normal) { Anchor = AnchorStyles.Right, Height = 32, Font = Theme.Small };
            btnSetRef.Width = btnSetRef.GetPreferredSize(Size.Empty).Width + 16;
            btnSetRef.MinimumSize = new Size(btnSetRef.Width, 0);
            btnSetRef.Click += (s, e) => { if (!Core.SetLoudnessReference()) Shell.Error("Connect your DAC first."); RefreshState(); };
            loudRow.Controls.Add(lblLoud, 0, 0);
            loudRow.Controls.Add(lblLoudValue, 1, 0);
            // the detail text takes whatever room is left (shortened with "…"), so the button never clips
            lblLoudDetail.AutoSize = false;
            lblLoudDetail.AutoEllipsis = true;
            lblLoudDetail.Dock = DockStyle.Fill;
            lblLoudDetail.TextAlign = ContentAlignment.MiddleLeft;
            loudRow.Controls.Add(lblLoudDetail, 3, 0);
            loudRow.Controls.Add(btnSetRef, 4, 0);
            loudStrip.Controls.Add(loudRow);
            heroGrid.Controls.Add(loudStrip, 0, 1);

            graph = new ResponseGraph { Simple = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 4), MinimumSize = new Size(0, 90) };
            heroGrid.Controls.Add(graph, 0, 2);

            var axis = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, AutoSize = true, Margin = new Padding(0) };
            axis.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            axis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            axis.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            axis.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            axis.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var a20 = Ui.Text("20 Hz", Theme.Small, Theme.Muted);
            var a1k = Ui.Text("1 kHz", Theme.Small, Theme.Muted);
            a1k.Anchor = AnchorStyles.None;
            var a20k = Ui.Text("20 kHz", Theme.Small, Theme.Muted);
            legend = new Legend { Size = new Size(210, 18), Margin = new Padding(18, 0, 0, 0) };
            btnEditEq = new Btn("Edit EQ", Btn.Kinds.Link) { Height = 22, Margin = new Padding(18, 0, 0, 0) };
            btnEditEq.Width = btnEditEq.GetPreferredSize(Size.Empty).Width;
            btnEditEq.Click += (s, e) => Shell.EditProfile(Core.ActiveProfile);
            axis.Controls.Add(a20, 0, 0);
            axis.Controls.Add(a1k, 1, 0);
            axis.Controls.Add(a20k, 2, 0);
            axis.Controls.Add(legend, 3, 0);
            axis.Controls.Add(btnEditEq, 4, 0);
            heroGrid.Controls.Add(axis, 0, 3);
            hero.Controls.Add(heroGrid);
            grid.Controls.Add(hero, 0, 1);
            grid.SetColumnSpan(hero, 2);

            // ----------------------------------------------------------- rules
            var rulesCard = new Card { Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 12), Padding = new Padding(16, 16, 16, 14) };
            var rulesGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            rulesGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rulesGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rulesGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var lblEq = Ui.Text("EQ", Theme.F(10.5f, FontStyle.Bold));
            lblEq.Margin = new Padding(0, 0, 0, 8);
            rulesGrid.Controls.Add(lblEq, 0, 0);
            ruleList = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = new Padding(0) };
            ruleList.Resize += (s, e) => SizeRuleItems();
            ruleList.HandleCreated += (s, e) => Theme.DarkScrollBars(ruleList);
            rulesGrid.Controls.Add(ruleList, 0, 1);
            btnNewRule = new Btn("New rule", Btn.Kinds.Dashed).WithIcon(Icons.Plus);
            btnNewRule.Dock = DockStyle.Fill;
            btnNewRule.Height = 40;
            btnNewRule.Radius = 10;
            btnNewRule.Margin = new Padding(0, 8, 0, 0);
            btnNewRule.Click += (s, e) => { if (RulesPage.AddRule(Core, Shell)) RefreshState(); };
            rulesGrid.Controls.Add(btnNewRule, 0, 2);
            rulesCard.Controls.Add(rulesGrid);
            grid.Controls.Add(rulesCard, 2, 1);

            // ---------------------------------------------------------- volume
            var volCard = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(16, 10, 18, 10) };
            var volRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = new Padding(0) };
            volRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            volRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            volRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            volRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            volRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            btnMute = new Btn("", Btn.Kinds.Inset).WithIcon(Icons.Speaker, true);
            btnMute.Size = new Size(38, 38);
            btnMute.AccessibleName = "Mute";
            btnMute.Anchor = AnchorStyles.Left;
            btnMute.Margin = new Padding(0, 0, 10, 0);
            btnMute.Click += (s, e) => ToggleMute();
            btnDown = new Btn("", Btn.Kinds.Inset).WithIcon(Icons.Minus, true);
            btnDown.Size = new Size(32, 32);
            btnDown.AccessibleName = "Volume down one step";
            btnDown.Anchor = AnchorStyles.Left;
            btnUp = new Btn("", Btn.Kinds.Inset).WithIcon(Icons.Plus, true);
            btnUp.Size = new Size(32, 32);
            btnUp.AccessibleName = "Volume up one step";
            btnUp.Anchor = AnchorStyles.Left;
            slider = new VolumeSlider { Warn = ToppingTables.VolWarnDb, Dock = DockStyle.Fill, Margin = new Padding(6, 0, 6, 0) };
            lblVolume = Ui.Text("—", Theme.F(15f, FontStyle.Bold));
            lblVolume.Anchor = AnchorStyles.Right;
            lblVolume.TextAlign = ContentAlignment.MiddleRight;
            volRow.Controls.Add(btnMute, 0, 0);
            volRow.Controls.Add(btnDown, 1, 0);
            volRow.Controls.Add(slider, 2, 0);
            volRow.Controls.Add(btnUp, 3, 0);
            volRow.Controls.Add(lblVolume, 4, 0);
            volCard.Controls.Add(volRow);
            grid.Controls.Add(volCard, 0, 2);
            grid.SetColumnSpan(volCard, 3);
            WireVolume();

            // -------------------------------------------------------- settings
            segInput = new Segmented("USB", "Optical") { Dock = DockStyle.Top };
            segGain = new Segmented("Low", "High") { Dock = DockStyle.Top };
            ddFilter = new Dropdown { Dock = DockStyle.Top };
            lblInput = Ui.Text("Input", Theme.Small, Theme.Muted);
            lblGain = Ui.Text("Headphone gain", Theme.Small, Theme.Muted);
            lblFilter = Ui.Text("PCM filter", Theme.Small, Theme.Muted);
            grid.Controls.Add(Setting(lblInput, segInput, new Padding(0, 0, 8, 0)), 0, 3);
            grid.Controls.Add(Setting(lblGain, segGain, new Padding(8, 0, 8, 0)), 1, 3);
            grid.Controls.Add(Setting(lblFilter, ddFilter, new Padding(8, 0, 0, 0)), 2, 3);
            segInput.Committed += i => SendSetting(segInput, "input", i);
            segGain.Committed += i => SendSetting(segGain, "gain", i);
            ddFilter.Committed += i => SendSetting(ddFilter, "filter", i);

            // ---------------------------------------------------------- footer
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = new Padding(0, 6, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lblStatus = Ui.Text("", Theme.Small, Theme.Muted);
            lblStatus.Anchor = AnchorStyles.Left;
            btnStatusAction = new Btn("", Btn.Kinds.Link) { Height = 20, Font = Theme.Small, Margin = new Padding(10, 0, 0, 0), Anchor = AnchorStyles.Left };
            btnStatusAction.Click += (s, e) => { if (statusAction != null) statusAction(); };
            lblVersion = Ui.Text("", Theme.Small, Theme.Muted);
            lblVersion.Anchor = AnchorStyles.Right;
            btnUpdate = new Btn("Install", Btn.Kinds.Link) { Height = 20, Font = Theme.Small, Margin = new Padding(10, 0, 0, 0), Anchor = AnchorStyles.Right };
            btnUpdate.Width = btnUpdate.GetPreferredSize(Size.Empty).Width;
            btnUpdate.Click += (s, e) => Shell.App.InstallUpdate(true);
            footer.Controls.Add(lblStatus, 0, 0);
            footer.Controls.Add(btnStatusAction, 1, 0);
            footer.Controls.Add(lblVersion, 3, 0);
            footer.Controls.Add(btnUpdate, 4, 0);
            grid.Controls.Add(footer, 0, 4);
            grid.SetColumnSpan(footer, 3);

            Controls.Add(grid);
        }

        static void AddSwitch(TableLayoutPanel t, string label, Switch sw, int row)
        {
            var l = Ui.Text(label);
            l.Anchor = AnchorStyles.Right;
            l.Margin = new Padding(0, 0, 10, 0);
            sw.Anchor = AnchorStyles.Left;
            sw.Margin = new Padding(0, row == 0 ? 0 : 10, 0, 0);
            l.Margin = new Padding(0, row == 0 ? 0 : 10, 10, 0);
            t.Controls.Add(l, 0, row);
            t.Controls.Add(sw, 1, row);
        }

        static Control Setting(Label l, Control c, Padding m)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = m };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            l.Margin = new Padding(0, 0, 0, 6);
            t.Controls.Add(l, 0, 0);
            t.Controls.Add(c, 0, 1);
            return t;
        }

        // ------------------------------------------------------------ refresh --
        public override void RefreshState()
        {
            var m = Core.Monitor;
            HashSet<string> lastSeen;
            var vals = Core.Values(out lastSeen);
            bool connected = m.Connected;
            var proto = !connected || m.Handler == null ? Protocol.None : m.Handler is Dx1Protocol ? Protocol.Dx1 : Protocol.Dx5;
            if (proto != builtFor) Rebuild(proto);

            // header
            ((Dot)dot).Color = m.Paused ? Theme.Warn : connected ? Theme.Good : Theme.Muted;
            lblDevice.Text = m.Model != null ? m.Model.Name : "Topping DAC";
            lblConn.Text = m.Paused ? "Paused" : connected ? "Connected" : m.Status;
            btnPause.Text = m.Paused ? "Resume" : "Pause";
            btnPower.Enabled = connected;

            // hero
            swEq.Checked = Core.EqOn;
            swLoud.Checked = Core.LoudnessOn;
            if (!Core.EqOn)
            {
                lblProfile.Text = "Off";
                lblProfile.ForeColor = Theme.Muted;
                lblRule.Text = "EQ is switched off";
            }
            else
            {
                lblProfile.Text = string.IsNullOrEmpty(Core.ActiveProfile) ? "No EQ" : Core.ActiveProfile;
                lblProfile.ForeColor = string.IsNullOrEmpty(Core.ActiveProfile) ? Theme.Muted : Theme.Text;
                lblRule.Text = !connected ? (Core.ActiveProfile == null ? m.Status : "Kept while the DAC is away")
                             : Core.ActiveRule != null ? "Rule: " + Core.RuleText(Core.ActiveRule) : "No rule matches";
            }
            RefreshLoudness(connected);
            RefreshGraph();
            btnEditEq.Visible = !string.IsNullOrEmpty(Core.ActiveProfile);
            RefreshRules();

            // volume + settings
            RefreshVolume(vals, connected);
            RefreshSettings(vals, connected);
            RefreshFooter(vals);
        }

        void RefreshLoudness(bool connected)
        {
            var l = Core.LoudnessNow;
            loudStrip.Visible = Core.LoudnessOn && Core.EqOn;
            if (l == null || !loudStrip.Visible) return;
            btnSetRef.Enabled = connected;
            string tip;
            if (!l.HasReference)
            {
                lblLoudValue.Text = "No reference volume";
                lblLoudDetail.Text = "";
                tip = "Play music at your normal listening volume, then set it as the reference.";
            }
            else
            {
                var p = l.Plan;
                bool boosting = p != null && p.Boosting;
                lblLoudValue.Text = boosting ? "+" + p.BassDb.ToString("0.0") + " dB at 50 Hz" : "Nothing added at this volume";
                lblLoudDetail.Text = boosting ? p.KnobBelow.ToString("0.0") + " dB below reference" : "Reference " + Db(l.RefDb.Value);
                tip = "Reference volume: " + Db(l.RefDb.Value) + " on the DAC." +
                      (p != null && p.HeadroomDb > 0
                          ? "\nLoudness keeps " + p.HeadroomDb.ToString("0.0") + " dB of headroom at every volume so the extra bass can't clip.\n" +
                            "That's why switching Loudness on makes everything that much quieter; turning the DAC down then\n" +
                            "lowers the level exactly as much as the knob says."
                          : "");
            }
            tipDetail.SetToolTip(lblLoudDetail, tip);
            tipDetail.SetToolTip(lblLoudValue, tip);
        }

        void RefreshGraph()
        {
            var l = Core.LoudnessNow;
            string profile = Core.EqOn ? Core.ActiveProfile : null;
            var plan = l == null ? null : l.Plan;
            string key = profile + "|" + (plan != null ? string.Join(",", plan.Curve.Select(x => x.ToString("0.0"))) + plan.PreampDb : "-");
            if (key == graphKey) return;
            graphKey = key;
            string text = "";
            try { if (!string.IsNullOrEmpty(profile)) text = Core.Profiles.Read(profile); } catch { }
            graph.Data = EqText.Analyse(text);
            string loud = plan != null && plan.Boosting ? Loudness.ApoText(plan, "") : null;
            graph.Overlay = loud == null ? null : EqText.Analyse(text + "\r\n" + loud);
            legend.Visible = loud != null;
        }

        void RefreshRules()
        {
            var rules = Core.Settings.Rules;
            string key = string.Join("\n", rules.Select(r => Core.RuleText(r) + "|" + r.Profile + "|" + (r == Core.ActiveRule && Core.Monitor.Connected)));
            if (key == rulesKey) return;
            rulesKey = key;
            ruleList.SuspendLayout();
            foreach (Control c in ruleList.Controls.Cast<Control>().ToList()) c.Dispose();
            ruleList.Controls.Clear();
            foreach (var r in rules)
            {
                var rule = r;
                var it = new RuleItem
                {
                    Condition = Core.RuleText(r),
                    Profile = string.IsNullOrEmpty(r.Profile) ? "No EQ" : r.Profile,
                    Active = r == Core.ActiveRule && Core.Monitor.Connected,
                    Margin = new Padding(0, 0, 0, 8),
                    AccessibleName = Core.RuleText(r) + ": " + r.Profile,
                };
                it.Click += (s, e) => { if (RulesPage.EditRule(Core, Shell, rule)) RefreshState(); };
                ruleList.Controls.Add(it);
            }
            ruleList.ResumeLayout();
            SizeRuleItems();
        }

        void SizeRuleItems()
        {
            int w = ruleList.ClientSize.Width - (ruleList.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0);
            foreach (Control c in ruleList.Controls) c.Width = Math.Max(100, w);
        }

        void RefreshFooter(Dictionary<string, int> vals)
        {
            var st = Core.Eapo.Check();
            Color c = Theme.Muted;
            string text, action = null;
            statusAction = null;
            int dacEq;
            if (!st.Installed) { text = "Equalizer APO isn't installed"; action = "Download"; statusAction = () => OpenUrl(Updater.EapoPage); c = Theme.Warn; }
            else if (!st.Writable || !st.Included) { text = "Equalizer APO needs a one-time setup"; action = "Set up"; statusAction = () => { var e = Core.Eapo.Setup(); if (e != null) Shell.Error(e); Core.Changed(); }; c = Theme.Warn; }
            else if (Core.WriteError != null) { text = "Couldn't update Equalizer APO: " + Core.WriteError; c = Theme.Bad; }
            else if (st.OtherLines.Count > 0) { text = "Equalizer APO also applies another EQ"; action = "Turn it off"; statusAction = () => SettingsPage.DisableOthers(Core, Shell); c = Theme.Warn; }
            else if (vals.TryGetValue(Dx1Protocol.DacEq, out dacEq) && dacEq >= 0) { text = "The DAC's own EQ (EQ " + (dacEq + 1) + ") is on and adds to this EQ"; action = "Turn it off"; statusAction = () => SendDacEqOff(); c = Theme.Warn; }
            else if (Core.Updater.EapoLatest != null) { text = "Equalizer APO " + Core.Updater.EapoLatest + " is available"; action = "Download"; statusAction = () => OpenUrl(Updater.EapoPage); }
            else text = "✓  Equalizer APO is ready";
            lblStatus.Text = text;
            lblStatus.ForeColor = c;
            btnStatusAction.Visible = action != null;
            if (action != null) { btnStatusAction.Text = action; btnStatusAction.Width = btnStatusAction.GetPreferredSize(Size.Empty).Width; }

            var up = Core.Updater.Available;
            lblVersion.Text = up != null ? "Update " + up.Tag + " available" : "Version " + Updater.Current;
            lblVersion.ForeColor = up != null ? Theme.Accent : Theme.Muted;
            btnUpdate.Visible = up != null;
        }

        static void OpenUrl(string u) { try { System.Diagnostics.Process.Start(u); } catch { } }
        static string Db(double v) { return v.ToString("0.0") + " dB"; }

        // ----------------------------------------------------------- DAC model --
        void Rebuild(Protocol p)
        {
            builtFor = p;
            busy.Clear();
            if (p == Protocol.Dx5)
            {
                var input = Dx5Protocol.All["dx5.inputType"];
                segInput.SetItems(input.Values.Select(input.Format));
                ddFilter.SetItems(Dx5Protocol.All["dx5.pcmFilter"].Values.Select(Dx5Protocol.All["dx5.pcmFilter"].Format));
            }
            else
            {
                segInput.SetItems(new[] { "USB", "Optical" });
                ddFilter.SetItems(Enumerable.Range(1, 8).Select(i => "Filter " + i));
            }
        }

        void RefreshSettings(Dictionary<string, int> vals, bool connected)
        {
            foreach (var c in new Control[] { segInput, segGain, ddFilter }) c.Enabled = connected;
            if (!connected) return;
            if (builtFor == Protocol.Dx5)
            {
                Select(segInput, vals, "dx5.inputType", Dx5Protocol.All["dx5.inputType"].Values);
                Select(segGain, vals, "dx5.highGain", new[] { 0, 1 });
                Select(ddFilter, vals, "dx5.pcmFilter", Dx5Protocol.All["dx5.pcmFilter"].Values);
            }
            else
            {
                Select(segInput, vals, Dx1Protocol.Input, new[] { 0, 1 });
                Select(segGain, vals, Dx1Protocol.Gain, new[] { 0, 1 });
                Select(ddFilter, vals, Dx1Protocol.Filter, Enumerable.Range(0, 8).ToArray());
            }
        }

        void Select(Control c, Dictionary<string, int> vals, string id, int[] values)
        {
            if (busy.Contains(c)) return;
            int v;
            int i = vals.TryGetValue(id, out v) ? Array.IndexOf(values, v) : -1;
            var seg = c as Segmented;
            if (seg != null) seg.SelectedIndex = i; else ((Dropdown)c).SelectedIndex = i;
        }

        void SendSetting(Control source, string what, int index)
        {
            if (!Shell.Actions.Consent(Shell)) { RefreshState(); return; }
            busy.Add(source);
            Action<string> done = err => { busy.Remove(source); if (err != null) Shell.Error(err); RefreshState(); };
            if (builtFor == Protocol.Dx5)
            {
                if (what == "gain") { Shell.Actions.Send(Dx5Protocol.SetGain(index == 1), done); return; }
                string field = what == "input" ? "inputType" : "pcmFilter";
                var f = Dx5Protocol.All["dx5." + field];
                int raw = f.Values[index];
                Shell.Actions.SendVerified(Dx5Protocol.Set(ToppingTables.Dx5Settable[field].Key, raw), "dx5." + field, raw, f.Format, done);
            }
            else
            {
                var job = what == "input" ? Dx1Protocol.SetInput(index) : what == "gain" ? Dx1Protocol.SetGain(index) : Dx1Protocol.SetFilter(index);
                Shell.Actions.Send(job, done);
            }
        }

        void SendDacEqOff()
        {
            if (!Shell.Actions.Consent(Shell)) return;
            Shell.Actions.Send(Dx1Protocol.SelectEq(-1), err => { if (err != null) Shell.Error(err); RefreshState(); });
        }

        void TogglePower()
        {
            if (!Shell.Actions.Consent(Shell)) return;
            var v = Core.Monitor.Snapshot();
            int pw;
            WriteJob job;
            if (builtFor == Protocol.Dx5) job = Dx5Protocol.SetPower(!(v.TryGetValue("dx5.powered", out pw) && pw == 1));
            else job = Dx1Protocol.SetPower(v.TryGetValue(Dx1Protocol.Power, out pw) && pw == 2);
            Shell.Actions.Send(job, err => { if (err != null) Shell.Error(err); RefreshState(); });
        }

        bool Muted(Dictionary<string, int> v)
        {
            int m;
            if (builtFor == Protocol.Dx5) return v.TryGetValue("dx5.muted", out m) && m == 1;
            return v.TryGetValue(Dx1Protocol.MuteBits, out m) && (m & 1) == 1;
        }

        void ToggleMute()
        {
            if (!Shell.Actions.Consent(Shell)) return;
            var v = Core.Monitor.Snapshot();
            bool mute = !Muted(v);
            busy.Add(btnMute);
            Action<string> done = err => { busy.Remove(btnMute); if (err != null) Shell.Error(err); RefreshState(); };
            if (builtFor == Protocol.Dx5)
                Shell.Actions.SendVerified(Dx5Protocol.Set(ToppingTables.Dx5Settable["muted"].Key, mute ? 1 : 0), "dx5.muted", mute ? 1 : 0,
                                           Dx5Protocol.All["dx5.muted"].Format, done);
            else
                Shell.Actions.Send(Dx1Protocol.SetMute(mute ? (Core.Settings.MuteOptical ? 3 : 1) : 0), done);
        }

        // ------------------------------------------------------------- volume --
        double dacLevel = double.NaN, startLevel = double.NaN, lastSent = double.NaN;
        double? pending;
        bool inFlight;
        int lastAck;
        Timer pace;

        static string Fmt(double db) { return db.ToString("0.0") + " dB"; }
        static double Max(double a, double b) { return double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Max(a, b); }
        bool Interacting() { return slider.Dragging || inFlight || pending.HasValue || unchecked(Environment.TickCount - lastAck) < 600; }

        void WireVolume()
        {
            double warn = ToppingTables.VolWarnDb;
            pace = new Timer { Interval = 25 };
            Disposed += (s, e) => pace.Dispose();
            pace.Tick += (s, e) => { pace.Stop(); Flush(); };
            btnDown.Click += (s, e) => slider.Step(-1);
            btnUp.Click += (s, e) => slider.Step(1);
            Func<double, bool> loud = level => !double.IsNaN(level) && level > warn;
            Func<double> cap = () => loud(startLevel) ? double.PositiveInfinity : warn;

            slider.MouseDown += (s, e) => { startLevel = dacLevel; };
            slider.Changing += () =>
            {
                if (!slider.Dragging) return;   // steps (keys, wheel, buttons) are handled on commit
                double db = slider.Value;
                lblVolume.Text = Fmt(db);
                lblVolume.ForeColor = db > cap() ? Theme.Warn : Theme.Text;
                Send(db > cap() ? cap() : db);   // above -10 dB the DAC holds until you let go and confirm
            };
            slider.Committed += () =>
            {
                double db = slider.Value;
                double from = !double.IsNaN(startLevel) ? startLevel : Max(dacLevel, lastSent);
                double allowed = loud(from) ? double.PositiveInfinity : warn;
                startLevel = double.NaN;
                if (db > allowed)
                {
                    bool hadFocus = slider.Focused;
                    var answer = MessageBox.Show(FindForm(), "Set the volume to " + Fmt(db) + "? That's loud, especially for headphones.",
                                                 Program.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (answer != DialogResult.Yes) slider.Value = Math.Min(warn, db);
                    if (hadFocus) slider.Focus();
                }
                lblVolume.Text = Fmt(slider.Value);
                lblVolume.ForeColor = Theme.Text;
                Send(slider.Value);
            };
        }

        void Send(double db) { pending = db; if (!inFlight && !pace.Enabled) Flush(); }

        void Flush()
        {
            if (inFlight || !pending.HasValue) return;
            double db = pending.Value;
            pending = null;
            if (db == lastSent) return;
            if (!Shell.Actions.Consent(Shell)) { slider.Value = dacLevel; lblVolume.Text = Fmt(dacLevel); return; }
            inFlight = true;
            lastSent = db;
            WriteJob job = builtFor == Protocol.Dx5
                ? Dx5Protocol.SetVolume((int)Math.Round(-db / Dx5Protocol.VolumeStep(Core.Monitor.Snapshot())))
                : Dx1Protocol.SetVolume(Dx1Protocol.VolumeField(Core.Monitor.Snapshot()), db);
            Shell.Actions.Send(job, err =>
            {
                inFlight = false;
                lastAck = Environment.TickCount;
                if (err != null) { pending = null; Shell.Error(err); return; }
                if (pending.HasValue) pace.Start();   // keep up with the slider, ~40 updates a second at most
            });
        }

        void RefreshVolume(Dictionary<string, int> vals, bool connected)
        {
            var h = Core.Monitor.Handler;
            double? db = connected && h != null ? h.VolumeDb(vals) : null;
            slider.Enabled = btnDown.Enabled = btnUp.Enabled = btnMute.Enabled = db.HasValue;
            slider.Steps = builtFor == Protocol.Dx5 ? Dx5Protocol.VolumeSteps(Dx5Protocol.VolumeStep(vals)) : Dx1Protocol.VolumeSteps;
            if (!busy.Contains(btnMute))
            {
                bool muted = Muted(vals);
                btnMute.Icon = muted ? Icons.Muted : Icons.Speaker;
                btnMute.TextColor = muted ? Theme.Warn : (Color?)null;
                btnMute.AccessibleName = muted ? "Unmute" : "Mute";
                btnMute.Invalidate();
            }
            if (!db.HasValue) { lblVolume.Text = "—"; return; }
            dacLevel = db.Value;
            if (Interacting()) return;
            slider.Value = db.Value;
            lastSent = slider.Value;
            lblVolume.Text = Fmt(slider.Value);
            lblVolume.ForeColor = Theme.Text;
        }

        // ------------------------------------------------------------- bits --
        sealed class Dot : Panel
        {
            Color color = Theme.Muted;
            public Color Color { get { return color; } set { if (value != color) { color = value; Invalidate(); } } }
            public Dot() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
                Theme.Smooth(e.Graphics);
                using (var b = new SolidBrush(color)) e.Graphics.FillEllipse(b, 1, 1, Width - 3, Height - 3);
            }
        }

        sealed class Legend : Panel
        {
            public Legend() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
                Theme.Smooth(g);
                float s = Theme.Scale, y = Height / 2f;
                using (var p = new Pen(Theme.Accent, 2 * s)) g.DrawLine(p, 0, y, 14 * s, y);
                var f = Theme.Small;
                int x = (int)(20 * s);
                Theme.DrawText(g, "EQ", f, Theme.Muted, new Rectangle(x, 0, 40, Height), TextFormatFlags.VerticalCenter);
                x += TextRenderer.MeasureText("EQ", f).Width + (int)(14 * s);
                using (var p = new Pen(Color.FromArgb(150, Theme.Accent), 2 * s) { DashPattern = new[] { 2f, 1.5f } }) g.DrawLine(p, x, y, x + 14 * s, y);
                x += (int)(20 * s);
                Theme.DrawText(g, "With loudness", f, Theme.Muted, new Rectangle(x, 0, Width - x, Height), TextFormatFlags.VerticalCenter);
            }
        }
    }
}

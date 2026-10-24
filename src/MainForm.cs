using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DacEqSwitcher
{
    sealed class MainForm : Form
    {
        readonly AppCore core;
        readonly TrayApp app;
        static readonly Color Muted = SystemColors.GrayText;
        static readonly Color Warn = Color.FromArgb(178, 92, 0);
        static readonly Color Bad = Color.FromArgb(190, 30, 30);

        // overview
        Label lblDevice, lblStatus, lblActive, lblWhy, lblDacEq, lblEapo, lblHint;
        Button btnPause, btnSetup, btnOthers, btnConfigurator;
        TableLayoutPanel tblState;
        CheckBox chkStartup, chkUpdates;
        Label lblUpdate;
        Button btnUpdate;
        string stateKey;
        // profiles
        ListBox lstProfiles;
        TextBox txtEq;
        ResponseGraph graph;
        Label lblEqInfo, lblNoProfile;
        Button btnSave, btnRename, btnDelete, btnStore;
        Timer analyseTimer;
        string editing;
        bool dirty, loading;
        // rules
        ListView lvRules;
        ComboBox cboDefault;
        Button btnAdd, btnEdit, btnRemove, btnUp, btnDown;
        Label lblRulesHint;
        TabControl tabs;
        TabPage profilesPage, dacPage;
        DacPanel dacPanel;

        public MainForm(AppCore core, TrayApp app)
        {
            this.core = core;
            this.app = app;
            Text = Program.Name;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 560);
            MinimumSize = new Size(640, 500);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.S && tabs.SelectedTab == profilesPage) { SaveProfile(); e.SuppressKeyPress = true; } };

            tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            dacPanel = new DacPanel(core, new DacActions(core, System.Threading.SynchronizationContext.Current), () => editing);
            dacPage = new TabPage("DAC controls") { UseVisualStyleBackColor = true };
            dacPage.Controls.Add(dacPanel);
            profilesPage = BuildProfiles();
            tabs.TabPages.Add(BuildOverview());
            tabs.TabPages.Add(dacPage);
            tabs.TabPages.Add(profilesPage);
            tabs.TabPages.Add(BuildRules());
            tabs.Selecting += (s, e) => { if (e.TabPage != profilesPage && !ConfirmLeaveEditor()) e.Cancel = true; };
            tabs.SelectedIndexChanged += (s, e) => { RefreshProfiles(); RefreshRules(); RefreshState(); };
            Controls.Add(tabs);

            FormClosing += (s, e) => { if (!ConfirmLeaveEditor()) e.Cancel = true; };
            Shown += (s, e) => { PerformLayout(); FitRuleColumns(); };
            RefreshProfiles();
            RefreshRules();
            RefreshEapo();
            RefreshState();
        }

        // ================================================================ helpers
        static Label Lbl(string text, float size = 0, FontStyle style = FontStyle.Regular, Color? color = null)
        {
            var l = new Label { Text = text, AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
            if (size > 0 || style != FontStyle.Regular) l.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, size > 0 ? size : SystemFonts.MessageBoxFont.Size, style);
            if (color.HasValue) l.ForeColor = color.Value;
            return l;
        }

        static Button Btn(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 1, 6, 1), Margin = new Padding(0, 3, 6, 3), UseVisualStyleBackColor = true };
            b.Click += click;
            return b;
        }

        static FlowLayoutPanel Row(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Dock = DockStyle.Top };
            f.Controls.AddRange(cs);
            return f;
        }

        static TableLayoutPanel Stack()
        {
            var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        /// A titled section: bold heading with its content indented underneath.
        static Control Section(string title, Control content)
        {
            var t = Stack();
            t.Margin = new Padding(0, 16, 0, 0);
            t.Controls.Add(Lbl(title, 0, FontStyle.Bold));
            content.Margin = new Padding(0, 4, 0, 0);
            t.Controls.Add(content);
            return t;
        }

        readonly List<Label> wrapping = new List<Label>();

        /// Label that wraps to the window width.
        Label Wrap(Label l) { wrapping.Add(l); return l; }

        protected override void OnLayout(LayoutEventArgs e)
        {
            int w = Math.Max(200, ClientSize.Width - 70);
            foreach (var l in wrapping) if (l.MaximumSize.Width != w) l.MaximumSize = new Size(w, 0);
            base.OnLayout(e);
        }

        string ProfileLabel(string name)
        {
            if (string.IsNullOrEmpty(name)) return "No EQ";
            return core.Profiles.Exists(name) ? name : name + " (missing)";
        }

        string ConditionText(Condition c)
        {
            var f = core.Field(c.Field);
            return f == null ? c.Field + " = " + c.Value : f.Name + " is " + f.Format(c.Value);
        }

        string RuleText(Rule r)
        {
            return string.Join(" and ", r.Conditions.Select(ConditionText));
        }

        // ================================================================ overview
        TabPage BuildOverview()
        {
            var page = new TabPage("Overview") { UseVisualStyleBackColor = true, AutoScroll = true, Padding = new Padding(16, 12, 16, 12) };
            var col = Stack();

            lblDevice = Lbl("", 12f, FontStyle.Bold);
            lblStatus = Lbl("", 0, FontStyle.Regular, Muted);
            btnPause = Btn("Pause", (s, e) => app.TogglePause());
            var head = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var who = Stack();
            who.Controls.Add(lblDevice);
            who.Controls.Add(lblStatus);
            head.Controls.Add(who, 0, 0);
            head.Controls.Add(btnPause, 1, 0);
            col.Controls.Add(head);

            var active = Stack();
            active.Controls.Add(Lbl("Active EQ", 0, FontStyle.Regular, Muted));
            lblActive = Lbl("", 15f, FontStyle.Bold);
            lblWhy = Lbl("", 0, FontStyle.Regular, Muted);
            active.Controls.Add(lblActive);
            active.Controls.Add(lblWhy);
            active.Margin = new Padding(0, 12, 0, 0);
            col.Controls.Add(active);

            tblState = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
            tblState.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tblState.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var stateBox = Stack();
            stateBox.Controls.Add(tblState);
            lblDacEq = Wrap(Lbl("", 0, FontStyle.Regular, Warn));
            stateBox.Controls.Add(lblDacEq);
            col.Controls.Add(Section("DAC settings", stateBox));

            var eq = Stack();
            lblEapo = Wrap(Lbl(""));
            eq.Controls.Add(lblEapo);
            btnSetup = Btn("Set up Equalizer APO", (s, e) => SetupEapo());
            btnOthers = Btn("Turn off the other EQ…", (s, e) => DisableOthers());
            btnConfigurator = Btn("Open Configurator", (s, e) => { try { core.Eapo.OpenConfigurator(); } catch (Exception ex) { Error(ex.Message); } });
            eq.Controls.Add(Row(btnSetup, btnOthers, btnConfigurator));
            col.Controls.Add(Section("Equalizer APO", eq));

            chkStartup = new CheckBox { Text = "Start with Windows (in the notification area)", AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
            chkStartup.Click += (s, e) => app.SetStartup(chkStartup.Checked);
            col.Controls.Add(chkStartup);

            chkUpdates = new CheckBox { Text = "Check for updates once a day", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            chkUpdates.Click += (s, e) =>
            {
                core.Settings.NoUpdateCheck = !chkUpdates.Checked;
                core.SaveSettings();
                core.Updater.SetEnabled(chkUpdates.Checked);
                if (chkUpdates.Checked) core.Updater.CheckNow();
            };
            col.Controls.Add(chkUpdates);
            lblUpdate = Wrap(Lbl("", 0, FontStyle.Bold, Color.FromArgb(37, 99, 235)));
            btnUpdate = Btn("Install update", (s, e) => app.InstallUpdate(true));
            var upRow = Row(lblUpdate, btnUpdate);
            upRow.Margin = new Padding(0, 8, 0, 0);
            col.Controls.Add(upRow);

            lblHint = Wrap(Lbl("Closing this window keeps " + Program.Name + " running in the notification area.\n" +
                          "Version " + Updater.Current + ". Device support and setting names come from toppingctl (rev " + ToppingTables.Revision + ", Apache-2.0). Not affiliated with Topping.",
                          7.5f, FontStyle.Regular, Muted));
            lblHint.Margin = new Padding(0, 14, 0, 0);
            col.Controls.Add(lblHint);

            page.Controls.Add(col);
            return page;
        }

        public void RefreshState()
        {
            var m = core.Monitor;
            lblDevice.Text = m.Model != null ? m.Model.Name : "Topping DAC";
            lblStatus.Text = m.Status;
            lblStatus.ForeColor = m.Connected ? Color.FromArgb(30, 130, 70) : Muted;
            btnPause.Text = m.Paused ? "Resume" : "Pause";

            lblActive.Text = app.ActiveText;
            if (core.WriteError != null) { lblWhy.Text = "Couldn't update Equalizer APO: " + core.WriteError; lblWhy.ForeColor = Bad; }
            else
            {
                lblWhy.ForeColor = Muted;
                if (!m.Connected) lblWhy.Text = core.ActiveProfile == null ? "" : "Kept while the DAC is disconnected or paused";
                else if (core.ActiveRule != null) lblWhy.Text = "Because " + RuleText(core.ActiveRule);
                else lblWhy.Text = core.Settings.Rules.Count == 0 ? "No rules yet: add them on the Rules tab" : "No rule matches, so the default is used";
            }

            // DAC settings: common ones plus any used in a rule
            HashSet<string> lastSeen;
            var vals = core.Values(out lastSeen);
            var used = new HashSet<string>(core.Settings.Rules.SelectMany(r => r.Conditions).Select(c => c.Field));
            var show = core.Fields.Where(f => f.Common || used.Contains(f.Id)).ToList();
            string key = string.Join(",", show.Select(f => f.Id));
            if (key != stateKey)
            {
                stateKey = key;
                tblState.SuspendLayout();
                tblState.Controls.Clear();
                tblState.RowCount = Math.Max(1, show.Count);
                for (int i = 0; i < show.Count; i++)
                {
                    tblState.Controls.Add(Lbl(show[i].Name, 0, FontStyle.Regular, Muted) , 0, i);
                    var v = Lbl("");
                    v.Margin = new Padding(16, 2, 0, 2);
                    v.Tag = show[i];
                    tblState.Controls.Add(v, 1, i);
                }
                if (show.Count == 0) tblState.Controls.Add(Lbl("Connect a supported Topping DAC to see its settings.", 0, FontStyle.Regular, Muted), 0, 0);
                tblState.ResumeLayout();
            }
            foreach (Control c in tblState.Controls)
            {
                var f = c.Tag as FieldDef;
                if (f == null) continue;
                int v;
                if (vals.TryGetValue(f.Id, out v))
                {
                    c.Text = f.Format(v) + (lastSeen.Contains(f.Id) ? "   (last seen)" : "");
                    c.ForeColor = lastSeen.Contains(f.Id) || !m.Connected ? Muted : ForeColor;
                }
                else
                {
                    c.Text = f.PushOnly ? "Not known yet: it appears once you change it on the DAC" : "—";
                    c.ForeColor = Muted;
                }
            }

            int eqv;
            if (vals.TryGetValue(Dx1Protocol.DacEq, out eqv) && eqv >= 0)
            {
                lblDacEq.Text = "The DAC's own EQ (EQ " + (eqv + 1) + ") is on. It is applied on top of the Equalizer APO EQ.";
                lblDacEq.Visible = true;
            }
            else lblDacEq.Visible = false;
            chkStartup.Checked = Startup.Enabled;
            chkUpdates.Checked = !core.Settings.NoUpdateCheck;
            var up = core.Updater.Available;
            lblUpdate.Text = up == null ? "" : "Update available: " + up.Tag;
            lblUpdate.Visible = btnUpdate.Visible = up != null;
            if (lvRules != null) HighlightActiveRule();
            if (tabs != null && tabs.SelectedTab == dacPage) dacPanel.Refresh(core.Monitor.Snapshot());
        }

        void RefreshEapo()
        {
            var st = core.Eapo.Check();
            btnSetup.Visible = st.Installed && (!st.Writable || !st.Included);
            btnOthers.Visible = st.Installed && st.Writable && st.Included && st.OtherLines.Count > 0;
            btnConfigurator.Visible = st.Installed;
            if (!st.Installed)
            {
                lblEapo.Text = "Equalizer APO wasn't found. Install it from sourceforge.net/projects/equalizerapo, select your DAC in its Configurator, then reopen this window.";
                lblEapo.ForeColor = Warn;
            }
            else if (!st.Writable || !st.Included)
            {
                lblEapo.Text = "One-time setup needed: lets this app change Equalizer APO's EQ (asks for administrator permission once) and adds one line to its config.txt.";
                lblEapo.ForeColor = Warn;
            }
            else if (st.OtherLines.Count > 0)
            {
                lblEapo.Text = "Ready. Equalizer APO's config.txt also applies other EQ, which adds to this app's EQ:\n   " +
                               string.Join("\n   ", st.OtherLines.Take(3)) + (st.OtherLines.Count > 3 ? "\n   …" : "");
                lblEapo.ForeColor = Warn;
            }
            else
            {
                lblEapo.Text = "Ready. Check in Configurator that Equalizer APO is installed on your DAC.";
                lblEapo.ForeColor = Color.FromArgb(30, 130, 70);
            }
        }

        void SetupEapo()
        {
            UseWaitCursor = true;
            string err = core.Eapo.Setup();
            UseWaitCursor = false;
            if (err != null) Error(err);
            RefreshEapo();
            core.Changed();
        }

        void DisableOthers()
        {
            var st = core.Eapo.Check();
            if (MessageBox.Show(this, "These lines in Equalizer APO's config.txt will be turned into comments, so only this app's EQ applies:\n\n" +
                                string.Join("\n", st.OtherLines) + "\n\nA backup of config.txt is kept in the same folder.",
                                Program.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try
            {
                string b = core.Eapo.DisableOtherLines();
                Log.Write("disabled other config lines, backup " + b);
            }
            catch (Exception ex) { Error(ex.Message); }
            RefreshEapo();
        }

        void Error(string msg) { MessageBox.Show(this, msg, Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        // ================================================================ profiles
        TabPage BuildProfiles()
        {
            var page = new TabPage("EQ profiles") { UseVisualStyleBackColor = true, Padding = new Padding(10) };
            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 230 };

            lstProfiles = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            lstProfiles.SelectedIndexChanged += (s, e) => SelectProfile();
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(lstProfiles, 0, 0);
            btnRename = Btn("Rename", (s, e) => RenameProfile());
            btnDelete = Btn("Delete", (s, e) => DeleteProfile());
            var lb = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            btnStore = Btn("Store in DAC…", (s, e) => { if (ConfirmLeaveEditor()) dacPanel.StoreEq(editing); });
            lb.Controls.AddRange(new Control[] { Btn("New", (s, e) => NewProfile()), Btn("Import…", (s, e) => ImportProfiles()), btnRename, btnDelete, btnStore });
            left.Controls.Add(lb, 0, 1);
            split.Panel1.Controls.Add(left);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            txtEq = new TextBox
            {
                Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, AcceptsTab = true, Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9.5f), HideSelection = false,
            };
            try { txtEq.Font = new Font("Consolas", 9.5f); } catch { }
            txtEq.TextChanged += (s, e) => { if (loading) return; SetDirty(true); analyseTimer.Stop(); analyseTimer.Start(); };
            lblNoProfile = Wrap(Lbl("Create a new EQ, or import one from Peace (.peace), Equalizer APO, AutoEQ or Room EQ Wizard (.txt).\n" +
                               "You can also paste Equalizer APO text straight into a new profile.", 0, FontStyle.Regular, Muted));
            lblNoProfile.Dock = DockStyle.Fill;
            var editorHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            editorHost.Controls.Add(txtEq);
            editorHost.Controls.Add(lblNoProfile);
            right.Controls.Add(editorHost, 0, 0);
            graph = new ResponseGraph { Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
            right.Controls.Add(graph, 0, 1);
            lblEqInfo = Lbl("", 0, FontStyle.Regular, Muted);
            lblEqInfo.AutoSize = true;
            lblEqInfo.Anchor = AnchorStyles.Left;
            btnSave = Btn("Save", (s, e) => SaveProfile());
            var bottom = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(lblEqInfo, 0, 0);
            bottom.Controls.Add(btnSave, 1, 0);
            right.Controls.Add(bottom, 0, 2);
            split.Panel2.Controls.Add(right);
            split.Panel2.Padding = new Padding(6, 0, 0, 0);
            page.Controls.Add(split);

            analyseTimer = new Timer { Interval = 300 };
            analyseTimer.Tick += (s, e) => { analyseTimer.Stop(); Analyse(); };
            Disposed += (s, e) => analyseTimer.Dispose();
            return page;
        }

        void RefreshProfiles()
        {
            var names = core.Profiles.Names();
            string keep = editing;
            loading = true;
            lstProfiles.BeginUpdate();
            lstProfiles.Items.Clear();
            foreach (var n in names) lstProfiles.Items.Add(n);
            lstProfiles.EndUpdate();
            loading = false;
            if (keep != null && names.Contains(keep)) lstProfiles.SelectedItem = keep;
            else if (names.Count > 0 && !dirty) lstProfiles.SelectedIndex = 0;
            else ShowEditor(null, null);
        }

        void SelectProfile()
        {
            if (loading) return;
            var name = lstProfiles.SelectedItem as string;
            if (name == editing) return;
            if (!ConfirmLeaveEditor()) { loading = true; lstProfiles.SelectedItem = editing; loading = false; return; }
            string text = "";
            try { text = name == null ? "" : core.Profiles.Read(name); } catch (Exception ex) { Error(ex.Message); }
            ShowEditor(name, text);
        }

        void ShowEditor(string name, string text)
        {
            editing = name;
            bool has = name != null;
            txtEq.Visible = has; lblNoProfile.Visible = !has;
            btnRename.Enabled = btnDelete.Enabled = btnStore.Enabled = has;
            loading = true;
            txtEq.Text = text ?? "";
            loading = false;
            SetDirty(false);
            Analyse();
        }

        void SetDirty(bool d)
        {
            dirty = d && editing != null;
            btnSave.Enabled = dirty;
            btnSave.Text = dirty ? "Save" : "Saved";
        }

        void Analyse()
        {
            if (editing == null) { graph.Data = null; lblEqInfo.Text = ""; return; }
            var r = EqText.Analyse(txtEq.Text);
            graph.Data = r;
            var parts = new List<string> { "Peak " + r.PeakDb.ToString("+0.0;-0.0") + " dB at " + Hz(r.PeakHz) };
            Color c = Muted;
            if (r.PeakDb > 0.05) { parts.Add("may clip: lower the Preamp by " + r.PeakDb.ToString("0.0") + " dB"); c = Warn; }
            var bad = EqText.UnknownLines(txtEq.Text);
            if (bad.Count > 0) { parts.Add("line " + string.Join(", ", bad.Take(5)) + (bad.Count > 5 ? "…" : "") + " not understood by Equalizer APO"); c = Warn; }
            if (r.Skipped > 0) parts.Add(r.Skipped + " line(s) not drawn");
            if (!r.Identical) parts.Add("blue = left, red = right");
            if (editing == core.ActiveProfile) parts.Add("active now");
            lblEqInfo.Text = string.Join("  ·  ", parts);
            lblEqInfo.ForeColor = c;
        }

        static string Hz(double f) { return f >= 1000 ? (f / 1000).ToString("0.0") + " kHz" : f.ToString("0") + " Hz"; }

        bool ConfirmLeaveEditor()
        {
            if (!dirty || editing == null) return true;
            var r = MessageBox.Show(this, "Save changes to “" + editing + "”?", Program.Name, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) SaveProfile(); else SetDirty(false);
            return true;
        }

        void SaveProfile()
        {
            if (editing == null || !dirty) return;
            try { core.Profiles.Save(editing, txtEq.Text); }
            catch (Exception ex) { Error(ex.Message); return; }
            SetDirty(false);
            if (editing == core.ActiveProfile) core.Changed();   // Equalizer APO picks it up now
            Analyse();
        }

        void NewProfile()
        {
            if (!ConfirmLeaveEditor()) return;
            string name = Prompt("Name for the new EQ:", "New EQ");
            if (name == null) return;
            name = core.Profiles.UniqueName(name);
            core.Profiles.Save(name, "Preamp: 0 dB\r\n# Example: Filter: ON PK Fc 1000 Hz Gain -3 dB Q 1.41\r\n");
            editing = null;
            RefreshProfiles();
            lstProfiles.SelectedItem = name;
            txtEq.Focus();
            txtEq.SelectAll();
        }

        void ImportProfiles()
        {
            if (!ConfirmLeaveEditor()) return;
            using (var dlg = new OpenFileDialog
            {
                Title = "Import EQ", Multiselect = true,
                Filter = "EQ files (*.peace;*.txt;*.cfg)|*.peace;*.txt;*.cfg|All files (*.*)|*.*",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string last = null;
                var notes = new List<string>();
                var problems = new List<string>();
                foreach (var file in dlg.FileNames)
                {
                    try
                    {
                        string text = EqText.Import(file, File.ReadAllBytes(file), notes);
                        last = core.Profiles.UniqueName(Path.GetFileNameWithoutExtension(file));
                        core.Profiles.Save(last, text);
                    }
                    catch (ImportException ex) { problems.Add(Path.GetFileName(file) + ": " + ex.Message); }
                    catch (IOException ex) { problems.Add(Path.GetFileName(file) + ": " + ex.Message); }
                }
                editing = null;
                RefreshProfiles();
                if (last != null) lstProfiles.SelectedItem = last;
                if (problems.Count > 0 || notes.Count > 0)
                    MessageBox.Show(this, string.Join("\n\n", problems.Concat(notes)), Program.Name, MessageBoxButtons.OK,
                        problems.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
        }

        void RenameProfile()
        {
            if (editing == null || !ConfirmLeaveEditor()) return;
            string to = Prompt("New name:", editing);
            if (to == null) return;
            to = ProfileStore.CleanName(to);
            if (to.Length == 0 || to == editing) return;
            if (core.Profiles.Exists(to)) { Error("An EQ called “" + to + "” already exists."); return; }
            string from = editing;
            try { core.Profiles.Rename(from, to); } catch (Exception ex) { Error(ex.Message); return; }
            foreach (var r in core.Settings.Rules) if (r.Profile == from) r.Profile = to;
            if (core.Settings.DefaultProfile == from) core.Settings.DefaultProfile = to;
            core.SaveSettings();
            editing = to;
            RefreshProfiles();
            core.Changed();
        }

        void DeleteProfile()
        {
            if (editing == null) return;
            int uses = core.Settings.Rules.Count(r => r.Profile == editing) + (core.Settings.DefaultProfile == editing ? 1 : 0);
            string msg = "Delete the EQ “" + editing + "”?" + (uses > 0 ? "\n\nRules that use it will switch to No EQ." : "");
            if (MessageBox.Show(this, msg, Program.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { core.Profiles.Delete(editing); } catch (Exception ex) { Error(ex.Message); return; }
            foreach (var r in core.Settings.Rules) if (r.Profile == editing) r.Profile = "";
            if (core.Settings.DefaultProfile == editing) core.Settings.DefaultProfile = "";
            core.SaveSettings();
            SetDirty(false);
            editing = null;
            RefreshProfiles();
            core.Changed();
        }

        string Prompt(string label, string value)
        {
            using (var f = new Form
            {
                Text = Program.Name, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = Font, Padding = new Padding(12),
            })
            {
                var t = new TextBox { Text = value, Width = 320 };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                var col = Stack();
                col.Controls.Add(Lbl(label));
                col.Controls.Add(t);
                var buttons = Row(ok, cancel);
                buttons.Dock = DockStyle.None;
                buttons.Anchor = AnchorStyles.Right;
                col.Controls.Add(buttons);
                f.Controls.Add(col);
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(this) == DialogResult.OK && t.Text.Trim().Length > 0 ? t.Text.Trim() : null;
            }
        }

        // ================================================================ rules
        TabPage BuildRules()
        {
            var page = new TabPage("Rules") { UseVisualStyleBackColor = true, Padding = new Padding(10) };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            lblRulesHint = Wrap(Lbl("", 0, FontStyle.Regular, Muted));
            t.Controls.Add(lblRulesHint, 0, 0);

            lvRules = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable };
            lvRules.Columns.Add("When the DAC's", 420);
            lvRules.Columns.Add("use this EQ", 200);
            lvRules.SelectedIndexChanged += (s, e) => UpdateRuleButtons();
            lvRules.DoubleClick += (s, e) => EditRule();
            lvRules.Resize += (s, e) => FitRuleColumns();
            t.Controls.Add(lvRules, 0, 1);

            btnAdd = Btn("Add rule…", (s, e) => AddRule());
            btnEdit = Btn("Edit…", (s, e) => EditRule());
            btnRemove = Btn("Remove", (s, e) => RemoveRule());
            btnUp = Btn("Move up", (s, e) => MoveRule(-1));
            btnDown = Btn("Move down", (s, e) => MoveRule(1));
            t.Controls.Add(Row(btnAdd, btnEdit, btnRemove, btnUp, btnDown), 0, 2);

            cboDefault = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
            cboDefault.SelectionChangeCommitted += (s, e) =>
            {
                var item = cboDefault.SelectedItem as ProfileItem;
                core.Settings.DefaultProfile = item == null ? "" : item.Name;
                core.SaveSettings();
                core.Changed();
            };
            var lbl = Lbl("When no rule matches, use:");
            lbl.Margin = new Padding(0, 7, 8, 0);
            var defRow = Row(lbl, cboDefault);
            defRow.Margin = new Padding(0, 8, 0, 0);
            t.Controls.Add(defRow, 0, 3);
            page.Controls.Add(t);
            return page;
        }

        void FitRuleColumns()
        {
            if (lvRules.Columns.Count != 2) return;
            int w = lvRules.ClientSize.Width;
            lvRules.Columns[1].Width = Math.Max(140, w / 3);
            lvRules.Columns[0].Width = Math.Max(150, w - lvRules.Columns[1].Width - 4);
        }

        sealed class ProfileItem
        {
            public string Name, Label;
            public override string ToString() { return Label; }
        }

        List<ProfileItem> ProfileItems()
        {
            var l = new List<ProfileItem> { new ProfileItem { Name = "", Label = "No EQ" } };
            l.AddRange(core.Profiles.Names().Select(n => new ProfileItem { Name = n, Label = n }));
            return l;
        }

        void RefreshRules()
        {
            if (lvRules == null) return;
            int sel = lvRules.SelectedIndices.Count > 0 ? lvRules.SelectedIndices[0] : -1;
            lvRules.BeginUpdate();
            lvRules.Items.Clear();
            foreach (var r in core.Settings.Rules)
            {
                var it = new ListViewItem(RuleText(r)) { Tag = r };
                it.SubItems.Add(ProfileLabel(r.Profile));
                lvRules.Items.Add(it);
            }
            if (sel >= 0 && sel < lvRules.Items.Count) lvRules.Items[sel].Selected = true;
            lvRules.EndUpdate();
            HighlightActiveRule();

            var items = ProfileItems();
            cboDefault.Items.Clear();
            cboDefault.Items.AddRange(items.ToArray());
            cboDefault.SelectedItem = items.FirstOrDefault(i => i.Name == core.Settings.DefaultProfile) ?? items[0];

            bool known = core.Fields.Count > 0;
            btnAdd.Enabled = known;
            lblRulesHint.Text = known
                ? "Pick which EQ to use for your DAC's settings. Rules are checked from the top; the first one that matches wins. The rule in bold matches right now."
                : "Connect a supported Topping DAC once, so the app can find out which settings it reports. Then you can add rules here.";
            UpdateRuleButtons();
        }

        void HighlightActiveRule()
        {
            foreach (ListViewItem it in lvRules.Items)
            {
                bool on = core.Monitor.Connected && it.Tag == core.ActiveRule;
                if ((it.Font.Bold) != on) it.Font = new Font(lvRules.Font, on ? FontStyle.Bold : FontStyle.Regular);
            }
        }

        void UpdateRuleButtons()
        {
            int i = lvRules.SelectedIndices.Count > 0 ? lvRules.SelectedIndices[0] : -1;
            btnEdit.Enabled = btnRemove.Enabled = i >= 0 && core.Fields.Count > 0;
            btnRemove.Enabled = i >= 0;
            btnUp.Enabled = i > 0;
            btnDown.Enabled = i >= 0 && i < lvRules.Items.Count - 1;
        }

        void AddRule()
        {
            var r = new Rule();
            using (var d = new RuleDialog(core, r, ProfileItemsForDialog()))
                if (d.ShowDialog(this) != DialogResult.OK) return;
            core.Settings.Rules.Add(r);
            CommitRules(core.Settings.Rules.Count - 1);
        }

        void EditRule()
        {
            if (lvRules.SelectedIndices.Count == 0 || core.Fields.Count == 0) return;
            int i = lvRules.SelectedIndices[0];
            var copy = new Rule
            {
                Profile = core.Settings.Rules[i].Profile,
                Conditions = core.Settings.Rules[i].Conditions.Select(c => new Condition { Field = c.Field, Value = c.Value }).ToList(),
            };
            using (var d = new RuleDialog(core, copy, ProfileItemsForDialog()))
                if (d.ShowDialog(this) != DialogResult.OK) return;
            core.Settings.Rules[i] = copy;
            CommitRules(i);
        }

        List<KeyValuePair<string, string>> ProfileItemsForDialog()
        {
            return ProfileItems().Select(p => new KeyValuePair<string, string>(p.Name, p.Label)).ToList();
        }

        void RemoveRule()
        {
            if (lvRules.SelectedIndices.Count == 0) return;
            int i = lvRules.SelectedIndices[0];
            core.Settings.Rules.RemoveAt(i);
            CommitRules(Math.Min(i, core.Settings.Rules.Count - 1));
        }

        void MoveRule(int d)
        {
            if (lvRules.SelectedIndices.Count == 0) return;
            int i = lvRules.SelectedIndices[0], j = i + d;
            var rules = core.Settings.Rules;
            if (j < 0 || j >= rules.Count) return;
            var r = rules[i]; rules[i] = rules[j]; rules[j] = r;
            CommitRules(j);
        }

        void CommitRules(int select)
        {
            core.SaveSettings();
            core.Changed();
            RefreshRules();
            foreach (ListViewItem it in lvRules.SelectedItems) it.Selected = false;
            if (select >= 0 && select < lvRules.Items.Count) { lvRules.Items[select].Selected = true; lvRules.Items[select].EnsureVisible(); }
            stateKey = null;   // the overview shows settings used by rules
            RefreshState();
        }
    }
}

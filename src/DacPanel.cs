using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DacEqSwitcher
{
    /// The DAC tab: every control toppingctl implements for the connected model.
    sealed class DacPanel : Panel
    {
        readonly AppCore core;
        readonly DacActions actions;
        readonly Func<string> selectedProfile;
        readonly TableLayoutPanel grid;
        readonly Label header, status, note;
        readonly List<Action<Dictionary<string, int>>> updaters = new List<Action<Dictionary<string, int>>>();
        readonly HashSet<Control> busy = new HashSet<Control>();   // writes in flight: don't overwrite from the DAC
        Protocol built = (Protocol)(-1);
        bool builtConnected;
        int builtValues;
        int slotsVersion = -1;

        static readonly Color Muted = SystemColors.GrayText;

        public DacPanel(AppCore core, DacActions actions, Func<string> selectedProfile)
        {
            this.core = core;
            this.actions = actions;
            this.selectedProfile = selectedProfile;
            Dock = DockStyle.Fill;
            AutoScroll = true;
            Padding = new Padding(16, 12, 16, 12);

            var col = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top };
            col.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header = new Label { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12f, FontStyle.Bold) };
            col.Controls.Add(header);
            grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 10, 0, 0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            col.Controls.Add(grid);
            status = new Label { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            col.Controls.Add(status);
            note = new Label { AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 10, 0, 0), MaximumSize = new Size(640, 0),
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 7.5f) };
            col.Controls.Add(note);
            Controls.Add(col);
        }

        // ----------------------------------------------------------- helpers --
        void Row(string label, Control c, string hint = null)
        {
            var l = new Label { Text = label, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 7, 16, 3) };
            int r = grid.RowCount++;
            grid.Controls.Add(l, 0, r);
            if (hint != null)
            {
                var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
                c.Margin = new Padding(0, 3, 8, 3);
                f.Controls.Add(c);
                f.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 7, 0, 0) });
                c = f;
            }
            else c.Margin = new Padding(0, 3, 0, 3);
            grid.Controls.Add(c, 1, r);
        }

        void Heading(string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(0, 14, 0, 2) };
            int r = grid.RowCount++;
            grid.Controls.Add(l, 0, r);
            grid.SetColumnSpan(l, 2);
        }

        void Status(string err, string ok)
        {
            status.Text = err ?? ok;
            status.ForeColor = err != null ? Color.FromArgb(190, 30, 30) : Color.FromArgb(30, 130, 70);
        }

        /// Asks once for the DX1 II, whose write support toppingctl marks as not yet fully verified.
        bool Consent()
        {
            if (core.Monitor.Handler is Dx5Protocol || core.Settings.Dx1WriteConsent) return true;
            var r = MessageBox.Show(FindForm(),
                "toppingctl, which this app's DAC control comes from, marks DX1 II control as \"unverified\": " +
                "it was worked out on one unit (firmware 3.07) and each command was checked on that DAC's front panel.\n\n" +
                "The app only sends the commands toppingctl implements, and never factory reset, firmware or scene-save commands.\n\n" +
                "Control your DX1 II from this app?", Program.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r != DialogResult.Yes) return false;
            core.Settings.Dx1WriteConsent = true;
            core.SaveSettings();
            return true;
        }

        void Send(Control source, WriteJob job, string okText)
        {
            if (!Consent()) { Refresh(core.Monitor.Snapshot()); return; }
            busy.Add(source);
            status.Text = "Sending…";
            status.ForeColor = Muted;
            actions.Send(job, err => { busy.Remove(source); Status(err, okText); });
        }

        ComboBox Choice(string fieldId, int[] values, Func<int, string> format, Func<int, WriteJob> write, string label)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
            foreach (var v in values) c.Items.Add(new Item(v, format(v)));
            c.SelectionChangeCommitted += (s, e) =>
            {
                var it = (Item)c.SelectedItem;
                Send(c, write(it.Value), label + " set to " + it.Text + ".");
            };
            updaters.Add(vals =>
            {
                if (busy.Contains(c) || c.DroppedDown) return;
                int v;
                c.SelectedItem = vals.TryGetValue(fieldId, out v) ? c.Items.Cast<Item>().FirstOrDefault(i => i.Value == v) : null;
            });
            return c;
        }

        sealed class Item
        {
            public readonly int Value;
            public readonly string Text;
            public Item(int v, string t) { Value = v; Text = t; }
            public override string ToString() { return Text; }
        }

        /// Volume slider in half-dB units; sends when released. Loud levels need a confirmation.
        Control Volume(Func<Dictionary<string, int>, double?> current, Func<double, double> snap, Func<double, WriteJob> write)
        {
            var bar = new TrackBar { Minimum = -198, Maximum = 0, TickFrequency = 20, SmallChange = 1, LargeChange = 10, Width = 300, AutoSize = true };
            var lbl = new Label { AutoSize = true, Margin = new Padding(6, 10, 0, 0), MinimumSize = new Size(70, 0) };
            bool dragging = false;
            double last = double.NaN;
            Action commit = () =>
            {
                double db = snap(bar.Value / 2.0);
                if (db == last) return;
                if (db > ToppingTables.VolWarnDb && (double.IsNaN(last) || db > last) &&
                    MessageBox.Show(FindForm(), "Set the volume to " + db.ToString("0.0") + " dB? That's loud, especially for headphones.",
                                    Program.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    if (!double.IsNaN(last)) bar.Value = Clamp((int)Math.Round(last * 2));
                    lbl.Text = (double.IsNaN(last) ? db : last).ToString("0.0") + " dB";
                    return;
                }
                last = db;
                Send(bar, write(db), "Volume set to " + db.ToString("0.0") + " dB.");
            };
            bar.Scroll += (s, e) => lbl.Text = snap(bar.Value / 2.0).ToString("0.0") + " dB";
            bar.MouseDown += (s, e) => dragging = true;
            bar.MouseUp += (s, e) => { dragging = false; commit(); };
            bar.KeyUp += (s, e) => commit();
            updaters.Add(vals =>
            {
                var db = current(vals);
                bar.Enabled = db.HasValue;
                if (!db.HasValue || dragging || busy.Contains(bar) || bar.Focused && bar.Capture) return;
                last = db.Value;
                bar.Value = Clamp((int)Math.Round(db.Value * 2));
                lbl.Text = db.Value.ToString("0.0") + " dB";
            });
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            f.Controls.Add(bar);
            f.Controls.Add(lbl);
            return f;
        }

        static int Clamp(int v) { return Math.Max(-198, Math.Min(0, v)); }

        Button ActionButton(string text, Func<WriteJob> job, string ok, string confirm = null)
        {
            var b = new Button { Text = text, AutoSize = true, UseVisualStyleBackColor = true };
            b.Click += (s, e) =>
            {
                if (confirm != null && MessageBox.Show(FindForm(), confirm, Program.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                Send(b, job(), ok);
            };
            return b;
        }

        Button StoreButton()
        {
            var b = new Button { Text = "Store an EQ profile in the DAC…", AutoSize = true, UseVisualStyleBackColor = true };
            b.Click += (s, e) => StoreEq(selectedProfile());
            return b;
        }

        public void StoreEq(string profile)
        {
            if (core.Monitor.Handler == null || !core.Monitor.Connected) { MessageBox.Show(FindForm(), "Connect your DAC first.", Program.Name); return; }
            if (!Consent()) return;
            using (var d = new StoreEqDialog(core, profile))
            {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                Send(this, d.Job, "Stored “" + d.Profile + "” in the DAC" + (d.Target != null ? " (" + d.Target + ")" : "") + ".");
            }
        }

        // ------------------------------------------------------------- build --
        public void Refresh(Dictionary<string, int> vals)
        {
            var h = core.Monitor.Handler;
            var p = !core.Monitor.Connected || h == null ? Protocol.None : h is Dx1Protocol ? Protocol.Dx1 : Protocol.Dx5;
            if (p != built || core.Monitor.Connected != builtConnected || p == Protocol.Dx5 && builtValues == 0 && vals.Count > 0) Build(p);
            var dx1 = h as Dx1Protocol;
            if (dx1 != null && dx1.StoredSlots.Length == 3 && slotsVersion != SlotsVersion(vals)) { Build(p); }
            foreach (var u in updaters) u(vals);
        }

        static int SlotsVersion(Dictionary<string, int> vals)
        {
            int v;
            return vals.TryGetValue(Dx1Protocol.Slots, out v) ? v : 0;
        }

        void Build(Protocol p)
        {
            built = p;
            builtConnected = core.Monitor.Connected;
            var snap = core.Monitor.Snapshot();
            slotsVersion = SlotsVersion(snap);
            builtValues = snap.Count;
            updaters.Clear();
            busy.Clear();
            grid.SuspendLayout();
            foreach (Control c in grid.Controls.Cast<Control>().ToList()) c.Dispose();
            grid.Controls.Clear();
            grid.RowCount = 0;
            status.Text = "";
            header.Text = core.Monitor.Model != null ? core.Monitor.Model.Name : "Your DAC";

            if (p == Protocol.Dx1) BuildDx1((Dx1Protocol)core.Monitor.Handler);
            else if (p == Protocol.Dx5) BuildDx5();
            else
            {
                Row("", new Label { Text = core.Monitor.Paused ? "Paused. Resume on the Overview tab to control the DAC."
                                                               : core.Monitor.Status, AutoSize = true, ForeColor = Muted });
                note.Text = "";
            }
            grid.ResumeLayout();
        }

        void BuildDx1(Dx1Protocol h)
        {
            var output = new Label { AutoSize = true };
            updaters.Add(v => { int o; output.Text = v.TryGetValue(Dx1Protocol.Output, out o) ? h.Fields[0].Format(o) : "—"; });
            Row("Output", output, "(double-press the knob to change it)");

            Row("Volume", Volume(vals =>
            {
                int raw;
                string id = VolumeField(vals) == ToppingTables.Dx1FieldHpVol ? Dx1Protocol.VolHp
                          : VolumeField(vals) == ToppingTables.Dx1FieldLoVol ? Dx1Protocol.VolLo : Dx1Protocol.VolAll;
                return vals.TryGetValue(id, out raw) ? Dx1Protocol.VolumeDb(raw) : (double?)null;
            }, db => Dx1Protocol.VolumeDb(Dx1Protocol.VolumeRaw(db)),
               db => Dx1Protocol.SetVolume(VolumeField(core.Monitor.Snapshot()), db)));

            Row("Mute", Choice(Dx1Protocol.MuteBits, new[] { 0, 1, 2, 3 },
                v => new[] { "Off", "Analog outputs", "Optical output", "Analog + optical" }[v], Dx1Protocol.SetMute, "Mute"));
            Row("Input", Choice(Dx1Protocol.Input, new[] { 0, 1 }, v => v == 0 ? "USB" : "Optical", Dx1Protocol.SetInput, "Input"));
            Row("Headphone gain", Choice(Dx1Protocol.Gain, new[] { 0, 1 }, v => v == 0 ? "Low" : "High", Dx1Protocol.SetGain, "Headphone gain"));
            Row("PCM filter", Choice(Dx1Protocol.Filter, Enumerable.Range(0, 8).ToArray(), v => "Filter " + (v + 1), Dx1Protocol.SetFilter, "PCM filter"));

            var slots = h.StoredSlots;
            Func<int, string> eqName = v => v < 0 ? "Off" : "EQ " + (v + 1) + (slots.Length == 3 ? "  ·  " + slots[v].Name : "");
            var eq = Choice(Dx1Protocol.DacEq, new[] { -1, 0, 1, 2 }, eqName, Dx1Protocol.SelectEq, "The DAC's own EQ");
            Row("DAC's own EQ", eq, "adds to Equalizer APO's EQ");
            Row("", StoreButton());

            var power = new Button { AutoSize = true, UseVisualStyleBackColor = true };
            power.Click += (s, e) =>
            {
                int pw;
                bool standby = core.Monitor.Snapshot().TryGetValue(Dx1Protocol.Power, out pw) && pw == 2;
                Send(power, Dx1Protocol.SetPower(standby), standby ? "Waking the DAC." : "The DAC is going to standby.");
            };
            updaters.Add(v => { int pw; power.Text = v.TryGetValue(Dx1Protocol.Power, out pw) && pw == 2 ? "Wake up" : "Standby"; });
            Row("Power", power);

            note.Text = "Controls are the commands toppingctl implements for the DX1 II (rev " + ToppingTables.Revision + "). " +
                        "Input, gain and filter show once the DAC reports them, which it does when they change.";
        }

        /// toppingctl cmd_vol_dx1 targets: headphone memory, line memory, or both ("all").
        static int VolumeField(Dictionary<string, int> v)
        {
            int linked, output;
            if (v.TryGetValue(Dx1Protocol.VolLinked, out linked) && linked == 1) return ToppingTables.Dx1FieldLoHpVol;
            if (!v.TryGetValue(Dx1Protocol.Output, out output)) return ToppingTables.Dx1FieldLoHpVol;
            return output == 2 ? ToppingTables.Dx1FieldHpVol : output == 1 ? ToppingTables.Dx1FieldLoVol : ToppingTables.Dx1FieldLoHpVol;
        }

        void BuildDx5()
        {
            var all = Dx5Protocol.All;
            var reported = core.Monitor.Snapshot();
            Func<string, bool> has = id => reported.ContainsKey(id);

            Func<Dictionary<string, int>, double> step = v => { int s; return v.TryGetValue("dx5.volumeStep", out s) && s == 1 ? 1.0 : 0.5; };
            Row("Volume", Volume(vals =>
            {
                int raw;
                return vals.TryGetValue("dx5.volume", out raw) ? -raw * step(vals) : (double?)null;
            }, db => { double st = step(core.Monitor.Snapshot()); return -Math.Round(-db / st) * st; },
               db => Dx5Protocol.SetVolume((int)Math.Round(-db / step(core.Monitor.Snapshot())))));

            Row("Headphone gain", Choice("dx5.highGain", new[] { 0, 1 }, v => v == 0 ? "Low" : "High", v => Dx5Protocol.SetGain(v == 1), "Headphone gain"));

            var scenes = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            scenes.Controls.Add(ActionButton("Recall C1", () => Dx5Protocol.Recall(ToppingTables.RecallC1), "Recalled scene C1.",
                "Recall scene C1? It can change volume, input, output and EQ at once."));
            scenes.Controls.Add(ActionButton("Recall C2", () => Dx5Protocol.Recall(ToppingTables.RecallC2), "Recalled scene C2.",
                "Recall scene C2? It can change volume, input, output and EQ at once."));
            Row("Scenes", scenes);
            Row("DAC's EQ", StoreButton());

            var power = new Button { AutoSize = true, UseVisualStyleBackColor = true };
            power.Click += (s, e) =>
            {
                int pw;
                bool on = core.Monitor.Snapshot().TryGetValue("dx5.powered", out pw) && pw == 1;
                Send(power, Dx5Protocol.SetPower(!on), on ? "The DAC is going to standby." : "Waking the DAC.");
            };
            updaters.Add(v => { int pw; power.Text = v.TryGetValue("dx5.powered", out pw) && pw == 0 ? "Wake up" : "Standby"; });
            Row("Power", power);

            Heading("More settings");
            foreach (var kv in ToppingTables.Dx5Settable.OrderBy(k => all.ContainsKey("dx5." + k.Key) ? all["dx5." + k.Key].Name : k.Key))
            {
                string id = "dx5." + kv.Key;
                FieldDef f;
                if (kv.Key == "highGain" || !all.TryGetValue(id, out f) || !has(id) || f.Values.Length == 0) continue;   // setctl: only fields the DAC reports
                int cmd = kv.Value.Key;
                bool unverified = kv.Value.Value == "unchecked" || kv.Value.Value == "register";
                var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
                foreach (var v in f.Values) c.Items.Add(new Item(v, f.Format(v)));
                var fd = f;
                c.SelectionChangeCommitted += (s, e) =>
                {
                    var it = (Item)c.SelectedItem;
                    busy.Add(c);
                    status.Text = "Sending…";
                    status.ForeColor = Muted;
                    actions.SendVerified(Dx5Protocol.Set(cmd, it.Value), id, it.Value, fd.Format, err =>
                    {
                        busy.Remove(c);
                        Status(err, fd.Name + " set to " + it.Text + " (confirmed by the DAC).");
                    });
                };
                updaters.Add(vals =>
                {
                    if (busy.Contains(c) || c.DroppedDown) return;
                    int v;
                    c.SelectedItem = vals.TryGetValue(id, out v) ? c.Items.Cast<Item>().FirstOrDefault(i => i.Value == v) : null;
                });
                Row(f.Name, c, unverified ? "toppingctl: mapping not yet confirmed" : null);
            }
            note.Text = "Controls are the commands toppingctl implements for the DX5 II (rev " + ToppingTables.Revision + "). " +
                        "Every setting under More settings is read back from the DAC to confirm it changed.";
        }
    }

    /// Picks a profile (and DX1 II slot) and checks it fits the DAC's own EQ.
    sealed class StoreEqDialog : Form
    {
        readonly AppCore core;
        readonly ComboBox cboProfile, cboSlot;
        readonly CheckBox chkActivate;
        readonly Label lblCheck;
        readonly Button ok;
        public WriteJob Job { get; private set; }
        public string Profile { get; private set; }
        public string Target { get; private set; }

        public StoreEqDialog(AppCore core, string profile)
        {
            this.core = core;
            Text = "Store an EQ in the DAC";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);

            var col = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            col.Controls.Add(new Label { Text = "EQ profile:", AutoSize = true });
            cboProfile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
            foreach (var n in core.Profiles.Names()) cboProfile.Items.Add(n);
            cboProfile.SelectedItem = profile != null && cboProfile.Items.Contains(profile) ? profile : (cboProfile.Items.Count > 0 ? cboProfile.Items[0] : null);
            col.Controls.Add(cboProfile);

            var dx1 = core.Monitor.Handler as Dx1Protocol;
            if (dx1 != null)
            {
                col.Controls.Add(new Label { Text = "Store it in:", AutoSize = true, Margin = new Padding(0, 10, 0, 0) });
                cboSlot = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
                var slots = dx1.StoredSlots;
                for (int i = 0; i < 3; i++) cboSlot.Items.Add("EQ " + (i + 1) + (slots.Length == 3 ? "  (now “" + slots[i].Name + "”)" : ""));
                int cur;
                cboSlot.SelectedIndex = core.Monitor.Snapshot().TryGetValue(Dx1Protocol.DacEq, out cur) && cur >= 0 ? cur : 0;
                col.Controls.Add(cboSlot);
                chkActivate = new CheckBox { Text = "Turn the DAC's EQ on with this slot afterwards", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
                col.Controls.Add(chkActivate);
                cboSlot.SelectedIndexChanged += (s, e) => Check();
            }
            lblCheck = new Label { AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 10, 0, 0) };
            col.Controls.Add(lblCheck);

            ok = new Button { Text = "Store", AutoSize = true, Margin = new Padding(0, 12, 6, 0) };
            ok.Click += (s, e) => { if (Build()) DialogResult = DialogResult.OK; };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(0, 12, 0, 0) };
            var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            col.Controls.Add(buttons);
            Controls.Add(col);
            AcceptButton = ok;
            CancelButton = cancel;
            cboProfile.SelectedIndexChanged += (s, e) => Check();
            Check();
        }

        List<PeqBand> bands;
        double preamp;

        void Check()
        {
            var name = cboProfile.SelectedItem as string;
            if (name == null) { lblCheck.Text = "Create an EQ profile first."; ok.Enabled = false; return; }
            string text = core.Profiles.Read(name);
            var problems = Peq.Plan(text, core.Monitor.Model.Protocol, out bands, out preamp);
            if (problems.Count > 0)
            {
                lblCheck.ForeColor = Color.FromArgb(178, 92, 0);
                lblCheck.Text = "This EQ doesn't fit the DAC's own EQ:\n• " + string.Join("\n• ", problems.Take(6)) +
                                (problems.Count > 6 ? "\n• …" : "") + "\n\nIt still works through Equalizer APO.";
                ok.Enabled = false;
                return;
            }
            var r = EqText.Analyse(text);
            string where = cboSlot != null ? "EQ " + (cboSlot.SelectedIndex + 1) : "the DAC's EQ";
            lblCheck.ForeColor = SystemColors.ControlText;
            lblCheck.Text = bands.Count + " filter(s), preamp " + preamp.ToString("+0.0;-0.0;0") + " dB. Replaces the curve stored in " + where + "." +
                            (r.PeakDb > 0.05 ? "\n⚠ Peak gain is " + r.PeakDb.ToString("+0.0") + " dB, which can clip: lower its Preamp." : "");
            ok.Enabled = true;
        }

        bool Build()
        {
            Profile = cboProfile.SelectedItem as string;
            if (Profile == null) return false;
            if (core.Monitor.Handler is Dx1Protocol)
            {
                int slot = cboSlot.SelectedIndex, cur;
                int restore = chkActivate.Checked ? slot : (core.Monitor.Snapshot().TryGetValue(Dx1Protocol.DacEq, out cur) ? cur : -1);
                Job = Dx1Protocol.UploadEq(slot, bands, preamp, restore);
                Target = "EQ " + (slot + 1);
            }
            else Job = Dx5Protocol.UploadEq(bands, preamp);
            return true;
        }
    }
}

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
        readonly TableLayoutPanel grid;
        readonly Label header, status, note;
        readonly List<Action<Dictionary<string, int>>> updaters = new List<Action<Dictionary<string, int>>>();
        readonly HashSet<Control> busy = new HashSet<Control>();   // writes in flight: don't overwrite from the DAC
        Protocol built = (Protocol)(-1);
        bool builtConnected;
        int builtValues;
        int slotsVersion = -1;

        static readonly Color Muted = SystemColors.GrayText;

        public DacPanel(AppCore core, DacActions actions)
        {
            this.core = core;
            this.actions = actions;
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

        /// Volume control: a slider with one position per DAC step, -/+ buttons and the level in dB.
        /// The DAC follows the slider live while it's dragged. Going above -10 dB (toppingctl's
        /// loud threshold) needs a confirmation: while dragging, the DAC holds at -10 dB until
        /// you let go and confirm.
        Control Volume(Func<Dictionary<string, int>, double?> current, Func<Dictionary<string, int>, double[]> steps, Func<double, WriteJob> write)
        {
            double warn = ToppingTables.VolWarnDb;
            var slider = new VolumeSlider { Warn = warn, Width = 380, Margin = new Padding(0, 2, 0, 0) };
            var minus = new Button { Text = "\u2212", Width = 30, Height = 28, Margin = new Padding(0, 8, 6, 0), UseVisualStyleBackColor = true };
            var plus = new Button { Text = "+", Width = 30, Height = 28, Margin = new Padding(6, 8, 0, 0), UseVisualStyleBackColor = true };
            var lbl = new Label { AutoSize = true, Margin = new Padding(10, 12, 0, 0), MinimumSize = new Size(150, 0),
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10f, FontStyle.Bold) };
            minus.Click += (s, e) => slider.Step(-1);
            plus.Click += (s, e) => slider.Step(1);

            double dacLevel = double.NaN;       // what the DAC last reported
            double startLevel = double.NaN;     // DAC level when this drag began
            double? pending = null;             // newest level waiting to be sent
            double lastSent = double.NaN;
            bool inFlight = false;
            int lastAck = 0;
            var pace = new Timer { Interval = 25 };
            Disposed += (s, e) => pace.Dispose();

            Func<double, string> fmt = db => db.ToString("0.0") + " dB";
            Func<bool> interacting = () => slider.Dragging || inFlight || pending.HasValue || unchecked(Environment.TickCount - lastAck) < 600;

            Action flush = null;
            flush = () =>
            {
                if (inFlight || !pending.HasValue) return;
                double db = pending.Value;
                pending = null;
                if (db == lastSent) return;
                if (!Consent()) { slider.Value = dacLevel; lbl.Text = fmt(dacLevel); return; }
                inFlight = true;
                lastSent = db;
                busy.Add(slider);
                actions.Send(write(db), err =>
                {
                    inFlight = false;
                    lastAck = Environment.TickCount;
                    busy.Remove(slider);
                    if (err != null) { Status(err, null); pending = null; return; }
                    if (pending.HasValue) pace.Start();   // keep up with the slider, ~40 updates a second at most
                });
            };
            pace.Tick += (s, e) => { pace.Stop(); flush(); };
            Action<double> send = db => { pending = db; if (!inFlight && !pace.Enabled) flush(); };

            // Ask only when crossing above -10 dB; if the DAC is already above it, moving freely is fine.
            Func<double, bool> alreadyLoud = level => !double.IsNaN(level) && level > warn;
            Func<double> cap = () => alreadyLoud(startLevel) ? double.PositiveInfinity : warn;

            slider.MouseDown += (s, e) => { startLevel = dacLevel; };
            slider.Changing += () =>
            {
                double db = slider.Value;
                if (!slider.Dragging) return;   // steps (keys, wheel, buttons) are handled on commit
                lbl.Text = fmt(db);
                if (db > cap())
                {
                    lbl.ForeColor = Color.FromArgb(178, 92, 0);
                    status.Text = "Above " + fmt(warn) + ": the DAC stays at " + fmt(warn) + " until you let go and confirm.";
                    status.ForeColor = Color.FromArgb(178, 92, 0);
                    send(cap());
                }
                else
                {
                    lbl.ForeColor = ForeColor;
                    if (status.Text.StartsWith("Above ")) status.Text = "";
                    send(db);
                }
            };
            slider.Committed += () =>
            {
                double db = slider.Value;
                // after a drag: wherever the DAC was when it began; after a step: where it is now
                double from = !double.IsNaN(startLevel) ? startLevel : Max(dacLevel, lastSent);
                double allowed = alreadyLoud(from) ? double.PositiveInfinity : warn;
                startLevel = double.NaN;
                if (db > allowed)
                {
                    bool hadFocus = slider.Focused;
                    var answer = MessageBox.Show(FindForm(), "Set the volume to " + fmt(db) + "? That's loud, especially for headphones.",
                                                 Program.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (answer != DialogResult.Yes) slider.Value = Math.Min(warn, db);
                    if (hadFocus) slider.Focus();   // keep arrow keys working after the prompt
                }
                lbl.Text = fmt(slider.Value);
                lbl.ForeColor = ForeColor;
                send(slider.Value);
                status.Text = "";
            };

            updaters.Add(vals =>
            {
                var st = steps(vals);
                var db = current(vals);
                slider.Enabled = minus.Enabled = plus.Enabled = db.HasValue;
                if (st != null) slider.Steps = st;
                if (!db.HasValue) { lbl.Text = "\u2014"; return; }
                dacLevel = db.Value;
                if (interacting() || busy.Contains(slider)) return;
                slider.Value = db.Value;
                lastSent = slider.Value;
                lbl.Text = fmt(slider.Value);
            });

            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            f.Controls.AddRange(new Control[] { minus, slider, plus, lbl });
            return f;
        }

        static double Max(double a, double b) { return double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Max(a, b); }

        /// The volume levels a DX1 II can be set to (toppingctl dx1_db_to_raw).
        static readonly double[] Dx1Steps = Enumerable.Range(0, ToppingTables.Dx1VolRawMax + 1)
            .Where(r => Dx1Protocol.VolumeRaw(Dx1Protocol.VolumeDb(r)) == r).Select(Dx1Protocol.VolumeDb).ToArray();

        static double[] UniformSteps(double step)
        {
            int n = (int)Math.Round(99 / step);
            return Enumerable.Range(0, n + 1).Select(i => -99 + i * step).ToArray();
        }

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
            }, vals => Dx1Steps,
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
            }, vals => UniformSteps(step(vals)),
               db => Dx5Protocol.SetVolume((int)Math.Round(-db / step(core.Monitor.Snapshot())))));

            Row("Headphone gain", Choice("dx5.highGain", new[] { 0, 1 }, v => v == 0 ? "Low" : "High", v => Dx5Protocol.SetGain(v == 1), "Headphone gain"));

            var scenes = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            scenes.Controls.Add(ActionButton("Recall C1", () => Dx5Protocol.Recall(ToppingTables.RecallC1), "Recalled scene C1.",
                "Recall scene C1? It can change volume, input, output and EQ at once."));
            scenes.Controls.Add(ActionButton("Recall C2", () => Dx5Protocol.Recall(ToppingTables.RecallC2), "Recalled scene C2.",
                "Recall scene C2? It can change volume, input, output and EQ at once."));
            Row("Scenes", scenes);

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
}

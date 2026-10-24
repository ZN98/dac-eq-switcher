using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DacEqSwitcher
{
    /// Edits one rule: "when <setting> is <value> [and …], use <EQ>".
    sealed class RuleDialog : Form
    {
        readonly AppCore core;
        readonly Rule rule;
        readonly FlowLayoutPanel rows;
        readonly ComboBox cboProfile;
        readonly Dictionary<string, int> now;

        sealed class Item
        {
            public object Key;
            public string Text;
            public override string ToString() { return Text; }
        }

        public RuleDialog(AppCore core, Rule rule, List<KeyValuePair<string, string>> profiles)
        {
            this.core = core;
            this.rule = rule;
            HashSet<string> lastSeen;
            now = core.Values(out lastSeen);

            Text = rule.Conditions.Count == 0 ? "Add rule" : "Edit rule";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);

            var col = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            col.Controls.Add(new Label { Text = "When the DAC's", AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
            rows = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
            col.Controls.Add(rows);
            var add = new LinkLabel { Text = "+ and another setting", AutoSize = true, Margin = new Padding(0, 4, 0, 10) };
            add.LinkClicked += (s, e) => AddRow(null);
            col.Controls.Add(add);

            col.Controls.Add(new Label { Text = "use this EQ:", AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
            cboProfile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
            foreach (var p in profiles) cboProfile.Items.Add(new Item { Key = p.Key, Text = p.Value });
            cboProfile.SelectedItem = cboProfile.Items.Cast<Item>().FirstOrDefault(i => (string)i.Key == rule.Profile) ?? cboProfile.Items[0];
            col.Controls.Add(cboProfile);

            var ok = new Button { Text = "OK", AutoSize = true, Margin = new Padding(0, 14, 6, 0) };
            ok.Click += (s, e) => { if (Collect()) DialogResult = DialogResult.OK; };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
            var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            col.Controls.Add(buttons);
            Controls.Add(col);
            AcceptButton = ok;
            CancelButton = cancel;

            if (rule.Conditions.Count == 0) AddRow(null);
            else foreach (var c in rule.Conditions) AddRow(c);
        }

        void AddRow(Condition c)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            var field = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
            var isLbl = new Label { Text = "is", AutoSize = true, Margin = new Padding(6, 6, 6, 0) };
            var value = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
            var remove = new Button { Text = "✕", Width = 28, Margin = new Padding(6, 0, 0, 0) };
            remove.Click += (s, e) => { if (rows.Controls.Count > 1) { rows.Controls.Remove(row); row.Dispose(); } };

            var fields = core.Fields.ToList();
            if (c != null && !fields.Any(f => f.Id == c.Field)) fields.Add(new FieldDef { Id = c.Field, Name = c.Field });
            foreach (var f in fields) field.Items.Add(new Item { Key = f, Text = f.Name });
            field.SelectedIndexChanged += (s, e) => FillValues(field, value, null);
            var sel = field.Items.Cast<Item>().FirstOrDefault(i => c != null && ((FieldDef)i.Key).Id == c.Field);
            field.SelectedItem = sel ?? (field.Items.Count > 0 ? field.Items[0] : null);
            FillValues(field, value, c);

            row.Controls.AddRange(new Control[] { field, isLbl, value, remove });
            rows.Controls.Add(row);
        }

        void FillValues(ComboBox field, ComboBox value, Condition c)
        {
            value.Items.Clear();
            var f = field.SelectedItem == null ? null : (FieldDef)((Item)field.SelectedItem).Key;
            if (f == null) return;
            int cur;
            bool hasCur = now.TryGetValue(f.Id, out cur);
            var vals = f.Values.ToList();
            if (hasCur && !vals.Contains(cur)) vals.Add(cur);
            if (c != null && !vals.Contains(c.Value)) vals.Add(c.Value);
            foreach (var v in vals)
                value.Items.Add(new Item { Key = v, Text = f.Format(v) + (hasCur && v == cur ? "   (now)" : "") });
            int want = c != null ? c.Value : hasCur ? cur : (vals.Count > 0 ? vals[0] : 0);
            value.SelectedItem = value.Items.Cast<Item>().FirstOrDefault(i => (int)i.Key == want);
        }

        bool Collect()
        {
            var conds = new List<Condition>();
            foreach (Control row in rows.Controls)
            {
                var field = (ComboBox)row.Controls[0];
                var value = (ComboBox)row.Controls[2];
                if (field.SelectedItem == null || value.SelectedItem == null) continue;
                var id = ((FieldDef)((Item)field.SelectedItem).Key).Id;
                if (conds.Any(x => x.Field == id))
                {
                    MessageBox.Show(this, "Each setting can only be used once in a rule.", Program.Name);
                    return false;
                }
                conds.Add(new Condition { Field = id, Value = (int)((Item)value.SelectedItem).Key });
            }
            if (conds.Count == 0) return false;
            rule.Conditions = conds;
            rule.Profile = (string)((Item)cboProfile.SelectedItem).Key;
            return true;
        }
    }
}

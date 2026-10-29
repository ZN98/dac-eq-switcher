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
        public bool DeleteRequested { get; private set; }

        sealed class Item
        {
            public object Key;
            public string Text;
            public override string ToString() { return Text; }
        }

        static ComboBox Combo(int width)
        {
            return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, FlatStyle = FlatStyle.Flat, BackColor = Theme.Inset, ForeColor = Theme.Text, Font = Theme.Body };
        }

        public RuleDialog(AppCore core, Rule rule, bool existing)
        {
            this.core = core;
            this.rule = rule;
            HashSet<string> lastSeen;
            now = core.Values(out lastSeen);

            Text = existing ? "Edit rule" : "New rule";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(18);
            HandleCreated += (s, e) => Theme.DarkTitleBar(this);

            var col = Ui.Column();
            col.Controls.Add(Ui.Text("When the DAC's", Theme.Body, Theme.Muted));
            rows = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 6, 0, 0) };
            col.Controls.Add(rows);
            var add = new Btn("And another setting", Btn.Kinds.Link).WithIcon(Icons.Plus);
            add.Width = add.GetPreferredSize(Size.Empty).Width;
            add.Margin = new Padding(0, 2, 0, 12);
            add.Click += (s, e) => AddRow(null);
            col.Controls.Add(add);

            col.Controls.Add(Ui.Text("use this EQ", Theme.Body, Theme.Muted));
            cboProfile = Combo(300);
            cboProfile.Margin = new Padding(0, 6, 0, 0);
            cboProfile.Items.Add(new Item { Key = "", Text = "No EQ" });
            foreach (var p in core.Profiles.Names()) cboProfile.Items.Add(new Item { Key = p, Text = p });
            cboProfile.SelectedItem = cboProfile.Items.Cast<Item>().FirstOrDefault(i => (string)i.Key == rule.Profile) ?? cboProfile.Items[0];
            col.Controls.Add(cboProfile);

            var ok = new Btn("Save", Btn.Kinds.Accent) { Width = 96, Margin = new Padding(8, 0, 0, 0) };
            ok.Click += (s, e) => { if (Collect()) DialogResult = DialogResult.OK; };
            var cancel = new Btn("Cancel") { Width = 96, DialogResult = DialogResult.Cancel, Margin = new Padding(8, 0, 0, 0) };
            var buttons = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 18, 0, 0) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            if (existing)
            {
                var del = new Btn("Delete rule", Btn.Kinds.Link) { TextColor = Theme.Bad };
                del.Width = del.GetPreferredSize(Size.Empty).Width;
                del.Anchor = AnchorStyles.Left;
                del.Click += (s, e) => { DeleteRequested = true; DialogResult = DialogResult.OK; };
                buttons.Controls.Add(del, 0, 0);
            }
            buttons.Controls.Add(cancel, 2, 0);
            buttons.Controls.Add(ok, 3, 0);
            col.Controls.Add(buttons);
            Controls.Add(col);
            AcceptButton = ok;
            CancelButton = cancel;

            if (rule.Conditions.Count == 0) AddRow(null);
            else foreach (var c in rule.Conditions) AddRow(c);
        }

        void AddRow(Condition c)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
            var field = Combo(200);
            var isLbl = Ui.Text("is", Theme.Body, Theme.Muted);
            isLbl.Margin = new Padding(8, 6, 8, 0);
            var value = Combo(230);
            var remove = new Btn("", Btn.Kinds.Ghost) { Text = "✕", Width = 30, Height = 28, Margin = new Padding(6, 0, 0, 0), AccessibleName = "Remove this setting" };
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

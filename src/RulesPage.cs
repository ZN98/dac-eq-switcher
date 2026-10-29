using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DacEqSwitcher
{
    /// Rules: checked from the top, the first that matches picks the EQ.
    sealed class RulesPage : Page
    {
        readonly FlowLayoutPanel list;
        readonly Dropdown ddDefault;
        readonly Label lblEmpty;
        List<string> defaultItems = new List<string>();
        string key;

        public RulesPage(AppCore core, MainForm shell) : base(core, shell)
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = new Padding(0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

            grid.Controls.Add(Ui.Text("Rules", Theme.F(15f, FontStyle.Bold)), 0, 0);
            var add = new Btn("New rule", Btn.Kinds.Accent).WithIcon(Icons.Plus);
            add.Width = add.GetPreferredSize(Size.Empty).Width;
            add.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            add.Click += (s, e) => { if (AddRule(Core, Shell)) RefreshState(); };
            grid.Controls.Add(add, 1, 0);

            list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = new Padding(0) };
            list.Resize += (s, e) => SizeRows();
            list.HandleCreated += (s, e) => Theme.DarkScrollBars(list);
            lblEmpty = Ui.Text("No rules yet. Add one to pick an EQ from your DAC's settings.", Theme.Body, Theme.Muted);
            grid.Controls.Add(list, 0, 1);
            grid.SetColumnSpan(list, 2);

            var def = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 14, 0, 0) };
            var lbl = Ui.Text("When no rule matches", Theme.Body, Theme.Muted);
            lbl.Anchor = AnchorStyles.Left;
            lbl.Margin = new Padding(0, 0, 14, 0);
            ddDefault = new Dropdown { Width = 260 };
            ddDefault.Committed += i =>
            {
                Core.Settings.DefaultProfile = i <= 0 ? "" : defaultItems[i];
                Core.SaveSettings();
                Core.Changed();
            };
            def.Controls.Add(lbl, 0, 0);
            def.Controls.Add(ddDefault, 1, 0);
            grid.Controls.Add(def, 0, 2);
            Controls.Add(grid);
        }

        public override void RefreshState()
        {
            var rules = Core.Settings.Rules;
            defaultItems = new List<string> { "No EQ" };
            defaultItems.AddRange(Core.Profiles.Names());
            ddDefault.SetItems(defaultItems);
            ddDefault.SelectedIndex = Math.Max(0, defaultItems.IndexOf(Core.Settings.DefaultProfile));

            string k = string.Join("\n", rules.Select(r => Core.RuleText(r) + "|" + r.Profile + "|" + (r == Core.ActiveRule && Core.Monitor.Connected)));
            if (k == key) return;
            key = k;
            list.SuspendLayout();
            foreach (Control c in list.Controls.Cast<Control>().ToList()) if (c != lblEmpty) c.Dispose();
            list.Controls.Clear();
            if (rules.Count == 0) list.Controls.Add(lblEmpty);
            for (int i = 0; i < rules.Count; i++) list.Controls.Add(Row(rules, i));
            list.ResumeLayout();
            SizeRows();
        }

        Control Row(List<Rule> rules, int i)
        {
            var r = rules[i];
            bool active = r == Core.ActiveRule && Core.Monitor.Connected;
            var card = new Card { Height = 66, Padding = new Padding(16, 10, 10, 10), Margin = new Padding(0, 0, 0, 10), Radius = 12 };
            if (active) card.Border = Theme.Accent;
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 2, Margin = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int k = 0; k < 5; k++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var cond = Ui.Text((i + 1) + ".  " + Core.RuleText(r), Theme.Body, Theme.Muted);
            cond.AutoEllipsis = true;
            var prof = Ui.Text((string.IsNullOrEmpty(r.Profile) ? "No EQ" : r.Profile), Theme.F(10.5f, FontStyle.Bold));
            t.Controls.Add(cond, 0, 0);
            t.Controls.Add(prof, 0, 1);
            if (active)
            {
                var now = Ui.Text("Now", Theme.F(8f, FontStyle.Bold), Theme.Accent);
                now.Anchor = AnchorStyles.Right;
                now.Margin = new Padding(0, 0, 12, 0);
                t.Controls.Add(now, 1, 0);
                t.SetRowSpan(now, 2);
            }
            var up = IconBtn(Icons.Up, "Move up", i > 0, () => MoveRule(i, -1));
            var down = IconBtn(Icons.Down, "Move down", i < rules.Count - 1, () => MoveRule(i, 1));
            var edit = new Btn("Edit") { Width = 70, Anchor = AnchorStyles.Right, Margin = new Padding(6, 0, 0, 0) };
            edit.Click += (s, e) => { if (EditRule(Core, Shell, r)) RefreshState(); };
            t.Controls.Add(up, 2, 0); t.SetRowSpan(up, 2);
            t.Controls.Add(down, 3, 0); t.SetRowSpan(down, 2);
            t.Controls.Add(edit, 4, 0); t.SetRowSpan(edit, 2);
            card.Controls.Add(t);
            return card;
        }

        static Btn IconBtn(Icons.Draw icon, string name, bool enabled, Action click)
        {
            var b = new Btn("", Btn.Kinds.Ghost).WithIcon(icon, true);
            b.AccessibleName = name;
            b.Enabled = enabled;
            b.Anchor = AnchorStyles.Right;
            b.Click += (s, e) => click();
            new ToolTip().SetToolTip(b, name);
            return b;
        }

        void SizeRows()
        {
            int w = list.ClientSize.Width - (list.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0) - 2;
            foreach (Control c in list.Controls) if (c is Card) c.Width = Math.Max(200, w);
        }

        void MoveRule(int i, int d)
        {
            var rules = Core.Settings.Rules;
            int j = i + d;
            if (j < 0 || j >= rules.Count) return;
            var r = rules[i]; rules[i] = rules[j]; rules[j] = r;
            Core.SaveSettings();
            Core.Changed();
            RefreshState();
        }

        /// Shows the rule editor for a new rule. True if one was added.
        public static bool AddRule(AppCore core, MainForm shell)
        {
            if (core.Fields.Count == 0)
            {
                shell.Error("Connect your DAC once so the app can find out which settings it reports. Then you can add rules.");
                return false;
            }
            var r = new Rule();
            using (var d = new RuleDialog(core, r, false))
                if (d.ShowDialog(shell) != DialogResult.OK) return false;
            core.Settings.Rules.Add(r);
            core.SaveSettings();
            core.Changed();
            return true;
        }

        /// Edits (or deletes) a rule. True if anything changed.
        public static bool EditRule(AppCore core, MainForm shell, Rule rule)
        {
            var copy = new Rule
            {
                Profile = rule.Profile,
                Conditions = rule.Conditions.Select(c => new Condition { Field = c.Field, Value = c.Value }).ToList(),
            };
            using (var d = new RuleDialog(core, copy, true))
            {
                if (d.ShowDialog(shell) != DialogResult.OK) return false;
                int i = core.Settings.Rules.IndexOf(rule);
                if (i < 0) return false;
                if (d.DeleteRequested) core.Settings.Rules.RemoveAt(i);
                else core.Settings.Rules[i] = copy;
            }
            core.SaveSettings();
            core.Changed();
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DacEqSwitcher
{
    /// EQ profiles: list on the left, editor and response graph on the right.
    sealed class ProfilesPage : Page
    {
        readonly ListBox list;
        readonly TextBox editor;
        readonly ResponseGraph graph;
        readonly Label info, empty;
        readonly Btn btnSave, btnRename, btnDelete, btnFiles;
        readonly Dropdown ddRate;
        readonly ToolTip tip = new ToolTip { AutoPopDelay = 30000 };
        static readonly int[] Rates = { 0, 44100, 48000, 88200, 96000, 176400, 192000 };
        int rateIndex;
        readonly Timer analyseTimer = new Timer { Interval = 300 };
        string editing;
        bool dirty, loading;

        public ProfilesPage(AppCore core, MainForm shell) : base(core, shell)
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var title = Ui.Text("EQ profiles", Theme.F(15f, FontStyle.Bold));
            title.Margin = new Padding(0, 0, 0, 12);
            grid.Controls.Add(title, 0, 0);
            var newBtn = new Btn("New", Btn.Kinds.Accent).WithIcon(Icons.Plus);
            var importBtn = new Btn("Import…");
            foreach (var b in new[] { newBtn, importBtn }) { b.Width = b.GetPreferredSize(Size.Empty).Width; b.Margin = new Padding(8, 0, 0, 0); }
            newBtn.Click += (s, e) => New();
            importBtn.Click += (s, e) => Import();
            var actions = Ui.Row(importBtn, newBtn);
            actions.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            grid.Controls.Add(actions, 1, 0);

            // list
            var listCard = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0), Padding = new Padding(8) };
            list = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.Body,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 36, IntegralHeight = false,
            };
            list.DrawItem += DrawItem;
            list.SelectedIndexChanged += (s, e) => SelectFromList();
            list.HandleCreated += (s, e) => Theme.DarkScrollBars(list);
            listCard.Controls.Add(list);
            grid.Controls.Add(listCard, 0, 1);

            // editor
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            var edCard = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(14, 12, 6, 12) };
            editor = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, AcceptsTab = true,
                BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Text, HideSelection = false,
                Font = new Font(FontFamily.GenericMonospace, 10f),
            };
            try { var c = new Font("Consolas", 10f); if (c.Name == "Consolas") editor.Font = c; } catch { }
            editor.HandleCreated += (s, e) => Theme.DarkScrollBars(editor);
            editor.TextChanged += (s, e) => { if (loading) return; SetDirty(true); analyseTimer.Stop(); analyseTimer.Start(); };
            empty = Ui.Text("No EQ profiles yet. Click New, or Import a Peace (.peace), Equalizer APO, AutoEQ or REW (.txt) file, or an impulse response (.wav, .flac …) for convolution.", Theme.Body, Theme.Muted);
            empty.Dock = DockStyle.Fill;
            empty.AutoSize = false;
            edCard.Controls.Add(editor);
            edCard.Controls.Add(empty);
            right.Controls.Add(edCard, 0, 0);

            var graphCard = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10), Padding = new Padding(10) };
            graph = new ResponseGraph { Dock = DockStyle.Fill };
            graphCard.Controls.Add(graph);
            right.Controls.Add(graphCard, 0, 1);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Margin = new Padding(0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ddRate = new Dropdown { Width = 168, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 0) };
            ddRate.Committed += i => { rateIndex = i; Analyse(); };
            tip.SetToolTip(ddRate, "The sample rate the graph and clipping check use. Equalizer APO works at the rate Windows runs your DAC at.");
            info = Ui.Text("", Theme.Small, Theme.Muted);
            info.AutoSize = false;          // one line under the graph; the full text is in the tooltip
            info.Dock = DockStyle.Fill;
            info.Height = TextRenderer.MeasureText("Ag", Theme.Small).Height + 4;
            info.Margin = new Padding(4, 0, 0, 8);
            info.TextAlign = ContentAlignment.MiddleLeft;
            info.AutoEllipsis = true;
            btnFiles = new Btn("Files");
            btnRename = new Btn("Rename");
            btnDelete = new Btn("Delete");
            btnSave = new Btn("Save", Btn.Kinds.Accent);
            foreach (var b in new[] { btnFiles, btnRename, btnDelete, btnSave }) { b.Width = Math.Max(90, b.GetPreferredSize(Size.Empty).Width); b.Margin = new Padding(8, 0, 0, 0); b.Anchor = AnchorStyles.Right; }
            btnFiles.Click += (s, e) => FilesMenu();
            tip.SetToolTip(btnFiles, "Impulse responses (Convolution) and other files (Include) this EQ uses");
            btnRename.Click += (s, e) => Rename();
            btnDelete.Click += (s, e) => Delete();
            btnSave.Click += (s, e) => Save();
            bottom.Controls.Add(ddRate, 0, 0);
            right.Controls.Add(info, 0, 2);
            bottom.Controls.Add(btnFiles, 2, 0);
            bottom.Controls.Add(btnRename, 3, 0);
            bottom.Controls.Add(btnDelete, 4, 0);
            bottom.Controls.Add(btnSave, 5, 0);
            right.Controls.Add(bottom, 0, 3);
            grid.Controls.Add(right, 1, 1);
            Controls.Add(grid);

            analyseTimer.Tick += (s, e) => { analyseTimer.Stop(); Analyse(); };
            Disposed += (s, e) => analyseTimer.Dispose();
        }

        void DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = e.Graphics;
            Theme.Smooth(g);
            using (var b = new SolidBrush(Theme.Card)) g.FillRectangle(b, e.Bounds);
            bool sel = (e.State & DrawItemState.Selected) != 0;
            var r = new RectangleF(e.Bounds.X + 1, e.Bounds.Y + 2, e.Bounds.Width - 2, e.Bounds.Height - 4);
            if (sel) Theme.Fill(g, Theme.Selected, r, 8);
            string name = (string)list.Items[e.Index];
            bool active = name == Core.ActiveProfile && Core.EqOn;
            var rect = new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - (active ? 70 : 20), e.Bounds.Height);
            Theme.DrawText(g, name, Theme.Body, sel ? Theme.Text : Theme.Muted, rect, TextFormatFlags.VerticalCenter);
            if (active)
            {
                var f = Theme.F(8f, FontStyle.Bold);
                var pr = new RectangleF(e.Bounds.Right - 52, e.Bounds.Y + (e.Bounds.Height - 18) / 2f, 42, 18);
                Theme.Fill(g, Theme.Accent, pr, 7);
                Theme.DrawText(g, "Now", f, Theme.OnAccent, Rectangle.Round(pr), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        public override void RefreshState() { list.Invalidate(); }

        public override void Shown()
        {
            Core.RefreshSampleRate();
            Reload(editing);
        }

        double Rate { get { return rateIndex <= 0 ? EqText.DeviceSampleRate : Rates[rateIndex]; } }

        void FillRates()
        {
            string dev = (EqText.DeviceSampleRate / 1000).ToString("0.###") + " kHz";
            var items = new List<string> { dev + (Core.SampleRateKnown ? " · your DAC" : " · assumed") };
            items.AddRange(Rates.Skip(1).Select(r => (r / 1000.0).ToString("0.###") + " kHz"));
            ddRate.SetItems(items);
            ddRate.SelectedIndex = rateIndex;
        }

        public void Select(string name)
        {
            if (name != null && list.Items.Contains(name)) list.SelectedItem = name;
        }

        void Reload(string keep)
        {
            var names = Core.Profiles.Names();
            loading = true;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var n in names) list.Items.Add(n);
            list.EndUpdate();
            loading = false;
            editing = null;
            if (keep != null && names.Contains(keep)) list.SelectedItem = keep;
            else if (names.Count > 0) list.SelectedIndex = 0;
            else ShowEditor(null, null);
        }

        void SelectFromList()
        {
            if (loading) return;
            var name = list.SelectedItem as string;
            if (name == editing) return;
            if (!CanLeave()) { loading = true; list.SelectedItem = editing; loading = false; return; }
            string text = "";
            try { text = name == null ? "" : Core.Profiles.Read(name); } catch (Exception ex) { Shell.Error(ex.Message); }
            ShowEditor(name, text);
        }

        void ShowEditor(string name, string text)
        {
            editing = name;
            bool has = name != null;
            editor.Visible = has;
            empty.Visible = !has;
            btnRename.Enabled = btnDelete.Enabled = btnFiles.Enabled = has;
            ddRate.Visible = has;
            loading = true;
            editor.Text = text ?? "";
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
            if (editing == null) { graph.Data = null; info.Text = ""; return; }
            FillRates();
            EqText.Response r;
            try { r = EqText.Analyse(editor.Text, 1000, 20, 20000, Core.BaseDirFor(editing), Rate); }
            catch (Exception ex) { graph.Data = null; info.Text = "Couldn't draw this EQ: " + ex.Message; info.ForeColor = Theme.Warn; return; }
            graph.Data = r;
            var parts = new List<string>();
            var details = new List<string>();
            Color c = Theme.Muted;
            if (r.PeakDb > 0.05) { parts.Add("May clip: lower the Preamp by " + Math.Ceiling(r.PeakDb * 10) / 10 + " dB"); c = Theme.Warn; }
            var bad = EqText.UnknownLines(editor.Text);
            if (bad.Count > 0)
            {
                parts.Add("Line " + string.Join(", ", bad.Take(5)) + (bad.Count > 5 ? "…" : "") + " isn't understood by Equalizer APO");
                details.Add("Equalizer APO ignores lines it doesn't recognise. Command names are case-sensitive (Preamp, Filter, GraphicEQ, Convolution …).");
                c = Theme.Warn;
            }
            if (r.Notes.Count > 0)
            {
                parts.Add(r.Notes.Count == 1 ? r.Notes[0] : r.Notes.Count + " notes (hover)");
                details.AddRange(r.Notes);
                if (r.Notes.Any(n => n.Contains("skips it") || n.Contains("wasn't found") || n.Contains("ignores"))) c = Theme.Warn;
            }
            if (!r.Identical) parts.Add("Purple = left, blue = right");
            var files = Core.Profiles.Files(editing);
            if (files.Count > 0) details.Add("Files: " + string.Join(", ", files));
            info.Text = string.Join("  ·  ", parts);
            info.ForeColor = c;
            tip.SetToolTip(info, string.Join("\n", parts.Concat(details).Distinct()));
        }

        void FilesMenu()
        {
            if (editing == null) return;
            var m = Theme.Menu();
            var files = Core.Profiles.Files(editing);
            m.Items.Add("Add file…", null, (s, e) => AddFiles());
            if (files.Count > 0)
            {
                m.Items.Add(new ToolStripSeparator());
                foreach (var f in files)
                {
                    string file = f;
                    var item = new ToolStripMenuItem(file);
                    item.DropDown = Theme.Menu();
                    item.DropDownItems.Add("Insert in the EQ", null, (s, e) => InsertLine(file));
                    item.DropDownItems.Add("Remove", null, (s, e) =>
                    {
                        if (MessageBox.Show(FindForm(), "Remove “" + file + "” from this EQ?", Program.Name, MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                        try { Core.Profiles.RemoveFile(editing, file); } catch (Exception ex) { Shell.Error(ex.Message); return; }
                        if (editing == Core.ActiveProfile) Core.Changed();
                        Analyse();
                    });
                    m.Items.Add(item);
                }
            }
            m.Show(btnFiles, new Point(0, btnFiles.Height));
        }

        void AddFiles()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Add files to “" + editing + "”", Multiselect = true,
                Filter = "Impulse responses (*.wav;*.flac;*.ogg;*.aiff;*.w64;*.caf)|*.wav;*.flac;*.ogg;*.oga;*.aif;*.aiff;*.w64;*.rf64;*.caf|Equalizer APO files (*.txt)|*.txt|All files (*.*)|*.*",
            })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                foreach (var f in dlg.FileNames)
                {
                    try { InsertLine(Core.Profiles.AddFile(editing, f)); }
                    catch (Exception ex) { Shell.Error(Path.GetFileName(f) + ": " + ex.Message); }
                }
                if (editing == Core.ActiveProfile && !dirty) Core.Changed();
            }
        }

        /// Adds "Convolution: file" (audio) or "Include: file" (text) on a new line at the caret.
        void InsertLine(string file)
        {
            string line = (AudioFile.IsAudio(file) ? "Convolution: " : "Include: ") + file;
            int at = editor.SelectionStart;
            string t = editor.Text;
            int lineStart = at > 0 ? t.LastIndexOf('\n', Math.Max(0, at - 1)) + 1 : 0;
            int lineEnd = t.IndexOf('\n', at); if (lineEnd < 0) lineEnd = t.Length;
            bool emptyLine = t.Substring(lineStart, lineEnd - lineStart).Trim().Length == 0;
            string insert = emptyLine ? line : "\r\n" + line;
            int pos = emptyLine ? lineStart : lineEnd;
            editor.Text = t.Substring(0, pos) + insert + t.Substring(pos);
            editor.SelectionStart = pos + insert.Length;
            editor.Focus();
        }

        public override bool CanLeave()
        {
            if (!dirty || editing == null) return true;
            var r = MessageBox.Show(FindForm(), "Save changes to “" + editing + "”?", Program.Name, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) Save(); else SetDirty(false);
            return true;
        }

        public void Save()
        {
            if (editing == null || !dirty) return;
            try { Core.Profiles.Save(editing, editor.Text); }
            catch (Exception ex) { Shell.Error(ex.Message); return; }
            SetDirty(false);
            if (editing == Core.ActiveProfile) Core.Changed();   // Equalizer APO picks it up now
            Analyse();
        }

        void New()
        {
            if (!CanLeave()) return;
            string name = Shell.Prompt("Name for the new EQ:", "New EQ");
            if (name == null) return;
            name = Core.Profiles.UniqueName(name);
            Core.Profiles.Save(name, "Preamp: 0 dB\r\n# Filter: ON PK Fc 1000 Hz Gain -3 dB Q 1.41\r\n");
            Reload(name);
            editor.Focus();
            editor.SelectAll();
        }

        void Import()
        {
            if (!CanLeave()) return;
            using (var dlg = new OpenFileDialog
            {
                Title = "Import EQ", Multiselect = true,
                Filter = "EQ files and impulse responses|*.peace;*.txt;*.cfg;*.wav;*.flac;*.ogg;*.oga;*.aif;*.aiff;*.w64;*.rf64;*.caf|All files (*.*)|*.*",
            })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                string last = null;
                var notes = new List<string>();
                var problems = new List<string>();
                foreach (var file in dlg.FileNames)
                {
                    try
                    {
                        last = Core.Profiles.Import(file, notes);
                    }
                    catch (ImportException ex) { problems.Add(Path.GetFileName(file) + ": " + ex.Message); }
                    catch (IOException ex) { problems.Add(Path.GetFileName(file) + ": " + ex.Message); }
                }
                Reload(last ?? editing);
                if (problems.Count > 0 || notes.Count > 0)
                    MessageBox.Show(FindForm(), string.Join("\n\n", problems.Concat(notes)), Program.Name, MessageBoxButtons.OK,
                        problems.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
        }

        void Rename()
        {
            if (editing == null || !CanLeave()) return;
            string to = Shell.Prompt("New name:", editing);
            if (to == null) return;
            to = ProfileStore.CleanName(to);
            if (to.Length == 0 || to == editing) return;
            if (Core.Profiles.Exists(to)) { Shell.Error("An EQ called “" + to + "” already exists."); return; }
            string from = editing;
            try { Core.Profiles.Rename(from, to); } catch (Exception ex) { Shell.Error(ex.Message); return; }
            Core.Eapo.RemoveSynced(from);
            foreach (var r in Core.Settings.Rules) if (r.Profile == from) r.Profile = to;
            if (Core.Settings.DefaultProfile == from) Core.Settings.DefaultProfile = to;
            Core.SaveSettings();
            Reload(to);
            Core.Changed();
        }

        void Delete()
        {
            if (editing == null) return;
            int uses = Core.Settings.Rules.Count(r => r.Profile == editing) + (Core.Settings.DefaultProfile == editing ? 1 : 0);
            string msg = "Delete the EQ “" + editing + "”?" + (uses > 0 ? "\n\nRules that use it will switch to No EQ." : "");
            if (MessageBox.Show(FindForm(), msg, Program.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { Core.Profiles.Delete(editing); } catch (Exception ex) { Shell.Error(ex.Message); return; }
            Core.Eapo.RemoveSynced(editing);
            foreach (var r in Core.Settings.Rules) if (r.Profile == editing) r.Profile = "";
            if (Core.Settings.DefaultProfile == editing) Core.Settings.DefaultProfile = "";
            Core.SaveSettings();
            SetDirty(false);
            Reload(null);
            Core.Changed();
        }
    }
}

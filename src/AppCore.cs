using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DacEqSwitcher
{
    [DataContract]
    sealed class Condition
    {
        [DataMember] public string Field;
        [DataMember] public int Value;
    }

    [DataContract]
    sealed class Rule
    {
        [DataMember] public List<Condition> Conditions = new List<Condition>();
        [DataMember] public string Profile = "";   // "" = no EQ
    }

    [DataContract]
    sealed class Settings
    {
        [DataMember] public List<Rule> Rules = new List<Rule>();
        [DataMember] public string DefaultProfile = "";
        [DataMember] public Dictionary<string, int> LastSeen = new Dictionary<string, int>();
        [DataMember] public string LastModel;
        [DataMember] public bool Dx1WriteConsent;
        [DataMember] public bool NoUpdateCheck;        // default (false) = check daily
        [DataMember] public string NotifiedUpdate;
        [DataMember] public string NotifiedEapo;
        [DataMember] public bool EqOff;               // master switch; default (false) = EQ on
        [DataMember] public string LastProfile;       // last EQ the rules picked, used until the DAC is seen
        [DataMember] public bool MuteOptical;               // DX1 II mute also mutes the optical output

        void Fix()
        {
            if (Rules == null) Rules = new List<Rule>();
            foreach (var r in Rules) { if (r.Conditions == null) r.Conditions = new List<Condition>(); if (r.Profile == null) r.Profile = ""; }
            if (DefaultProfile == null) DefaultProfile = "";
            if (LastSeen == null) LastSeen = new Dictionary<string, int>();
        }

        static DataContractJsonSerializer Ser()
        {
            return new DataContractJsonSerializer(typeof(Settings), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        }

        public static Settings Load(string path)
        {
            Settings s = null;
            try
            {
                if (File.Exists(path))
                    using (var f = File.OpenRead(path)) s = (Settings)Ser().ReadObject(f);
            }
            catch (Exception ex) { Log.Write("settings: " + ex.Message); }
            s = s ?? new Settings();
            s.Fix();
            return s;
        }

        public void Save(string path)
        {
            string tmp = path + ".tmp";
            using (var f = File.Create(tmp)) Ser().WriteObject(f, this);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
    }

    static class Paths
    {
        public static readonly string Data = Environment.GetEnvironmentVariable("DACEQ_DATA") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DacEqSwitcher");
        public static readonly string Profiles = Path.Combine(Data, "EQ profiles");
        public static readonly string Settings = Path.Combine(Data, "settings.json");
        public static readonly string Log = Path.Combine(Data, "log.txt");
    }

    static class Log
    {
        static readonly object gate = new object();

        public static void Write(string msg)
        {
            lock (gate)
            {
                try
                {
                    var fi = new FileInfo(Paths.Log);
                    if (fi.Exists && fi.Length > 256 * 1024) File.Delete(Paths.Log);
                    File.AppendAllText(Paths.Log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss  ") + msg + "\r\n");
                }
                catch { }
            }
        }
    }

    /// EQ profiles are plain Equalizer APO text files, one per profile.
    sealed class ProfileStore
    {
        public IList<string> Names()
        {
            if (!Directory.Exists(Paths.Profiles)) return new List<string>();
            return Directory.GetFiles(Paths.Profiles, "*.txt").Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        string PathOf(string name) { return Path.Combine(Paths.Profiles, name + ".txt"); }
        public bool Exists(string name) { return !string.IsNullOrEmpty(name) && File.Exists(PathOf(name)); }
        public string Read(string name) { return File.ReadAllText(PathOf(name)); }

        public void Save(string name, string text)
        {
            Directory.CreateDirectory(Paths.Profiles);
            File.WriteAllText(PathOf(name), text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
        }

        public void Delete(string name)
        {
            File.Delete(PathOf(name));
            if (Directory.Exists(FilesDir(name))) Directory.Delete(FilesDir(name), true);
        }

        public void Rename(string from, string to)
        {
            File.Move(PathOf(from), PathOf(to));
            if (Directory.Exists(FilesDir(from))) Directory.Move(FilesDir(from), FilesDir(to));
        }

        // ------------------------------------------------------- attached files --
        /// Files an EQ refers to (Convolution impulse responses, Included files) live next to it.
        /// Relative paths in the EQ start in this folder.
        public string FilesDir(string name) { return Path.Combine(Paths.Profiles, name + " files"); }

        public const string SyncedName = "DAC EQ Switcher profile.txt";   // the EQ's own text, next to its files

        public IList<string> Files(string name)
        {
            string d = FilesDir(name);
            if (!Directory.Exists(d)) return new List<string>();
            return Directory.GetFiles(d, "*", SearchOption.AllDirectories).Select(f => f.Substring(d.Length + 1)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public bool HasFiles(string name) { return !string.IsNullOrEmpty(name) && Files(name).Count > 0; }

        /// Copies a file in and returns the name to use in the EQ (made unique if needed).
        public string AddFile(string name, string source)
        {
            string d = FilesDir(name);
            Directory.CreateDirectory(d);
            string fileName = Path.GetFileName(source), stem = Path.GetFileNameWithoutExtension(source), ext = Path.GetExtension(source);
            string target = Path.Combine(d, fileName);
            if (File.Exists(target) && SameFile(source, target)) return fileName;
            for (int i = 2; File.Exists(target) || fileName.Equals(SyncedName, StringComparison.OrdinalIgnoreCase); i++)
            {
                fileName = stem + " (" + i + ")" + ext;
                target = Path.Combine(d, fileName);
            }
            File.Copy(source, target);
            return fileName;
        }

        public void RemoveFile(string name, string fileName)
        {
            string f = Path.Combine(FilesDir(name), fileName);
            if (File.Exists(f)) File.Delete(f);
            if (Directory.Exists(FilesDir(name)) && !Directory.EnumerateFileSystemEntries(FilesDir(name)).Any()) Directory.Delete(FilesDir(name));
        }

        static bool SameFile(string a, string b)
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }

        /// Imports an EQ file. Audio files become a Convolution EQ; for text files, the files they
        /// refer to (Convolution, Include, resolved like Equalizer APO does) are copied in with them.
        public string Import(string file, List<string> notes)
        {
            string name = UniqueName(Path.GetFileNameWithoutExtension(file));
            if (AudioFile.IsAudio(file))
            {
                string fn = AddFile(name, file);
                Save(name, "Convolution: " + fn + "\r\n");
                return name;
            }
            string text = EqText.Import(file, File.ReadAllBytes(file), notes);
            if (!file.EndsWith(".peace", StringComparison.OrdinalIgnoreCase))
                text = BringFiles(name, text, Path.GetDirectoryName(Path.GetFullPath(file)), notes, 0);
            Save(name, text);
            return name;
        }

        string BringFiles(string name, string text, string fromDir, List<string> notes, int depth)
        {
            var o = new StringBuilder();
            foreach (var raw in EqText.SplitLines(text))
            {
                string cmd = EqText.CommandOf(raw.TrimStart().StartsWith("#") ? "" : raw);
                if ((cmd == "Convolution" || cmd == "Include") && depth < 20)
                {
                    int colon = raw.IndexOf(':');
                    string value = raw.Substring(colon + 1).Trim();
                    if (Path.DirectorySeparatorChar == '/') value = value.Replace('\\', '/');   // Windows paths when testing elsewhere
                    if (value.IndexOf('`') >= 0)
                    {
                        // the file name comes from an expression: bring every file it could pick
                        string dirPart = Path.GetDirectoryName(value) ?? "";
                        if (dirPart.IndexOf('`') < 0)
                        {
                            string dir = Rooted(dirPart) ? dirPart : Path.Combine(fromDir, dirPart);
                            string pattern = Regex.Replace(Path.GetFileName(value), "`[^`]*`", "*");
                            int n = 0;
                            if (Directory.Exists(dir))
                                foreach (var f in Directory.GetFiles(dir, pattern)) { CopyAs(name, f, Path.GetFileName(f)); n++; }
                            if (n > 0 && dirPart.Length > 0) value = Path.GetFileName(value);
                            if (n == 0) Note(notes, "No files matching " + pattern + " were found next to the imported file. Add them with Add file.");
                            o.Append(raw.Substring(0, colon + 1)).Append(' ').Append(value).Append("\r\n");
                            continue;
                        }
                        Note(notes, "\"" + value + "\" picks its file with an expression; add the files it needs with Add file.");
                    }
                    else
                    {
                        string src = Rooted(value) ? value : Path.Combine(fromDir, value);
                        if (File.Exists(src))
                        {
                            string fn;
                            if (cmd == "Include")
                            {
                                // the included file's own references are brought too, relative to it
                                string inner = EqText.Decode(File.ReadAllBytes(src));
                                inner = BringFiles(name, inner, Path.GetDirectoryName(Path.GetFullPath(src)), notes, depth + 1);
                                string tmp = Path.GetTempFileName();
                                File.WriteAllText(tmp, inner, new UTF8Encoding(false));
                                fn = CopyAs(name, tmp, Path.GetFileName(src));
                                File.Delete(tmp);
                            }
                            else fn = AddFile(name, src);
                            o.Append(raw.Substring(0, colon + 1)).Append(' ').Append(fn).Append("\r\n");
                            continue;
                        }
                        Note(notes, Path.GetFileName(value) + " wasn't found next to the imported file. Add it with Add file.");
                    }
                }
                o.Append(raw).Append("\r\n");
            }
            return o.ToString().TrimEnd() + "\r\n";
        }

        string CopyAs(string name, string source, string fileName)
        {
            string d = FilesDir(name);
            Directory.CreateDirectory(d);
            string target = Path.Combine(d, fileName);
            if (File.Exists(target) && SameFile(source, target)) return fileName;
            if (File.Exists(target))
            {
                string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
                for (int i = 2; File.Exists(target); i++) target = Path.Combine(d, fileName = stem + " (" + i + ")" + ext);
            }
            File.Copy(source, target);
            return fileName;
        }

        static bool Rooted(string p) { return p.StartsWith("\\") || p.StartsWith("/") || (p.Length > 1 && p[1] == ':'); }

        static void Note(List<string> notes, string n) { if (notes != null && !notes.Contains(n)) notes.Add(n); }

        public static string CleanName(string s)
        {
            s = (s ?? "").Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }

        public string UniqueName(string wanted)
        {
            string b = CleanName(wanted);
            if (b.Length == 0) b = "EQ";
            string n = b;
            for (int i = 2; Exists(n); i++) n = b + " (" + i + ")";
            return n;
        }
    }

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Value = "DAC EQ Switcher";

        static string Command { get { return "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --tray"; } }

        public static bool Enabled
        {
            get
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(Value) != null; }
                catch { return false; }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(Value, Command); else if (k.GetValue(Value) != null) k.DeleteValue(Value);
                }
            }
        }

        /// Keeps the start-up entry pointing at this exe if it was moved.
        public static void Refresh()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    var cur = k == null ? null : k.GetValue(Value) as string;
                    if (cur != null && cur != Command) k.SetValue(Value, Command);
                }
            }
            catch { }
        }
    }

    /// Picks the EQ from the DAC's state and the user's rules, and hands it to Equalizer APO.
    sealed class AppCore : IDisposable
    {
        public readonly Settings Settings;
        public readonly ProfileStore Profiles = new ProfileStore();
        public readonly Eapo Eapo = new Eapo();
        public readonly DeviceMonitor Monitor = new DeviceMonitor();
        public readonly Updater Updater;
        public event Action StateChanged;

        readonly object evalLock = new object();
        string written;   // what we last wrote, to skip no-op writes

        public string ActiveProfile { get; private set; }   // null until applied; "" = no EQ
        public Rule ActiveRule { get; private set; }
        public string WriteError { get; private set; }

        public AppCore()
        {
            Directory.CreateDirectory(Paths.Data);
            Settings = Settings.Load(Paths.Settings);
            Updater = new Updater(() => Eapo.InstallDir);
            Monitor.Changed += OnMonitor;
            EqText.DefaultBaseDir = Eapo.ConfigDir;
            RefreshSampleRate(true);
        }

        // ---------------------------------------------------------- sample rate --
        /// The rate Windows runs the DAC at (what Equalizer APO processes); graphs use it.
        public bool SampleRateKnown { get; private set; }
        public string AudioDeviceName { get; private set; }
        DateTime rateChecked;

        public void RefreshSampleRate(bool now = false)
        {
            if (!now && (DateTime.UtcNow - rateChecked).TotalSeconds < 5) return;
            rateChecked = DateTime.UtcNow;
            var env = Environment.GetEnvironmentVariable("DACEQ_SAMPLE_RATE");
            int forced;
            if (env != null && int.TryParse(env, out forced)) { EqText.DeviceSampleRate = forced; SampleRateKnown = true; AudioDeviceName = "test"; return; }
            var d = AudioDevices.Find(Monitor.Model != null ? Monitor.Model.Name : Settings.LastModel);
            SampleRateKnown = d != null;
            if (d != null) { EqText.DeviceSampleRate = d.SampleRate; AudioDeviceName = d.Name; }
        }

        /// Where an EQ's relative paths start: its own files, else Equalizer APO's config folder.
        public string BaseDirFor(string profile)
        {
            return Profiles.HasFiles(profile) ? Profiles.FilesDir(profile) : Eapo.ConfigDir;
        }

        public void Start()
        {
            if (!EqOn) Evaluate(true);   // stay bypassed from the moment the app starts
            Monitor.Start();
            Updater.Changed += () => { var h = StateChanged; if (h != null) h(); };
            Updater.SetEnabled(!Settings.NoUpdateCheck);
        }

        public void SaveSettings()
        {
            try { Settings.Save(Paths.Settings); } catch (Exception ex) { Log.Write("save settings: " + ex.Message); }
        }

        public IList<FieldDef> Fields
        {
            get
            {
                var h = Monitor.Handler ?? (Settings.LastModel == null ? null : ProtocolHandler.For(ModelProtocol(Settings.LastModel)));
                return h == null ? new List<FieldDef>() : h.Fields;
            }
        }

        static Protocol ModelProtocol(string key)
        {
            var m = ToppingTables.Models.FirstOrDefault(x => x.Key == key);
            return m == null ? Protocol.None : m.Protocol;
        }

        public FieldDef Field(string id) { return Fields.FirstOrDefault(f => f.Id == id); }

        public string ConditionText(Condition c)
        {
            var f = Field(c.Field);
            return f == null ? c.Field + " = " + c.Value : f.Name + " is " + f.Format(c.Value);
        }

        public string RuleText(Rule r) { return string.Join(" and ", r.Conditions.Select(ConditionText)); }

        /// Current values; push-only settings not reported this session use their last seen value.
        public Dictionary<string, int> Values(out HashSet<string> lastSeen)
        {
            var v = Monitor.Snapshot();
            lastSeen = new HashSet<string>();
            foreach (var f in Fields.Where(f => f.PushOnly && !v.ContainsKey(f.Id)))
            {
                int x;
                if (Settings.LastSeen.TryGetValue(f.Id, out x)) { v[f.Id] = x; lastSeen.Add(f.Id); }
            }
            return v;
        }

        void OnMonitor()
        {
            if (Monitor.Connected && Monitor.Model != null && Settings.LastModel != Monitor.Model.Key)
            {
                Settings.LastModel = Monitor.Model.Key;
                SaveSettings();
            }
            var snap = Monitor.Snapshot();
            bool remember = false;
            foreach (var f in Fields.Where(f => f.PushOnly))
            {
                int v, old;
                if (snap.TryGetValue(f.Id, out v) && (!Settings.LastSeen.TryGetValue(f.Id, out old) || old != v))
                { Settings.LastSeen[f.Id] = v; remember = true; }
            }
            if (remember) SaveSettings();
            Evaluate(false);
            var h = StateChanged;
            if (h != null) h();
        }

        public static bool Matches(Rule r, Dictionary<string, int> values)
        {
            int v;
            return r.Conditions.Count > 0 && r.Conditions.All(c => values.TryGetValue(c.Field, out v) && v == c.Value);
        }

        public bool EqOn { get { return !Settings.EqOff; } }

        /// Master switch: off bypasses Equalizer APO's EQ; on restores what the rules pick.
        public void SetEqOn(bool on)
        {
            if (on == EqOn) return;
            Settings.EqOff = !on;
            SaveSettings();
            Log.Write(on ? "EQ switched on" : "EQ switched off");
            Changed();
        }

        const string OffText = "# DAC EQ Switcher: EQ switched off\r\n";
        string writtenKey;   // what the active file holds: "off" or "on:<profile>"

        /// Re-picks the EQ. force: rewrite even if the same profile (after it was edited).
        public void Evaluate(bool force)
        {
            lock (evalLock)
            {
                if (Monitor.Connected)
                {
                    HashSet<string> ls;
                    var vals = Values(out ls);
                    var rule = Settings.Rules.FirstOrDefault(r => Matches(r, vals));
                    string target = rule != null ? rule.Profile : Settings.DefaultProfile;
                    if (!Profiles.Exists(target)) target = "";
                    ActiveRule = rule;
                    if (ActiveProfile != target) Log.Write("EQ -> " + (target == "" ? "(none)" : target));
                    ActiveProfile = target;
                    if (Settings.LastProfile != target) { Settings.LastProfile = target; SaveSettings(); }
                }
                else if (ActiveProfile == null && Settings.LastProfile != null)
                    ActiveProfile = Profiles.Exists(Settings.LastProfile) ? Settings.LastProfile : "";
                // While the DAC is away the last EQ is kept; until one is known there's nothing to apply
                if (EqOn && ActiveProfile == null) return;
                string key = EqOn ? "on:" + ActiveProfile : "off";
                if (!force && key == writtenKey && WriteError == null) return;

                string text;
                if (!EqOn) text = OffText;
                else if (ActiveProfile == "") text = "# DAC EQ Switcher: no EQ for the DAC's current settings\r\n";
                else
                {
                    string body;
                    try { body = Profiles.Read(ActiveProfile); }
                    catch (Exception ex) { body = "# couldn't read profile: " + ex.Message + "\r\n"; }
                    string header = "# DAC EQ Switcher: active EQ \"" + ActiveProfile + "\" (edit it in the app, not here)\r\n";
                    if (Profiles.HasFiles(ActiveProfile))
                    {
                        // an EQ with its own files (impulse responses, included files) is copied into a folder
                        // of Equalizer APO's config, where its relative paths work and the audio service can read it
                        string include, syncErr;
                        if (!Eapo.SyncProfile(ActiveProfile, body, Profiles.FilesDir(ActiveProfile), out include, out syncErr))
                        {
                            WriteError = syncErr;
                            return;
                        }
                        text = header + "Include: " + include + "\r\nChannel: all\r\n";
                    }
                    else text = header + body.TrimEnd() + "\r\nChannel: all\r\n";
                }
                string err = null;
                if (force || text != written)
                {
                    if (Eapo.WriteActive(text, out err)) written = text;
                }
                WriteError = err;
                if (err == null) writtenKey = key;
            }
        }

        public void Changed()
        {
            Evaluate(true);
            var h = StateChanged;
            if (h != null) h();
        }

        public void Dispose()
        {
            Updater.Dispose();
            Monitor.Dispose();
        }
    }
}

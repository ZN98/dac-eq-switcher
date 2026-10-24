using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
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

        public void Delete(string name) { File.Delete(PathOf(name)); }
        public void Rename(string from, string to) { File.Move(PathOf(from), PathOf(to)); }

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
        public readonly Updater Updater = new Updater();
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
            Monitor.Changed += OnMonitor;
        }

        public void Start()
        {
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

        /// Re-picks the EQ. force: rewrite even if the same profile (after it was edited).
        public void Evaluate(bool force)
        {
            lock (evalLock)
            {
                if (!Monitor.Connected) return;   // keep the current EQ while the DAC is away
                HashSet<string> ls;
                var vals = Values(out ls);
                var rule = Settings.Rules.FirstOrDefault(r => Matches(r, vals));
                string target = rule != null ? rule.Profile : Settings.DefaultProfile;
                if (!Profiles.Exists(target)) target = "";
                ActiveRule = rule;
                if (!force && target == ActiveProfile && WriteError == null) return;

                string text;
                if (target == "") text = "# DAC EQ Switcher: no EQ for the DAC's current settings\r\n";
                else
                {
                    string body;
                    try { body = Profiles.Read(target); }
                    catch (Exception ex) { body = "# couldn't read profile: " + ex.Message + "\r\n"; }
                    text = "# DAC EQ Switcher: active EQ \"" + target + "\" (edit it in the app, not here)\r\n"
                         + body.TrimEnd() + "\r\nChannel: all\r\n";
                }
                string err = null;
                if (force || text != written)
                {
                    if (Eapo.WriteActive(text, out err)) written = text;
                }
                WriteError = err;
                if (ActiveProfile != target) Log.Write("EQ -> " + (target == "" ? "(none)" : target));
                ActiveProfile = target;
            }
        }

        public void Changed()
        {
            Evaluate(true);
            var h = StateChanged;
            if (h != null) h();
        }

        public void Dispose() { Updater.Dispose(); Monitor.Dispose(); }
    }
}

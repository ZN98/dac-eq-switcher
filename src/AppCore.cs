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
        [DataMember] public double? LoudnessRefDb;  // DAC volume at which this EQ needs no loudness compensation
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
        [DataMember] public bool LoudnessOff;         // default (false) = on, once a reference volume is set
        [DataMember] public double? DefaultLoudnessRefDb;   // reference when no rule matches
        [DataMember] public int LoudnessStrength = 100;     // % of the ISO 226 correction (100 = the standard)
        [DataMember] public int LoudnessMaxBoost = 12;      // dB; also the headroom kept at every volume
        [DataMember] public int LoudnessRefPhon = 80;       // level music is balanced for
        [DataMember] public int LoudnessVersion;            // 2: 100 % strength; 3: fixed headroom
        [DataMember] public bool MuteOptical;               // DX1 II mute also mutes the optical output

        void Fix()
        {
            if (Rules == null) Rules = new List<Rule>();
            foreach (var r in Rules) { if (r.Conditions == null) r.Conditions = new List<Condition>(); if (r.Profile == null) r.Profile = ""; }
            if (DefaultProfile == null) DefaultProfile = "";
            if (LastSeen == null) LastSeen = new Dictionary<string, int>();
            if (LoudnessVersion < 2)
            {
                // 2.2.12 defaulted to 60 %; start everyone on the standard
                LoudnessStrength = 100; LoudnessRefPhon = 80; LoudnessVersion = 2;
            }
            if (LoudnessVersion < 3) { LoudnessMaxBoost = 12; LoudnessVersion = 3; }   // headroom is now reserved for it
            if (LoudnessStrength <= 0 || LoudnessStrength > 100) LoudnessStrength = 100;
            if (LoudnessMaxBoost < 3 || LoudnessMaxBoost > 30) LoudnessMaxBoost = 12;
            if (LoudnessRefPhon < 60 || LoudnessRefPhon > (int)Loudness.MaxRefPhon) LoudnessRefPhon = 80;
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
        string writtenKey;   // what the active file holds

        // ------------------------------------------------------------- loudness --
        public sealed class LoudnessState
        {
            public bool On, HasReference;
            public double? RefDb, VolumeDb;
            public Loudness.Plan Plan;   // what's applied; null when nothing is added
        }

        double appliedBelow = -2;        // DAC volume below the reference the file is for; -1 = loudness inactive, -2 = unknown
        string planKey, headroomKey;
        double headroom;
        Loudness.Plan plan;
        System.Threading.Timer loudTimer;
        public LoudnessState LoudnessNow { get; private set; }

        public bool LoudnessOn { get { return !Settings.LoudnessOff; } }

        double? ReferenceFor(Rule r) { return r != null ? r.LoudnessRefDb : Settings.DefaultLoudnessRefDb; }

        public double? CurrentVolumeDb()
        {
            var h = Monitor.Handler;
            return h == null || !Monitor.Connected ? null : h.VolumeDb(Monitor.Snapshot());
        }

        public void SetLoudnessOn(bool on)
        {
            if (on == LoudnessOn) return;
            Settings.LoudnessOff = !on;
            SaveSettings();
            Changed();
        }

        /// Makes the current DAC volume the reference for the EQ that's playing now.
        public bool SetLoudnessReference()
        {
            var v = CurrentVolumeDb();
            if (!v.HasValue) return false;
            if (ActiveRule != null) ActiveRule.LoudnessRefDb = v.Value; else Settings.DefaultLoudnessRefDb = v.Value;
            SaveSettings();
            Changed();
            return true;
        }

        Loudness.Plan PlanFor(double below, string profile, string body)
        {
            if (below < 0) return null;
            string hk = profile + "|" + body + "|" + Settings.LoudnessStrength + "/" + Settings.LoudnessMaxBoost + "/" + Settings.LoudnessRefPhon;
            if (hk != headroomKey)
            {
                headroom = Loudness.Headroom(body, Settings.LoudnessRefPhon, Settings.LoudnessStrength / 100.0, Settings.LoudnessMaxBoost);
                headroomKey = hk;
            }
            string k = below + "|" + hk;
            if (k != planKey)
            {
                plan = Loudness.Make(headroom, below, Settings.LoudnessRefPhon, Settings.LoudnessStrength / 100.0, Settings.LoudnessMaxBoost);
                planKey = k;
            }
            return plan;
        }

        /// Re-picks the EQ. force: rewrite even if nothing changed (after a profile was edited).
        /// settle: apply a loudness change now rather than waiting for the volume to stop moving.
        public void Evaluate(bool force, bool settle = false)
        {
            lock (evalLock)
            {
                double? volume = null;
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
                    volume = Monitor.Handler != null ? Monitor.Handler.VolumeDb(vals) : null;
                }
                else if (ActiveProfile == null && Settings.LastProfile != null)
                    ActiveProfile = Profiles.Exists(Settings.LastProfile) ? Settings.LastProfile : "";

                // Loudness: follows the DAC volume in 0.5 dB steps below the reference; held while the DAC is away
                double? reference = ReferenceFor(ActiveRule);
                double below = appliedBelow;
                if (Monitor.Connected)
                    below = LoudnessOn && reference.HasValue && volume.HasValue ? Math.Max(0, Math.Round((reference.Value - volume.Value) * 2) / 2) : -1;
                string baseKey = EqOn ? "on:" + ActiveProfile : "off";
                bool profileChanged = writtenKey == null || !writtenKey.StartsWith(baseKey + "|");
                if (below != appliedBelow)
                {
                    if (settle || force || profileChanged || appliedBelow < 0 || below < 0) appliedBelow = below;
                    else
                    {
                        // the knob is moving: wait until it settles so Equalizer APO isn't reloaded constantly
                        if (loudTimer == null) loudTimer = new System.Threading.Timer(_ => { Evaluate(false, true); Raise(); });
                        loudTimer.Change(300, System.Threading.Timeout.Infinite);
                    }
                }
                // While the DAC is away the last EQ is kept; until one is known there's nothing to apply
                if (EqOn && ActiveProfile == null)
                {
                    LoudnessNow = new LoudnessState { On = LoudnessOn, HasReference = reference.HasValue, RefDb = reference, VolumeDb = volume };
                    return;
                }
                string body = "";
                if (EqOn && ActiveProfile != "")
                {
                    try { body = Profiles.Read(ActiveProfile); }
                    catch (Exception ex) { body = "# couldn't read profile: " + ex.Message + "\r\n"; }
                }
                var p = EqOn ? PlanFor(appliedBelow, ActiveProfile, body) : null;
                LoudnessNow = new LoudnessState { On = LoudnessOn, HasReference = reference.HasValue, RefDb = reference, VolumeDb = volume, Plan = p };

                string key = baseKey + "|" + (p == null ? "-" : planKey);
                if (!force && key == writtenKey && WriteError == null) return;

                string text;
                if (!EqOn) text = OffText;
                else
                {
                    if (ActiveProfile == "") text = "# DAC EQ Switcher: no EQ for the DAC's current settings\r\n";
                    else text = "# DAC EQ Switcher: active EQ \"" + ActiveProfile + "\" (edit it in the app, not here)\r\n"
                              + body.TrimEnd() + "\r\nChannel: all\r\n";
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    string loud = p == null ? null : Loudness.ApoText(p, "Loudness compensation (ISO 226): " + p.KnobBelow.ToString("0.0", inv)
                                                      + " dB below reference, " + p.HeadroomDb.ToString("0.00", inv) + " dB headroom kept at every volume");
                    if (loud != null) text += "\r\n" + loud;
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

        void Raise()
        {
            var h = StateChanged;
            if (h != null) h();
        }

        public void Changed()
        {
            Evaluate(true);
            var h = StateChanged;
            if (h != null) h();
        }

        public void Dispose()
        {
            if (loudTimer != null) loudTimer.Dispose();
            Updater.Dispose();
            Monitor.Dispose();
        }
    }
}

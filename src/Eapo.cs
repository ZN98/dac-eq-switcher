using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace DacEqSwitcher
{
    /// Equalizer APO integration: one file in its config folder, included from config.txt.
    sealed class Eapo
    {
        public const string ActiveName = "daceqswitcher_active.txt";
        public const string IncludeLine = "Include: " + ActiveName;
        const string BlockComment = "# DAC EQ Switcher: the next line loads the EQ it picks (delete it to stop)";
        const string DisabledPrefix = "# (disabled by DAC EQ Switcher) ";
        static readonly Encoding Utf8 = new UTF8Encoding(false);

        public string InstallDir { get; private set; }
        public string ConfigDir { get; private set; }
        public string ConfigTxt { get { return Path.Combine(ConfigDir, "config.txt"); } }
        public string ActivePath { get { return Path.Combine(ConfigDir, ActiveName); } }

        public sealed class State
        {
            public bool Installed, Writable, Included;
            public List<string> OtherLines = new List<string>();   // other active config lines (stack with ours)
        }

        public Eapo() { Locate(); }

        public void Locate()
        {
            string env = Environment.GetEnvironmentVariable("DACEQ_EAPO_CONFIG");
            if (!string.IsNullOrEmpty(env)) { ConfigDir = env; InstallDir = Path.GetDirectoryName(env); return; }
            string install = null, config = null;
            try
            {
                using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hk.OpenSubKey(@"SOFTWARE\EqualizerAPO"))
                {
                    if (k != null) { install = k.GetValue("InstallPath") as string; config = k.GetValue("ConfigPath") as string; }
                }
            }
            catch { }
            if (string.IsNullOrEmpty(install))
            {
                string pf = Environment.GetEnvironmentVariable("ProgramW6432");
                if (string.IsNullOrEmpty(pf)) pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                install = Path.Combine(pf, "EqualizerAPO");
            }
            InstallDir = install;
            ConfigDir = string.IsNullOrEmpty(config) ? Path.Combine(install, "config") : config;
        }

        public State Check()
        {
            var st = new State { Installed = Directory.Exists(ConfigDir) };
            if (!st.Installed) return st;
            st.Writable = CanWrite(ConfigDir);
            try
            {
                foreach (var raw in File.ReadAllLines(ConfigTxt))
                {
                    string s = raw.Trim();
                    if (s.Length == 0 || s.StartsWith("#")) continue;
                    if (s.Equals(IncludeLine, StringComparison.OrdinalIgnoreCase)) { st.Included = true; continue; }
                    if (s.StartsWith("Device:", StringComparison.OrdinalIgnoreCase)) continue;
                    st.OtherLines.Add(s);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return st;
        }

        /// Checks write access without changing anything (a new file in this folder
        /// would make Equalizer APO reload its configuration).
        bool CanWrite(string dir)
        {
            string f = Path.Combine(dir, ActiveName);
            if (!File.Exists(f)) f = Path.Combine(dir, "config.txt");
            if (!File.Exists(f)) return false;
            try { using (new FileStream(f, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } return true; }
            catch { return false; }
        }

        static bool CanCreateIn(string dir)
        {
            string probe = Path.Combine(dir, ".daceqswitcher_probe");
            try { File.WriteAllBytes(probe, new byte[0]); File.Delete(probe); return true; }
            catch { return false; }
        }

        /// Writes the EQ Equalizer APO should use. Equalizer APO notices the change and reloads.
        public bool WriteActive(string text, out string error)
        {
            error = null;
            var bytes = Utf8.GetBytes(text);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(ActivePath))
                    {
                        var old = File.ReadAllBytes(ActivePath);
                        if (old.SequenceEqual(bytes)) return true;   // nothing to reload
                    }
                    File.WriteAllBytes(ActivePath, bytes);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (attempt >= 3 || ex is UnauthorizedAccessException) { error = ex.Message; return false; }
                    System.Threading.Thread.Sleep(15);   // Equalizer APO may be reading it
                }
            }
        }

        public const string ProfilesFolder = "DAC EQ Switcher";

        /// Copies an EQ and its files to "config\DAC EQ Switcher\<name>\" (only what changed, so
        /// Equalizer APO doesn't reload needlessly). Returns the Include path relative to config.
        public bool SyncProfile(string name, string body, string filesDir, out string include, out string error)
        {
            include = ProfilesFolder + "\\" + name + "\\" + ProfileStore.SyncedName;
            error = null;
            try
            {
                string target = Path.Combine(ConfigDir, ProfilesFolder, name);
                Directory.CreateDirectory(target);
                var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProfileStore.SyncedName };
                foreach (var src in Directory.GetFiles(filesDir, "*", SearchOption.AllDirectories))
                {
                    string rel = src.Substring(filesDir.Length + 1);
                    wanted.Add(rel);
                    string dst = Path.Combine(target, rel);
                    var si = new FileInfo(src); var di = new FileInfo(dst);
                    if (di.Exists && di.Length == si.Length && di.LastWriteTimeUtc == si.LastWriteTimeUtc) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(src, dst, true);
                    File.SetLastWriteTimeUtc(dst, si.LastWriteTimeUtc);
                }
                foreach (var old in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
                    if (!wanted.Contains(old.Substring(target.Length + 1))) File.Delete(old);
                string eq = Path.Combine(target, ProfileStore.SyncedName);
                var bytes = Utf8.GetBytes(body);
                if (!File.Exists(eq) || !File.ReadAllBytes(eq).SequenceEqual(bytes)) File.WriteAllBytes(eq, bytes);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = "Couldn't copy the EQ's files to Equalizer APO's config folder: " + ex.Message;
                return false;
            }
        }

        /// Removes a renamed or deleted EQ's copy from the config folder.
        public void RemoveSynced(string name)
        {
            try
            {
                string target = Path.Combine(ConfigDir, ProfilesFolder, name);
                if (Directory.Exists(target)) Directory.Delete(target, true);
            }
            catch { }
        }

        /// Comments out other active lines in config.txt (backup kept). Returns the backup name.
        public string DisableOtherLines()
        {
            var lines = File.ReadAllLines(ConfigTxt);
            string backup = "config.txt.backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(ConfigTxt, Path.Combine(ConfigDir, backup));
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i].Trim();
                if (s.Length == 0 || s.StartsWith("#") || s.Equals(IncludeLine, StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("Device:", StringComparison.OrdinalIgnoreCase)) continue;
                lines[i] = DisabledPrefix + lines[i];
            }
            File.WriteAllLines(ConfigTxt, lines, Utf8);
            return backup;
        }

        // ------------------------------------------------------------ setup --
        /// One-time setup: lets this user write the config folder and adds the include line.
        /// Uses one UAC prompt only if the folder isn't writable yet. Returns null on success.
        public string Setup()
        {
            if (!Directory.Exists(ConfigDir)) return "Equalizer APO isn't installed.";
            if (CanWrite(ConfigDir) && (File.Exists(ActivePath) || CanCreateIn(ConfigDir)))
            {
                try { AddInclude(ConfigDir); return null; }
                catch (Exception ex) { return ex.Message; }
            }
            try
            {
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                    "--eapo-setup \"" + ConfigDir + "\" " + WindowsIdentity.GetCurrent().User.Value)
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode != 0) return "Setup didn't complete (code " + p.ExitCode + ").";
                }
                return null;
            }
            catch (System.ComponentModel.Win32Exception) { return "Setup needs administrator permission once. It was cancelled."; }
        }

        /// Runs in the elevated helper process.
        public static int RunElevatedSetup(string configDir, string sid)
        {
            try
            {
                var di = new DirectoryInfo(configDir);
                var sec = di.GetAccessControl();
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                di.SetAccessControl(sec);
                AddInclude(configDir);
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "daceqswitcher-setup.log"), ex.ToString()); } catch { }
                return 1;
            }
        }

        static void AddInclude(string configDir)
        {
            string active = Path.Combine(configDir, ActiveName);
            if (!File.Exists(active)) File.WriteAllText(active, "# DAC EQ Switcher: no EQ selected yet\r\n", Utf8);
            string cfg = Path.Combine(configDir, "config.txt");
            var lines = File.Exists(cfg) ? File.ReadAllLines(cfg).ToList() : new List<string>();
            if (lines.Any(l => l.Trim().Equals(IncludeLine, StringComparison.OrdinalIgnoreCase))) return;
            bool hasDevice = lines.Any(l => l.TrimStart().StartsWith("Device:", StringComparison.OrdinalIgnoreCase));
            if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
            lines.Add(BlockComment);
            if (hasDevice) lines.Add("Device: all");   // don't inherit a device filter from above
            lines.Add(IncludeLine);
            File.WriteAllLines(cfg, lines, Utf8);
        }

        public void OpenConfigurator()
        {
            string exe = Path.Combine(InstallDir, "Configurator.exe");
            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
    }
}

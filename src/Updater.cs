using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading;

namespace DacEqSwitcher
{
    /// Checks GitHub once a day for a newer build and installs it in place.
    /// One small HTTPS request per day; nothing else runs in between.
    sealed class Updater : IDisposable
    {
        public const string Repo = "ZN98/dac-eq-switcher";
        const string AssetName = "DacEqSwitcher.exe";
        static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(20), Every = TimeSpan.FromHours(24);

        public static Version Current
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        public sealed class Release
        {
            public Version Version;
            public string Tag, Page, Url, Sha256, Notes;
            public long Size;
        }

        [DataContract] sealed class GhAsset
        {
            [DataMember(Name = "name")] public string Name;
            [DataMember(Name = "browser_download_url")] public string Url;
            [DataMember(Name = "size")] public long Size;
            [DataMember(Name = "digest")] public string Digest;
        }

        [DataContract] sealed class GhRelease
        {
            [DataMember(Name = "tag_name")] public string Tag;
            [DataMember(Name = "html_url")] public string Page;
            [DataMember(Name = "body")] public string Body;
            [DataMember(Name = "draft")] public bool Draft;
            [DataMember(Name = "prerelease")] public bool Prerelease;
            [DataMember(Name = "assets")] public List<GhAsset> Assets;
        }

        [DataContract] sealed class SfFile { [DataMember(Name = "filename")] public string Filename; }
        [DataContract] sealed class SfPlatforms { [DataMember(Name = "windows")] public SfFile Windows; }
        [DataContract] sealed class SfBest
        {
            [DataMember(Name = "release")] public SfFile Release;
            [DataMember(Name = "platform_releases")] public SfPlatforms Platforms;
        }

        public const string EapoPage = "https://sourceforge.net/projects/equalizerapo/files/";
        readonly Func<string> eapoInstallDir;

        public Updater(Func<string> eapoInstallDir) { this.eapoInstallDir = eapoInstallDir; }

        Timer timer;
        public Release Available { get; private set; }
        /// Set when a newer Equalizer APO than the installed one has been released.
        public Version EapoLatest { get; private set; }
        public Version EapoInstalled { get; private set; }
        public event Action Changed;

        static Updater()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        /// Removes the previous exe left behind by an update.
        public static void CleanUp()
        {
            try
            {
                string old = Assembly.GetExecutingAssembly().Location + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch { }
        }

        public void SetEnabled(bool on)
        {
            if (timer != null) { timer.Dispose(); timer = null; }
            if (on) timer = new Timer(_ => Check(), null, FirstCheck, Every);
        }

        public void CheckNow() { ThreadPool.QueueUserWorkItem(_ => Check()); }

        void Check()
        {
            CheckEapo();
            try
            {
                var r = Latest();
                if (r != null && r.Version > Current && (Available == null || Available.Version != r.Version))
                {
                    Available = r;
                    Log.Write("update available: " + r.Tag);
                    var h = Changed;
                    if (h != null) h();
                }
            }
            catch (Exception ex) { Log.Write("update check: " + ex.Message); }
        }

        /// Equalizer APO isn't bundled (it needs admin rights and a restart to install), so the
        /// app only tells you when a newer version is out.
        void CheckEapo()
        {
            try
            {
                Version installed = null;
                string dir = eapoInstallDir();
                foreach (var f in new[] { "EqualizerAPO.dll", "Editor.exe", "Configurator.exe" })
                {
                    string p = Path.Combine(dir ?? "", f);
                    if (!File.Exists(p)) continue;
                    var fv = FileVersionInfo.GetVersionInfo(p);
                    Version v;
                    if (Version.TryParse((fv.ProductVersion ?? fv.FileVersion ?? "").Split(' ')[0].Replace(',', '.'), out v)) { installed = Norm(v); break; }
                }
                if (installed == null) return;
                string url = Environment.GetEnvironmentVariable("DACEQ_EAPO_URL") ?? "https://sourceforge.net/projects/equalizerapo/best_release.json";
                SfBest best;
                using (var resp = Request(url).GetResponse())
                using (var s = resp.GetResponseStream())
                    best = (SfBest)new DataContractJsonSerializer(typeof(SfBest)).ReadObject(s);
                string name = best == null ? null : best.Platforms != null && best.Platforms.Windows != null ? best.Platforms.Windows.Filename
                            : best.Release != null ? best.Release.Filename : null;
                var m = name == null ? null : System.Text.RegularExpressions.Regex.Match(name, @"EqualizerAPO[^/]*?-(\d+(?:\.\d+)+)\.exe$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                Version latest;
                if (m == null || !m.Success || !Version.TryParse(m.Groups[1].Value, out latest)) return;
                latest = Norm(latest);
                EapoInstalled = installed;
                bool newer = latest > installed;
                if (newer != (EapoLatest != null))
                {
                    EapoLatest = newer ? latest : null;
                    if (newer) Log.Write("Equalizer APO " + latest + " available (installed " + installed + ")");
                    var h = Changed;
                    if (h != null) h();
                }
            }
            catch (Exception ex) { Log.Write("Equalizer APO check: " + ex.Message); }
        }

        static Version Norm(Version v) { return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }

        static HttpWebRequest Request(string url)
        {
            var q = (HttpWebRequest)WebRequest.Create(url);
            q.UserAgent = "DacEqSwitcher/" + Current;
            if (url.Contains("github.com")) q.Accept = "application/vnd.github+json";
            q.Timeout = 20000;
            q.ReadWriteTimeout = 60000;
            return q;
        }

        static Release Latest()
        {
            string url = Environment.GetEnvironmentVariable("DACEQ_UPDATE_URL") ?? "https://api.github.com/repos/" + Repo + "/releases/latest";
            GhRelease gh;
            using (var resp = Request(url).GetResponse())
            using (var s = resp.GetResponseStream())
                gh = (GhRelease)new DataContractJsonSerializer(typeof(GhRelease)).ReadObject(s);
            if (gh == null || gh.Draft || gh.Prerelease || gh.Tag == null) return null;
            Version v;
            if (!Version.TryParse(gh.Tag.TrimStart('v', 'V'), out v)) return null;
            var asset = (gh.Assets ?? new List<GhAsset>()).FirstOrDefault(a => a.Name == AssetName);
            if (asset == null) return null;
            string sha = asset.Digest != null && asset.Digest.StartsWith("sha256:") ? asset.Digest.Substring(7).ToLowerInvariant() : null;
            return new Release
            {
                Version = new Version(v.Major, v.Minor, Math.Max(0, v.Build)), Tag = gh.Tag, Page = gh.Page,
                Url = asset.Url, Size = asset.Size, Sha256 = sha, Notes = gh.Body,
            };
        }

        /// Downloads, verifies and swaps in the new exe, then starts it. Returns an error, or null
        /// when the new version has been started and this one should exit.
        public string Install(bool showWindow)
        {
            var r = Available;
            if (r == null) return "No update available.";
            string exe = Assembly.GetExecutingAssembly().Location;
            string fresh = exe + ".new", old = exe + ".old";
            try
            {
                using (var resp = Request(r.Url).GetResponse())
                using (var s = resp.GetResponseStream())
                using (var f = File.Create(fresh)) s.CopyTo(f);
                var info = new FileInfo(fresh);
                if (r.Size > 0 && info.Length != r.Size) throw new IOException("the download was incomplete");
                if (r.Sha256 != null)
                {
                    string got;
                    using (var sha = SHA256.Create())
                    using (var f = File.OpenRead(fresh))
                        got = string.Concat(sha.ComputeHash(f).Select(b => b.ToString("x2")));
                    if (got != r.Sha256) throw new IOException("the download didn't match its checksum");
                }
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);           // Windows lets a running exe be renamed
                File.Move(fresh, exe);
                Process.Start(new ProcessStartInfo(exe, showWindow ? "--updated" : "--updated --tray") { UseShellExecute = false });
                Log.Write("updated to " + r.Tag);
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                TryDelete(fresh);
                return "This folder can't be written to, so the update can't be installed here. " +
                       "Download it from the release page instead, or move the app to a folder you own (for example in Documents).";
            }
            catch (Exception ex)
            {
                TryDelete(fresh);
                if (!File.Exists(exe) && File.Exists(old)) try { File.Move(old, exe); } catch { }
                return "The update couldn't be installed: " + ex.Message;
            }
        }

        static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        public void Dispose() { SetEnabled(false); }
    }
}

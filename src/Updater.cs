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

        Timer timer;
        public Release Available { get; private set; }
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

        static HttpWebRequest Request(string url)
        {
            var q = (HttpWebRequest)WebRequest.Create(url);
            q.UserAgent = "DacEqSwitcher/" + Current;
            q.Accept = "application/vnd.github+json";
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

// DAC EQ Switcher - Copyright 2026 ZN98 - GPL-3.0-or-later (see LICENSE and NOTICE).
// The install step is ported from Equalizer APO's DeviceAPOInfo (load/install/getOriginalAPO*),
// Copyright (C) 2013 Jonas Thedering, GPL-2.0-or-later, used under GPL-3.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace DacEqSwitcher
{
    /// Whether Equalizer APO is installed on the DAC's Windows playback device, and putting it back.
    ///
    /// Windows makes a new playback device (a new endpoint GUID) when a USB DAC re-enumerates,
    /// e.g. on another USB port or after a driver update. Equalizer APO stays on the old one, so the
    /// EQ stops. The old endpoint keeps Equalizer APO's settings in the registry; Repair copies them
    /// to the new one exactly as Equalizer APO's Device Selector would install it.
    static class ApoDevices
    {
        const string RenderPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
        const string ChildPath = @"SOFTWARE\EqualizerAPO\Child APOs";
        public const string PreMixGuid = "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}";
        public const string PostMixGuid = "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}";
        const string NoKey = "!KEY", NoValue = "!VALUE";

        const string Lfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1", Gfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2",
                     Sfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5", Mfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6",
                     Efx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7";
        static readonly string[] AllFx = { Lfx, Gfx, Sfx, Mfx, Efx };
        const int L = 0, G = 1, S = 2, M = 3, E = 4;
        const string SfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},5", MfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},6",
                     EfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},7", DefaultMode = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
        const string DisableEnhancements = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
        const string ConnectionName = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
        const string DeviceName = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";

        public sealed class Endpoint
        {
            public string Guid, Device, Connection;
            public bool Active, Installed;
            public string Name { get { return Connection + " (" + Device + ")"; } }
        }

        public sealed class Status
        {
            public List<Endpoint> Dac = new List<Endpoint>();   // the DAC's playback devices, past and present
            public Endpoint Missing;     // active device without Equalizer APO
            public Endpoint Template;    // an earlier device of the DAC that had it: copy from this
            public bool NeedsFix { get { return Missing != null; } }
        }

        /// HKLM, or (for tests) a stand-in under HKCU.
        static RegistryKey Root()
        {
            string test = Environment.GetEnvironmentVariable("DACEQ_TEST_HKLM");
            if (!string.IsNullOrEmpty(test)) return Registry.CurrentUser.CreateSubKey(test);
            if (Native.OnWindows && Environment.Is64BitOperatingSystem) return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return Registry.LocalMachine;
        }

        static bool IsApo(object v)
        {
            var s = v as string;
            return s != null && (s.Equals(PreMixGuid, StringComparison.OrdinalIgnoreCase) || s.Equals(PostMixGuid, StringComparison.OrdinalIgnoreCase));
        }

        public static List<Endpoint> List()
        {
            var list = new List<Endpoint>();
            try
            {
                using (var root = Root())
                using (var render = root.OpenSubKey(RenderPath))
                {
                    if (render == null) return list;
                    foreach (var guid in render.GetSubKeyNames())
                        using (var k = render.OpenSubKey(guid))
                        {
                            if (k == null) continue;
                            var e = new Endpoint { Guid = guid, Active = Convert.ToInt32(k.GetValue("DeviceState") ?? 0) == 1 };
                            using (var p = k.OpenSubKey("Properties"))
                            {
                                if (p != null) { e.Device = p.GetValue(DeviceName) as string ?? ""; e.Connection = p.GetValue(ConnectionName) as string ?? ""; }
                            }
                            using (var fx = k.OpenSubKey("FxProperties"))
                                e.Installed = fx != null && AllFx.Any(n => IsApo(fx.GetValue(n)));
                            list.Add(e);
                        }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException) { }
            return list;
        }

        static bool IsDac(Endpoint e, string modelHint)
        {
            if (string.IsNullOrEmpty(e.Device)) return false;
            if (!string.IsNullOrEmpty(modelHint))
            {
                string model = modelHint.StartsWith("Topping ", StringComparison.OrdinalIgnoreCase) ? modelHint.Substring(8) : modelHint;
                if (e.Device.IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return e.Device.IndexOf("TOPPING", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static Status Check(string modelHint)
        {
            var st = new Status();
            st.Dac = List().Where(e => IsDac(e, modelHint)).ToList();
            var active = st.Dac.Where(e => e.Active).ToList();
            if (active.Count == 0 || active.Any(e => e.Installed)) return st;   // not plugged in, or fine
            // prefer an output of the same kind (e.g. "Speakers") as the one that had Equalizer APO
            foreach (var a in active)
            {
                var t = st.Dac.FirstOrDefault(e => !e.Active && e.Installed && e.Connection == a.Connection)
                     ?? st.Dac.FirstOrDefault(e => !e.Active && e.Installed);
                if (t != null) { st.Missing = a; st.Template = t; return st; }
            }
            st.Missing = active[0];   // never set up on this DAC: Equalizer APO's Device Selector is the way
            return st;
        }

        // --------------------------------------------------------------- repair --
        public const int Ok = 0, NoTemplate = 2, AccessDenied = 3, NotFound = 4, Failed = 5, RestartFailed = 6;

        sealed class InstallState
        {
            public int Mode;   // 0 LFX/GFX, 1 SFX/MFX, 2 SFX/EFX
            public bool PreMix, PostMix, UseOriginalPre, UseOriginalPost, AllowSilent, AutoAdjust = true;
        }

        /// Runs elevated. Installs Equalizer APO on endpoint newGuid with the settings it had on templateGuid.
        public static int Repair(string newGuid, string templateGuid, string backupDir, out string message)
        {
            message = null;
            try
            {
                using (var root = Root())
                {
                    // the template's installation (DeviceAPOInfo::load, installed branch, version 2)
                    var st = new InstallState();
                    var found = new bool[5];
                    using (var fx = root.OpenSubKey(RenderPath + "\\" + templateGuid + "\\FxProperties"))
                    using (var child = root.OpenSubKey(ChildPath + "\\" + templateGuid))
                    {
                        if (fx == null || child == null) { message = "the earlier device has no Equalizer APO settings to copy"; return NoTemplate; }
                        for (int i = 0; i < 5; i++) found[i] = IsApo(fx.GetValue(AllFx[i]));
                        if (!found.Any(f => f)) { message = "Equalizer APO isn't on the earlier device either"; return NoTemplate; }
                        if ((child.GetValue("Version") as string) != "2") { message = "the earlier device uses an Equalizer APO installation format this app doesn't know"; return NoTemplate; }
                        string pre = child.GetValue("PreMixChild") as string ?? "", post = child.GetValue("PostMixChild") as string ?? "";
                        st.PreMix = found[L] || found[S];
                        st.PostMix = found[G] || found[M] || found[E];
                        st.UseOriginalPre = pre != "";
                        st.UseOriginalPost = post != "";
                        st.Mode = found[L] || found[G] ? 0 : found[E] ? 2 : 1;
                        var silent = child.GetValue("AllowSilentBufferModification") as string;
                        if (silent != null) st.AllowSilent = silent != "false";
                        var noAdjust = child.GetValue("DisableAutomaticAdjustment") as string;
                        if (noAdjust != null) st.AutoAdjust = noAdjust == "false";
                    }

                    string keyPath = RenderPath + "\\" + newGuid;
                    using (var ep = root.OpenSubKey(keyPath))
                        if (ep == null) { message = "the new playback device wasn't found"; return NotFound; }
                    var fxNew = root.OpenSubKey(keyPath + "\\FxProperties", true);
                    if (fxNew == null)
                    {
                        // creating FxProperties needs the key's ownership changed; leave that to Device Selector
                        message = "the new playback device has no effects settings yet";
                        return NoTemplate;
                    }
                    using (fxNew)
                    {
                        // the new device's own (driver) effects, as DeviceAPOInfo::load reads them
                        var original = new string[5];
                        for (int i = 0; i < 5; i++)
                        {
                            var v = fxNew.GetValue(AllFx[i]) as string;
                            original[i] = v == null || IsApo(v) ? NoValue : v;
                        }

                        // DeviceAPOInfo::install
                        using (var child = root.CreateSubKey(ChildPath + "\\" + newGuid))
                        {
                            var backup = new List<string>();
                            for (int i = 0; i < 5; i++)
                            {
                                child.SetValue(AllFx[i], original[i], RegistryValueKind.String);
                                if (original[i] != NoValue) backup.Add("\"" + AllFx[i] + "\"=\"" + original[i] + "\"");
                            }
                            if (backup.Count > 0 && backupDir != null)
                                try
                                {
                                    Directory.CreateDirectory(backupDir);
                                    File.WriteAllText(Path.Combine(backupDir, "fx-backup-" + newGuid.Trim('{', '}') + ".reg"),
                                        "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\" + keyPath + "\\FxProperties]\r\n" + string.Join("\r\n", backup) + "\r\n", Encoding.Unicode);
                                }
                                catch { }

                            child.SetValue("PreMixChild", st.UseOriginalPre ? OriginalPre(st.Mode, original) : "", RegistryValueKind.String);
                            child.SetValue("PostMixChild", st.UseOriginalPost ? OriginalPost(st.Mode, original) : "", RegistryValueKind.String);
                            child.SetValue("AllowSilentBufferModification", st.AllowSilent ? "true" : "false", RegistryValueKind.String);
                            if (st.AutoAdjust) { if (child.GetValue("DisableAutomaticAdjustment") != null) child.DeleteValue("DisableAutomaticAdjustment"); }
                            else child.SetValue("DisableAutomaticAdjustment", "true", RegistryValueKind.String);
                            child.SetValue("Version", "2", RegistryValueKind.String);
                        }

                        Action<string> del = n => { if (fxNew.GetValue(n) != null) fxNew.DeleteValue(n); };
                        Action<string, string> modes = (n, v) => { if (fxNew.GetValue(n) == null) fxNew.SetValue(n, new[] { v }, RegistryValueKind.MultiString); };
                        if (st.Mode == 0)
                        {
                            if (st.PreMix) fxNew.SetValue(Lfx, PreMixGuid, RegistryValueKind.String);
                            if (st.PostMix) fxNew.SetValue(Gfx, PostMixGuid, RegistryValueKind.String);
                            del(Sfx); del(Mfx); del(Efx);
                        }
                        else if (st.Mode == 1)
                        {
                            del(Lfx); del(Gfx);
                            if (st.PreMix) { fxNew.SetValue(Sfx, PreMixGuid, RegistryValueKind.String); modes(SfxModes, DefaultMode); }
                            if (st.PostMix) { fxNew.SetValue(Mfx, PostMixGuid, RegistryValueKind.String); modes(MfxModes, DefaultMode); }
                        }
                        else
                        {
                            del(Lfx); del(Gfx);
                            if (st.PreMix) { fxNew.SetValue(Sfx, PreMixGuid, RegistryValueKind.String); modes(SfxModes, DefaultMode); }
                            if (st.PostMix) { fxNew.SetValue(Efx, PostMixGuid, RegistryValueKind.String); modes(EfxModes, DefaultMode); }
                        }
                        del(DisableEnhancements);   // force-enable enhancements
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                message = "Windows didn't allow the change (" + ex.Message + ")";
                return AccessDenied;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Failed;
            }
            return RestartAudio(out message) ? Ok : RestartFailed;
        }

        // DeviceAPOInfo::getOriginalAPOPreMix / PostMix (Windows 8.1 and later)
        static string OriginalPre(int mode, string[] o)
        {
            string g = mode == 0 ? (o[L] == NoValue && o[G] == NoValue ? o[S] : o[L])
                     : (o[S] == NoValue && o[mode == 1 ? M : E] == NoValue ? o[L] : o[S]);
            return g == NoKey || g == NoValue ? "" : g;
        }

        static string OriginalPost(int mode, string[] o)
        {
            string g = mode == 0 ? (o[L] == NoValue && o[G] == NoValue ? o[M] : o[G])
                     : mode == 1 ? (o[S] == NoValue && o[M] == NoValue ? o[G] : o[M])
                     : (o[S] == NoValue && o[E] == NoValue ? o[G] : o[E]);
            return g == NoKey || g == NoValue ? "" : g;
        }

        /// Equalizer APO is loaded when Windows Audio starts the device; restarting it avoids a reboot.
        static bool RestartAudio(out string message)
        {
            message = null;
            if (Environment.GetEnvironmentVariable("DACEQ_TEST_HKLM") != null) return true;
            try
            {
                using (var sc = new System.ServiceProcess.ServiceController("Audiosrv"))
                {
                    if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    }
                    sc.Start();
                    sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                }
                return true;
            }
            catch (Exception ex)
            {
                message = "Equalizer APO is installed, but Windows Audio couldn't be restarted (" + ex.Message + "). Restart Windows to finish.";
                return false;
            }
        }
    }
}

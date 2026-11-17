using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using DacEqSwitcher;

// Equalizer APO on a reconnected DAC: detection, and the repair (a port of Equalizer APO's
// DeviceAPOInfo install) against a stand-in registry, for each install mode Equalizer APO uses.
static class AR
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) fails++; Console.WriteLine((ok ? "ok    " : "FAIL  ") + what); }

    const string Root = @"Software\DacEqTest\HKLM";
    const string Render = Root + @"\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    const string Child = Root + @"\SOFTWARE\EqualizerAPO\Child APOs";
    const string LFX = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1", GFX = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2",
                 SFX = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5", MFX = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6", EFX = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7";
    const string SMODES = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},5", MMODES = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},6", EMODES = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},7";
    const string DISABLE = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
    const string OLD = "{11111111-1111-1111-1111-111111111111}", NEW = "{22222222-2222-2222-2222-222222222222}", OTHER = "{33333333-3333-3333-3333-333333333333}";
    const string DRIVER_SFX = "{AAAAAAAA-0000-0000-0000-000000000001}", DRIVER_MFX = "{AAAAAAAA-0000-0000-0000-000000000002}";

    static void Endpoint(string guid, int state, string device, string connection, Action<RegistryKey> fx)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Render + "\\" + guid))
        {
            k.SetValue("DeviceState", state, RegistryValueKind.DWord);
            using (var p = k.CreateSubKey("Properties"))
            {
                p.SetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6", device);
                p.SetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2", connection);
            }
            if (fx != null) using (var f = k.CreateSubKey("FxProperties")) fx(f);
        }
    }

    static void ChildApo(string guid, string pre, string post, string version = "2", bool silent = false, bool noAdjust = false)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Child + "\\" + guid))
        {
            if (version != null) k.SetValue("Version", version);
            k.SetValue("PreMixChild", pre);
            k.SetValue("PostMixChild", post);
            k.SetValue("AllowSilentBufferModification", silent ? "true" : "false");
            if (noAdjust) k.SetValue("DisableAutomaticAdjustment", "true");
        }
    }

    static object Fx(string guid, string name) { using (var k = Registry.CurrentUser.OpenSubKey(Render + "\\" + guid + "\\FxProperties")) return k == null ? null : k.GetValue(name); }
    static object Ch(string guid, string name) { using (var k = Registry.CurrentUser.OpenSubKey(Child + "\\" + guid)) return k == null ? null : k.GetValue(name); }
    static void Reset() { try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\DacEqTest"); } catch { } }

    // the DAC was set up with Equalizer APO in one mode; it comes back as a new device carrying the driver's own effects
    static void Scenario(string mode)
    {
        Reset();
        Endpoint(OLD, 4, "DX1 II", "Speakers", f =>
        {
            if (mode == "lfx") { f.SetValue(LFX, ApoDevices.PreMixGuid); f.SetValue(GFX, ApoDevices.PostMixGuid); }
            else if (mode == "sfx-mfx") { f.SetValue(SFX, ApoDevices.PreMixGuid); f.SetValue(MFX, ApoDevices.PostMixGuid); }
            else { f.SetValue(SFX, ApoDevices.PreMixGuid); f.SetValue(EFX, ApoDevices.PostMixGuid); }
        });
        ChildApo(OLD, DRIVER_SFX, mode == "lfx" ? "" : DRIVER_MFX, silent: mode == "sfx-efx", noAdjust: mode == "sfx-efx");
        Endpoint(NEW, 1, "DX1 II", "Speakers", f =>
        {
            f.SetValue(SFX, DRIVER_SFX); f.SetValue(MFX, DRIVER_MFX); f.SetValue(DISABLE, 1, RegistryValueKind.DWord);
            f.SetValue(SMODES, new[] { "{AAAA}" }, RegistryValueKind.MultiString);
        });
        Endpoint(OTHER, 1, "Realtek(R) Audio", "Speakers", f => f.SetValue(SFX, ApoDevices.PreMixGuid));
    }

    static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("DACEQ_TEST_HKLM", Root);
        string exe = args[0];

        // ---- detection
        Scenario("sfx-mfx");
        var st = ApoDevices.Check("Topping DX1 II");
        Check(st.NeedsFix && st.Missing.Guid == NEW && st.Template != null && st.Template.Guid == OLD,
              "a reconnected DAC on a new Windows device without Equalizer APO is found, with its earlier device to copy from");
        Check(st.Dac.Count == 2, "only the DAC's devices are considered (not the PC's other speakers)");

        // ---- repair, Equalizer APO's default mode on Windows 10/11 (SFX/MFX)
        string msg;
        int code = ApoDevices.Repair(NEW, OLD, null, out msg);
        Check(code == ApoDevices.Ok, "repair (SFX/MFX) returns ok" + (msg != null ? ": " + msg : ""));
        Check((string)Fx(NEW, SFX) == ApoDevices.PreMixGuid && (string)Fx(NEW, MFX) == ApoDevices.PostMixGuid && Fx(NEW, EFX) == null,
              "Equalizer APO is in the new device's SFX and MFX slots");
        Check(((string[])Fx(NEW, SMODES))[0] == "{AAAA}" && ((string[])Fx(NEW, MMODES))[0] == "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}",
              "processing modes: kept where the driver had them, default where it didn't");
        Check(Fx(NEW, DISABLE) == null, "audio enhancements are switched back on, as Device Selector does");
        Check((string)Ch(NEW, SFX) == DRIVER_SFX && (string)Ch(NEW, MFX) == DRIVER_MFX && (string)Ch(NEW, LFX) == "!VALUE" && (string)Ch(NEW, GFX) == "!VALUE" && (string)Ch(NEW, EFX) == "!VALUE",
              "the driver's own effects are recorded so Equalizer APO can chain and later restore them");
        Check((string)Ch(NEW, "PreMixChild") == DRIVER_SFX && (string)Ch(NEW, "PostMixChild") == DRIVER_MFX,
              "the driver's effects still run inside Equalizer APO, as they did on the earlier device");
        Check((string)Ch(NEW, "Version") == "2" && (string)Ch(NEW, "AllowSilentBufferModification") == "false" && Ch(NEW, "DisableAutomaticAdjustment") == null,
              "the earlier device's options are copied");
        Check(!ApoDevices.Check("Topping DX1 II").NeedsFix, "afterwards the DAC is reported as fine");
        Check((string)Fx(OLD, SFX) == ApoDevices.PreMixGuid && (string)Fx(OTHER, SFX) == ApoDevices.PreMixGuid, "the earlier device and other devices are untouched");

        // ---- LFX/GFX mode
        Scenario("lfx");
        code = ApoDevices.Repair(NEW, OLD, null, out msg);
        Check(code == ApoDevices.Ok && (string)Fx(NEW, LFX) == ApoDevices.PreMixGuid && (string)Fx(NEW, GFX) == ApoDevices.PostMixGuid
              && Fx(NEW, SFX) == null && Fx(NEW, MFX) == null, "LFX/GFX mode: Equalizer APO in LFX/GFX, the driver's SFX/MFX moved inside it");
        Check((string)Ch(NEW, "PreMixChild") == DRIVER_SFX && (string)Ch(NEW, "PostMixChild") == "", "LFX/GFX mode: pre-mix chains the driver's effect, post-mix as before (none)");

        // ---- SFX/EFX mode with non-default options
        Scenario("sfx-efx");
        code = ApoDevices.Repair(NEW, OLD, null, out msg);
        Check(code == ApoDevices.Ok && (string)Fx(NEW, SFX) == ApoDevices.PreMixGuid && (string)Fx(NEW, EFX) == ApoDevices.PostMixGuid
              && (string)Fx(NEW, MFX) == DRIVER_MFX, "SFX/EFX mode: Equalizer APO in SFX/EFX, the driver's MFX left alone");
        Check((string)Ch(NEW, "AllowSilentBufferModification") == "true" && (string)Ch(NEW, "DisableAutomaticAdjustment") == "true",
              "SFX/EFX mode: troubleshooting options copied");

        // ---- cases left to Equalizer APO's Device Selector
        Scenario("sfx-mfx");
        using (var k = Registry.CurrentUser.OpenSubKey(Render + "\\" + NEW, true)) k.DeleteSubKeyTree("FxProperties");
        Check(ApoDevices.Repair(NEW, OLD, null, out msg) == ApoDevices.NoTemplate && Fx(NEW, SFX) == null,
              "a new device with no effects key isn't touched (Device Selector must change its permissions): " + msg);
        Scenario("sfx-mfx");
        ChildApo(OLD, DRIVER_SFX, DRIVER_MFX, version: "3");
        Check(ApoDevices.Repair(NEW, OLD, null, out msg) == ApoDevices.NoTemplate && Ch(NEW, "Version") == null,
              "an installation format this app doesn't know isn't copied: " + msg);
        Reset();
        Endpoint(NEW, 1, "TOPPING DX1 II", "Speakers", f => f.SetValue(SFX, DRIVER_SFX));
        st = ApoDevices.Check("Topping DX1 II");
        Check(st.NeedsFix && st.Template == null, "a DAC that never had Equalizer APO is reported, with Device Selector as the fix");
        Reset();
        Endpoint(OLD, 4, "DX1 II", "Speakers", f => f.SetValue(SFX, ApoDevices.PreMixGuid));
        Check(!ApoDevices.Check("Topping DX1 II").NeedsFix, "nothing is reported while the DAC is unplugged");
        Reset();
        Endpoint(NEW, 1, "DX1 II", "Speakers", f => f.SetValue(MFX, ApoDevices.PostMixGuid));
        Check(!ApoDevices.Check("Topping DX1 II").NeedsFix, "nothing is reported when Equalizer APO is on the DAC");

        // ---- the elevated entry point only accepts device GUIDs
        Scenario("sfx-mfx");
        var p = Process.Start(new ProcessStartInfo("mono", "\"" + exe + "\" --eapo-repair \"" + NEW + "\\..\\x\" " + OLD + " /tmp") { UseShellExecute = false });
        p.WaitForExit();
        Check(p.ExitCode == ApoDevices.Failed && Fx(NEW, SFX) as string == DRIVER_SFX, "the elevated helper rejects anything but device GUIDs");
        p = Process.Start(new ProcessStartInfo("mono", "\"" + exe + "\" --eapo-repair " + NEW + " " + OLD + " /tmp") { UseShellExecute = false });
        p.WaitForExit();
        Check(p.ExitCode == ApoDevices.Ok && (string)Fx(NEW, SFX) == ApoDevices.PreMixGuid, "the elevated helper repairs the device");

        Reset();
        Console.WriteLine(fails == 0 ? "Equalizer APO device repair: all checks passed" : "Equalizer APO device repair: " + fails + " FAILED");
        return fails;
    }
}

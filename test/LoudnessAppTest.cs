using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using DacEqSwitcher;

// The app, driven by a simulated DX1 II: as the volume moves across the loudness reference the
// written file keeps one preamp (so the level follows the DAC smoothly) and carries the right
// correction for each volume; the correction settles once the knob stops; turning Loudness off
// removes it all; 2.2.13 settings move to the new defaults.
static class LA
{
    static int fails;
    static string Dir { get { return Environment.GetEnvironmentVariable("DACEQ_EAPO_CONFIG"); } }
    static string Active() { return File.ReadAllText(Path.Combine(Dir, Eapo.ActiveName)); }
    static void Volume(double db) { File.WriteAllText(Environment.GetEnvironmentVariable("DACEQ_SIM_STATE"), "output=2\nvolHp=" + (int)Math.Round((db + 99) * 10)); }
    static void Check(bool ok, string what) { if (!ok) fails++; Console.WriteLine((ok ? "ok    " : "FAIL  ") + what); }
    static string Preamp(string loud) { var m = Regex.Match(loud, @"Preamp: (-?[\d.]+) dB"); return m.Success ? m.Groups[1].Value : "none"; }
    static string Loud(string text) { int i = text.IndexOf("# Loudness"); return i < 0 ? "" : text.Substring(i); }

    static int Main()
    {
        const string eq = "Preamp: -6 dB\r\nFilter: ON LSC Fc 105 Hz Gain 6 dB Q 0.7\r\n";
        // settings as 2.2.13 saved them: no boost limit
        Directory.CreateDirectory(Environment.GetEnvironmentVariable("DACEQ_DATA"));
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("DACEQ_DATA"), "settings.json"),
            "{\"LoudnessMaxBoost\":0,\"LoudnessStrength\":100,\"LoudnessRefPhon\":80,\"LoudnessVersion\":2}");
        Volume(-20);
        var core = new AppCore();
        Check(core.Settings.LoudnessMaxBoost == 12 && core.Settings.LoudnessVersion == 3, "2.2.13 settings move to a 12 dB largest boost");
        core.Profiles.Save("Phones", eq);
        core.Settings.Rules.Add(new Rule { Profile = "Phones", LoudnessRefDb = -20, Conditions = { new Condition { Field = Dx1Protocol.Output, Value = 2 } } });
        core.SaveSettings();
        core.Start();
        for (int i = 0; i < 50 && !core.Monitor.Connected; i++) Thread.Sleep(100);
        Thread.Sleep(800);

        double head = Loudness.Headroom(eq, 80, 1.0, 12);
        string pre = (-head).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var v in new[] { -20.0, -20.5, -21, -23, -30, -45, -12, -20 })
        {
            Volume(v);
            Thread.Sleep(1200);   // past the 300 ms settle
            string loud = Loud(Active());
            double below = Math.Max(0, -20 - v);
            string want = Loudness.ApoText(Loudness.Make(head, below, 80, 1.0, 12), "x");
            want = want.Substring(want.IndexOf("Channel"));
            Check(Active().Contains("\"Phones\"") && Preamp(loud) == pre && loud.Contains(want) && (below > 0) == loud.Contains("GraphicEQ"),
                  "DAC at " + v + " dB (reference -20): preamp " + Preamp(loud) + " dB" + (below > 0 ? ", correction for " + below + " dB below" : ", no correction"));
        }

        // a quick sweep: the file only follows once the knob settles
        foreach (var v in new[] { -21.0, -22, -23, -24, -25 }) { Volume(v); Thread.Sleep(120); }
        Thread.Sleep(1200);
        Check(Loud(Active()).Contains("5.0 dB below reference"), "after a quick sweep the correction settles on the final volume");

        core.SetLoudnessOn(false);
        Check(Loud(Active()) == "" && Active().Contains("\"Phones\""), "Loudness off: the EQ alone, no preamp or correction added");
        core.SetLoudnessOn(true);
        Check(Preamp(Loud(Active())) == pre, "Loudness on again: same headroom");
        core.Dispose();
        Console.WriteLine(fails == 0 ? "loudness in the app: all checks passed" : "loudness in the app: " + fails + " FAILED");
        return fails;
    }
}

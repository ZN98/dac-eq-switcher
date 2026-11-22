using System;
using System.IO;
using System.Linq;
using System.Threading;
using DacEqSwitcher;

// Both / Left / Right blocks and Swap L/R.
//  1. Splitting an EQ into blocks and putting it back never changes what Equalizer APO does
//     (same response at every frequency, both channels), and is stable when repeated.
//  2. EQs that branch or mix channels stay whole.
//  3. Swap L/R swaps the music before the EQ: the Left block stays on the left output.
//  4. The app writes the swap (also with EQ off), and nothing changes when it's off.
static class CT
{
    static void WriteRetry(string path, string text)
    {
        // the simulated DAC may be reading the file at that moment
        for (int i = 0; ; i++)
            try { System.IO.File.WriteAllText(path, text); return; }
            catch (System.IO.IOException) when (i < 50) { System.Threading.Thread.Sleep(20); }
    }

    static int fails;
    static void Check(bool ok, string what) { if (!ok) fails++; Console.WriteLine((ok ? "ok    " : "FAIL  ") + what); }

    static double Diff(EqText.Response a, EqText.Response b)
    {
        double d = 0;
        for (int i = 0; i < a.L.Length; i++) d = Math.Max(d, Math.Max(Math.Abs(a.L[i] - b.L[i]), Math.Abs(a.R[i] - b.R[i])));
        return d;
    }

    static EqText.Response A(string t) { return EqText.Analyse(t, 400, 20, 20000, null, 48000); }

    static void RoundTrip(string name, string text)
    {
        var s = ChannelBlocks.Separate(text);
        string back = ChannelBlocks.Compose(s.Blocks);
        var again = ChannelBlocks.Separate(back);
        double d = Diff(A(text), A(back));
        Check(s.Separated && d < 1e-9 && again.Blocks.SequenceEqual(s.Blocks),
              name + ": split into " + string.Join(" / ", s.Blocks.Select(b => EqText.SplitLines(b).Count(l => l.Trim().Length > 0) + " lines")) +
              ", same sound after rebuilding (max diff " + d.ToString("0.0e0") + " dB), stable");
    }

    static int Main(string[] a)
    {
        // 1. real presets and edge cases
        foreach (var f in new[] { "/mnt/user-data/uploads/EM6L.peace", "/mnt/user-data/uploads/Neumann_KH80.peace" })
            if (File.Exists(f)) RoundTrip(Path.GetFileName(f), EqText.Import(f, File.ReadAllBytes(f), null));
        RoundTrip("L/R-only preset", "Channel: L\r\nPreamp: -2 dB\r\nFilter: ON PK Fc 100 Hz Gain 3 dB Q 1\r\nChannel: R\r\nFilter: ON PK Fc 200 Hz Gain -3 dB Q 2");
        RoundTrip("channel numbers, 'L R', comments, Delay and GraphicEQ per side",
            "# my EQ\r\nPreamp: -6 dB\r\nChannel: 1\r\nDelay: 0.2 ms\r\nGraphicEQ: 20 3; 1000 0; 10000 -2\r\nChannel: L R\r\nFilter: ON HSC Fc 8000 Hz Gain 2 dB Q 0.7\r\n" +
            "Channel: 2\r\n# right side\r\nFilter: ON LSC Fc 105 Hz Gain 4 dB Q 0.7\r\nChannel: all\r\nFilter: ON PK Fc 3000 Hz Gain -2 dB Q 1");
        var s = ChannelBlocks.Separate("Preamp: -1 dB\r\nChannel: L\r\nFilter: ON PK Fc 100 Hz Gain 3 dB Q 1\r\nChannel: all\r\nFilter: ON PK Fc 2000 Hz Gain 1 dB Q 1");
        Check(s.Blocks[0].Contains("2000 Hz") && s.Blocks[0].Contains("Preamp") && s.Blocks[1].Contains("100 Hz") && s.Blocks[2] == "",
              "lines for both channels after a left section go back to Both channels");

        // 2. kept whole
        foreach (var t in new[] { "Preamp: -3 dB\r\nCopy: L=0.5*L+0.5*R R=L", "If: sampleRate == 44100\r\nPreamp: -1 dB\r\nEndIf:", "Channel: C\r\nPreamp: -3 dB", "Include: other.txt" })
        {
            var w = ChannelBlocks.Separate(t);
            Check(!w.Separated && w.Blocks[0] == t.Trim() && w.Blocks[1] == "" && w.Blocks[2] == "", "kept as one block (" + w.Reason + "): " + t.Split('\r')[0]);
        }

        // 3. swap: mute the right input, then see which output carries the music and which EQ it gets
        string eq = ChannelBlocks.Compose(new[] { "Preamp: -3 dB", "Filter: ON PK Fc 1000 Hz Gain 9 dB Q 1", "Filter: ON PK Fc 5000 Hz Gain -9 dB Q 1" });
        string muteRightInput = "Copy: R=0.0\r\n";
        var normal = A(muteRightInput + eq);
        var swapped = A(muteRightInput + AppCore.SwapText + eq);
        int k1 = Array.FindIndex(normal.Freqs, f => f >= 1000), k5 = Array.FindIndex(normal.Freqs, f => f >= 5000);
        Check(normal.L[k1] > 5 && normal.R[k1] < -100, "normal: the left music plays on the left with the Left EQ (+9 dB at 1 kHz)");
        Check(swapped.L[k1] < -100 && swapped.R[k5] < -10 && swapped.R[k1] > -4,
              "swapped: the left music plays on the right, with the Right EQ (-9 dB at 5 kHz), not the Left one");

        // 4. in the app
        WriteRetry(Environment.GetEnvironmentVariable("DACEQ_SIM_STATE"), "output=2");
        var core = new AppCore();
        core.Profiles.Save("Phones", eq);
        core.Settings.Rules.Add(new Rule { Profile = "Phones", Conditions = { new Condition { Field = Dx1Protocol.Output, Value = 2 } } });
        core.SaveSettings();
        core.Start();
        for (int i = 0; i < 50 && !core.Monitor.Connected; i++) Thread.Sleep(100);
        Thread.Sleep(800);
        string path = Path.Combine(Environment.GetEnvironmentVariable("DACEQ_EAPO_CONFIG"), Eapo.ActiveName);
        string before = File.ReadAllText(path);
        core.SetSwap(true);
        string on = File.ReadAllText(path);
        Check(on == AppCore.SwapText + before, "Swap L/R on: the swap is written before the EQ, the EQ itself unchanged");
        core.SetEqOn(false);
        Check(File.ReadAllText(path).StartsWith(AppCore.SwapText) && File.ReadAllText(path).Contains("EQ switched off"), "the swap stays when the EQ is switched off");
        core.SetEqOn(true);
        core.SetSwap(false);
        Check(File.ReadAllText(path) == before, "Swap L/R off: exactly the file as before");
        core.Dispose();
        var again2 = new AppCore();
        Check(!again2.SwapOn, "the setting is remembered");
        again2.Dispose();

        Console.WriteLine(fails == 0 ? "channels: all checks passed" : "channels: " + fails + " FAILED");
        return fails;
    }
}

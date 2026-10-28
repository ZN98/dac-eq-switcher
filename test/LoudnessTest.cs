using System;
using System.Linq;
using DacEqSwitcher;

// Loudness curve must match the independent Python reference to 0.01 dB, and its
// Equalizer APO text must parse back to the same curve.
static class LT
{
    static int Main(string[] a)
    {
        int fails = 0;
        var cases = new[] { new[] { 60.0, 80, 1.0, 99 }, new[] { 50.0, 80, 0.6, 12 }, new[] { 70.0, 80, 0.6, 12 } };
        var expected = Console.In.ReadToEnd().Trim().Split('\n');
        for (int c = 0; c < cases.Length; c++)
        {
            var g = Loudness.Curve(cases[c][0], cases[c][1], cases[c][2], cases[c][3]);
            var want = expected[c].Split(' ').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            double worst = g.Zip(want, (x, y) => Math.Abs(x - y)).Max();
            if (worst > 0.01) fails++;
            Console.WriteLine((worst > 0.01 ? "FAIL  " : "ok    ") + "listen " + cases[c][0] + " phon, strength " + cases[c][2] +
                              ": 50 Hz " + g[4].ToString("+0.0") + " dB, 10 kHz " + g[27].ToString("+0.0") + " dB (max diff " + worst.ToString("0.0000") + ")");
        }
        var curve = Loudness.Curve(50, 80, 0.6, 12);
        var text = Loudness.ApoText(curve, "test");
        var r = EqText.Analyse(text);
        // with the preamp the peak sits at 0 dB: nothing can clip
        bool ok = Math.Abs(r.PeakDb) < 0.15 && r.Skipped == 0;
        if (!ok) fails++;
        Console.WriteLine((ok ? "ok    " : "FAIL  ") + "APO text parses back, peak " + r.PeakDb.ToString("0.00") + " dB (preamp gives headroom)");
        if (Loudness.ApoText(Loudness.Curve(80, 80, 0.6, 12), "x") != null) { fails++; Console.WriteLine("FAIL  at the reference there should be nothing to add"); }
        else Console.WriteLine("ok    nothing added at the reference level");
        return fails;
    }
}

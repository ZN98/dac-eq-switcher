using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DacEqSwitcher;

// Loudness compensation against ISO 226:2003.
//  1. The contours match an independent implementation of the published tables and eq. (1).
//  2. Eq. (1) and the standard's separate inverse, eq. (2), agree; 1 kHz is the phon level.
//  3. End to end: a sound balanced at the reference (every frequency at 80 phon), turned down
//     on the DAC and passed through the Equalizer APO lines the app writes, is heard at one
//     loudness level at every standard frequency - the level the app says - per eq. (2).
//  4. The combined EQ + compensation never peaks above the EQ alone (no new clipping), and the
//     extra cut is no bigger than needed.
static class LT
{
    static int fails;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static void Check(bool ok, string what)
    {
        if (!ok) fails++;
        Console.WriteLine((ok ? "ok    " : "FAIL  ") + what);
    }

    static int Main()
    {
        // 1. contours vs the Python reference (stdin)
        var lines = Console.In.ReadToEnd().Trim().Split('\n');
        double worst = 0;
        for (int k = 0; k < lines.Length; k++)
        {
            var want = lines[k].Trim().Split(' ').Select(x => double.Parse(x, Inv)).ToArray();
            for (int i = 0; i < Loudness.Count; i++) worst = Math.Max(worst, Math.Abs(Loudness.Spl(i, 20 + 10 * k) - want[i]));
        }
        Check(lines.Length == 8 && worst < 1e-4, "contours, 29 frequencies x 20-90 phon, match the reference (max diff " + worst.ToString("0.000000", Inv) + " dB)");

        // 2. eq. (1) vs eq. (2), and the 1 kHz anchor
        double inv = 0, anchor = 0;
        for (int p = 20; p <= 90; p += 5)
        {
            anchor = Math.Max(anchor, Math.Abs(Loudness.Spl(17, p) - p));
            for (int i = 0; i < Loudness.Count; i++) inv = Math.Max(inv, Math.Abs(Loudness.Phon(i, Loudness.Spl(i, p)) - p));
        }
        // the two published equations are approximations of each other; they agree to ~0.06 phon
        Check(inv < 0.1, "eq. (2) inverts eq. (1) at every frequency (max diff " + inv.ToString("0.000", Inv) + " phon)");
        Check(anchor < 0.02, "1 kHz: dB SPL = phon (max diff " + anchor.ToString("0.000", Inv) + ")");

        // 3 + 4 with different EQs in front
        var profiles = new Dictionary<string, string>
        {
            { "no EQ", "" },
            { "bass-boost EQ with preamp", "Preamp: -6 dB\r\nFilter: ON LSC Fc 105 Hz Gain 6 dB Q 0.7\r\nFilter: ON PK Fc 3000 Hz Gain -3 dB Q 2" },
            { "EQ with spare headroom", "Preamp: -8 dB\r\nFilter: ON PK Fc 2000 Hz Gain 3 dB Q 1.4\r\nFilter: ON LSC Fc 80 Hz Gain -4 dB Q 0.7" },
            { "narrow treble peak, per channel", "Preamp: -2 dB\r\nChannel: L\r\nFilter: ON PK Fc 8000 Hz Gain 5 dB Q 6\r\nChannel: R\r\nFilter: ON PK Fc 60 Hz Gain 2 dB Q 1" },
            { "EQ that already clips", "Filter: ON PK Fc 40 Hz Gain 4 dB Q 1" },
        };
        foreach (var kv in profiles)
            foreach (var d in new[] { 0.5, 3, 5, 10, 15, 20, 30 })
            {
                var plan = Loudness.Make(kv.Value, d, 80, 1.0, 0);
                string tag = kv.Key + ", DAC " + d + " dB down";
                if (plan == null) { Check(false, tag + ": no plan"); continue; }
                string apo = Loudness.ApoText(plan, "test");

                // read back what Equalizer APO gets: preamp + GraphicEQ points (parsed here, not by the app)
                double pre = 0;
                var pts = new List<KeyValuePair<double, double>>();
                foreach (var raw in apo.Split('\n'))
                {
                    var l = raw.Trim();
                    if (l.StartsWith("Preamp:")) pre = double.Parse(l.Substring(7).Replace("dB", "").Trim(), Inv);
                    if (l.StartsWith("GraphicEQ:"))
                        foreach (var part in l.Substring(10).Split(';'))
                        {
                            var t = part.Trim().Split(' ');
                            pts.Add(new KeyValuePair<double, double>(double.Parse(t[0], Inv), double.Parse(t[1], Inv)));
                        }
                }
                // every standard frequency is a GraphicEQ point, so its gain is exact
                double lo = double.MaxValue, hi = double.MinValue;
                for (int i = 0; i < Loudness.Count; i++)
                {
                    double gain = pts.First(p => Math.Abs(p.Key - Loudness.Frequency(i)) < 1e-6).Value;
                    double spl = Loudness.Spl(i, 80) - d + pre + gain;      // the EQ itself was part of the reference sound
                    double phon = Loudness.Phon(i, spl);
                    lo = Math.Min(lo, phon); hi = Math.Max(hi, phon);
                }
                double heardPhon = 80 - plan.HeardBelow;
                // within the standard's range every frequency must land on the heard level; below
                // 20 phon (where it has no data) the 20-phon correction is held
                bool equal = heardPhon >= Loudness.MinPhon
                    ? hi - lo < 0.06 && Math.Abs((hi + lo) / 2 - heardPhon) < 0.06
                    : plan.Curve.Zip(Loudness.Curve(Loudness.MinPhon, 80, 1.0, 0), (a, b) => Math.Abs(a - b)).Max() < 1e-9;
                bool consistent = Math.Abs(plan.HeardBelow - (d - plan.PreampDb)) < 0.02;

                // 4. peaks: the EQ + compensation vs the EQ alone, finely sampled 5 Hz - 24 kHz
                var eq = EqText.Analyse(kv.Value, 8000, 5, 24000);
                var both = EqText.Analyse(kv.Value + "\r\nChannel: all\r\n" + apo, 8000, 5, 24000);
                double allowed = Math.Max(0, eq.PeakDb);
                bool noClip = both.PeakDb <= allowed + 0.01;
                bool notOvercut = plan.PreampDb == 0 || both.PeakDb >= allowed - 0.05;
                Check(equal && consistent && noClip && notOvercut,
                      tag + ": heard " + plan.HeardBelow.ToString("0.0", Inv) + " dB down" + (heardPhon < Loudness.MinPhon ? " (below 20 phon: 20-phon correction held)" : "") + ", every frequency at " + lo.ToString("0.00", Inv) + "-" + hi.ToString("0.00", Inv)
                      + " phon (want " + heardPhon.ToString("0.00", Inv) + "), +" + plan.BassDb.ToString("0.0", Inv) + " dB at 50 Hz, preamp " + plan.PreampDb.ToString("0.00", Inv)
                      + ", peak " + both.PeakDb.ToString("0.00", Inv) + " (EQ alone " + eq.PeakDb.ToString("0.00", Inv) + ")");
            }

        // edges, settings, and when there is nothing to do
        var g = Loudness.Make("", 15, 80, 1.0, 0);
        var nodes = Loudness.Nodes(g.Curve);
        Check(nodes.First().Key == Loudness.LowEdgeHz && nodes.First().Value == g.Curve[0] &&
              nodes.Last().Key == Loudness.HighEdgeHz && nodes.Last().Value == g.Curve[Loudness.Count - 1],
              "GraphicEQ covers 5 Hz - 22 kHz, held flat outside the standard's 20 Hz - 12.5 kHz");
        var full = Loudness.Curve(60, 80, 1.0, 0);
        var part60 = Loudness.Curve(60, 80, 0.6, 0);
        Check(full.Zip(part60, (a, b) => Math.Abs(b - 0.6 * a)).Max() < 1e-9, "strength scales the correction");
        Check(Loudness.Curve(40, 80, 1.0, 12).Max() <= 12 + 1e-9 && Loudness.Curve(40, 80, 1.0, 0).Max() > 12, "boost limit only applies when set");
        Check(Loudness.Make("", 0, 80, 1.0, 0) == null && Loudness.Make("", -5, 80, 1.0, 0) == null, "nothing added at or above the reference volume");
        Check(Math.Abs(Loudness.Curve(80, 80, 1.0, 0).Max()) < 1e-9 && Math.Abs(Loudness.Curve(80, 80, 1.0, 0).Min()) < 1e-9, "the curve is flat at the reference level");

        Console.WriteLine(fails == 0 ? "loudness: all checks passed" : "loudness: " + fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }
}

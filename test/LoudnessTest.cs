using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DacEqSwitcher;

// Loudness compensation against ISO 226:2003.
//  1. The contours match an independent implementation of the published tables and eq. (1).
//  2. Eq. (1) and the standard's separate inverse, eq. (2), agree; 1 kHz is the phon level.
//  3. End to end, from the reference to 60 dB below it, through the Equalizer APO lines the app
//     writes: the preamp (headroom) is the same at every volume, so the level follows the DAC
//     exactly; a sound balanced at the reference (every frequency at 80 phon) is heard at one
//     loudness level at every standard frequency, per eq. (2), wherever the standard applies.
//  4. The combined EQ + compensation never peaks above the EQ alone (no new clipping) at any
//     volume, and the headroom is no bigger than needed.
static class LT
{
    static int fails;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static void Check(bool ok, string what)
    {
        if (!ok) fails++;
        Console.WriteLine((ok ? "ok    " : "FAIL  ") + what);
    }

    static void Parse(string apo, out double pre, out Dictionary<double, double> pts)
    {
        // read back what Equalizer APO gets (parsed here, not by the app)
        pre = 0;
        pts = new Dictionary<double, double>();
        foreach (var raw in apo.Split('\n'))
        {
            var l = raw.Trim();
            if (l.StartsWith("Preamp:")) pre = double.Parse(l.Substring(7).Replace("dB", "").Trim(), Inv);
            if (l.StartsWith("GraphicEQ:"))
                foreach (var part in l.Substring(10).Split(';'))
                {
                    var t = part.Trim().Split(' ');
                    pts[double.Parse(t[0], Inv)] = double.Parse(t[1], Inv);
                }
        }
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
        foreach (var cap in new[] { 12.0, 30.0 })   // the default, and one high enough to test the standard unclamped
            foreach (var kv in profiles)
            {
                string tag = kv.Key + ", largest boost " + cap + " dB";
                double head = Loudness.Headroom(kv.Value, 80, 1.0, cap);
                var eq = EqText.Analyse(kv.Value, 3000, 5, 24000);
                double allowed = Math.Max(0, eq.PeakDb);
                double? refPre = null, ref1k = null;
                bool constantPre = true, followsKnob = true, equal = true, noClip = true;
                double worstPhon = 0, worstLevel = 0, highestPeak = double.MinValue;
                int exact = 0;
                foreach (var d in new[] { -5.0 }.Concat(Enumerable.Range(0, 121).Select(x => x * 0.5)))
                {
                    var plan = Loudness.Make(head, d, 80, 1.0, cap);
                    string apo = Loudness.ApoText(plan, "test") ?? "";
                    double pre; Dictionary<double, double> pts;
                    Parse(apo, out pre, out pts);
                    // the preamp is the same at every volume, including at and above the reference
                    if (refPre == null) refPre = pre;
                    if (Math.Abs(pre - refPre.Value) > 1e-9 || Math.Abs(pre + head) > 1e-9) constantPre = false;
                    // the level (1 kHz, where the correction is 0 by definition) follows the DAC exactly
                    double g1k = pts.Count == 0 ? 0 : pts[1000];
                    double level = pre + g1k - Math.Max(0, d);
                    if (ref1k == null) ref1k = level;
                    worstLevel = Math.Max(worstLevel, Math.Abs((ref1k.Value - level) - Math.Max(0, d)));
                    // every frequency heard at the same level (sound balanced at 80 phon at the reference,
                    // where the same preamp was already applied), wherever the standard applies and no boost is limited
                    if (d > 0 && 80 - d >= Loudness.MinPhon && plan.Curve.Max() < cap - 1e-9)
                    {
                        exact++;
                        for (int k = 0; k < Loudness.Count; k++)
                        {
                            double phon = Loudness.Phon(k, Loudness.Spl(k, 80) - d + pts[Loudness.Frequency(k)]);
                            // the target through the same two equations, so their 0.06 phon mismatch cancels
                            double want = Loudness.Phon(k, Loudness.Spl(k, 80 - d));
                            worstPhon = Math.Max(worstPhon, Math.Abs(phon - want));
                        }
                    }
                    // the EQ + correction never peaks above the EQ alone
                    if (d >= 0 && Math.Abs(d % 2) < 1e-9)
                    {
                        double peak = EqText.Analyse(kv.Value + "\r\nChannel: all\r\n" + apo, 3000, 5, 24000).PeakDb;
                        if (peak > allowed + 0.01) noClip = false;
                        highestPeak = Math.Max(highestPeak, peak);
                    }
                }
                followsKnob = worstLevel < 0.006;
                equal = worstPhon < 0.02;   // the written gains are rounded to 0.01 dB
                bool notOvercut = head == 0 || highestPeak >= allowed - 0.05;
                Check(constantPre && followsKnob && equal && noClip && notOvercut && exact > 0,
                      tag + ": headroom " + head.ToString("0.00", Inv) + " dB at every volume; level follows the DAC to " + worstLevel.ToString("0.000", Inv)
                      + " dB; " + exact + " levels exact to " + worstPhon.ToString("0.000", Inv) + " phon; peak at most " + highestPeak.ToString("0.00", Inv)
                      + " dB (EQ alone " + eq.PeakDb.ToString("0.00", Inv) + ")");
            }

        // edges, settings, and when there is nothing to do
        var g = Loudness.Make("", 15, 80, 1.0, 12);
        var nodes = Loudness.Nodes(g.Curve);
        Check(nodes.First().Key == Loudness.LowEdgeHz && nodes.First().Value == g.Curve[0] &&
              nodes.Last().Key == Loudness.HighEdgeHz && nodes.Last().Value == g.Curve[Loudness.Count - 1],
              "GraphicEQ covers 5 Hz - 22 kHz, held flat outside the standard's 20 Hz - 12.5 kHz");
        var full = Loudness.Curve(60, 80, 1.0, 0);
        var part60 = Loudness.Curve(60, 80, 0.6, 0);
        Check(full.Zip(part60, (a, b) => Math.Abs(b - 0.6 * a)).Max() < 1e-9, "strength scales the correction");
        Check(Loudness.Curve(40, 80, 1.0, 12).Max() <= 12 + 1e-9, "boosts stay within the largest boost");
        var atRef = Loudness.Make("", 0, 80, 1.0, 12);
        Check(!atRef.Boosting && !Loudness.Make("", -5, 80, 1.0, 12).Boosting && Loudness.ApoText(atRef, "x").Contains("Preamp") && !Loudness.ApoText(atRef, "x").Contains("GraphicEQ"),
              "at or above the reference: only the headroom, no correction");
        Check(Math.Abs(Loudness.Curve(80, 80, 1.0, 0).Max()) < 1e-9 && Math.Abs(Loudness.Curve(80, 80, 1.0, 0).Min()) < 1e-9, "the curve is flat at the reference level");

        Console.WriteLine(fails == 0 ? "loudness: all checks passed" : "loudness: " + fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }
}

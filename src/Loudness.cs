using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DacEqSwitcher
{
    /// Loudness compensation from the ISO 226:2003 normal equal-loudness-level contours.
    ///
    /// Hearing gets less sensitive to bass (and some treble) as level drops. If music was
    /// balanced at the reference level R (phon), listening at L needs the difference between
    /// the two contours added back, normalised at 1 kHz:
    ///     gain(f) = [Lp(f, L) - Lp(1k, L)] - [Lp(f, R) - Lp(1k, R)]
    /// so that every frequency is again heard at the same loudness level (L phon).
    ///
    /// The DAC's volume comes after Equalizer APO, so the boost can only be made digitally by
    /// cutting everything else (a preamp); that cut lowers the level actually heard. The plan
    /// below therefore solves for the heard level: heard = knob drop + preamp(heard), with the
    /// preamp sized on the active EQ and the compensation together.
    ///
    /// ISO 226:2023's tables are only in the paid standard; the 2003 edition is the one open
    /// implementations use. It defines 20 Hz - 12.5 kHz, 20-90 phon (5-12.5 kHz: up to 80 phon).
    static class Loudness
    {
        static readonly double[] F = { 20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800,
                                       1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500 };
        static readonly double[] Af = { 0.532, 0.506, 0.480, 0.455, 0.432, 0.409, 0.387, 0.367, 0.349, 0.330, 0.315, 0.301, 0.288,
                                        0.276, 0.267, 0.259, 0.253, 0.250, 0.246, 0.244, 0.243, 0.243, 0.243, 0.242, 0.242, 0.245,
                                        0.254, 0.271, 0.301 };
        static readonly double[] Lu = { -31.6, -27.2, -23.0, -19.1, -15.9, -13.0, -10.3, -8.1, -6.2, -4.5, -3.1, -2.0, -1.1, -0.4,
                                        0.0, 0.3, 0.5, 0.0, -2.7, -4.1, -1.0, 1.7, 2.5, 1.2, -2.1, -7.1, -11.2, -10.7, -3.1 };
        static readonly double[] Tf = { 78.5, 68.7, 59.5, 51.1, 44.0, 37.5, 31.5, 26.5, 22.1, 17.9, 14.4, 11.4, 8.6, 6.2, 4.4, 3.0,
                                        2.2, 2.4, 3.5, 1.7, -1.3, -4.2, -6.0, -5.4, -1.5, 6.0, 12.6, 13.9, 12.3 };
        const int K1000 = 17;

        public const double MinPhon = 20, MaxPhon = 90, MaxRefPhon = 80;   // the standard's valid range
        public const double LowEdgeHz = 5, HighEdgeHz = 22000;            // the GraphicEQ spans the whole audio band

        public static int Count { get { return F.Length; } }
        public static double Frequency(int i) { return F[i]; }

        /// ISO 226:2003 eq. (1): sound pressure level (dB) of a tone at table index i heard at `phon`.
        public static double Spl(int i, double phon)
        {
            double a = 4.47e-3 * (Math.Pow(10, 0.025 * phon) - 1.15)
                     + Math.Pow(0.4 * Math.Pow(10, (Tf[i] + Lu[i]) / 10 - 9), Af[i]);
            return 10 / Af[i] * Math.Log10(a) - Lu[i] + 94;
        }

        /// ISO 226:2003 eq. (2), the inverse: loudness level (phon) of a tone at index i played at `spl` dB.
        public static double Phon(int i, double spl)
        {
            double b = Math.Pow(0.4 * Math.Pow(10, (spl + Lu[i]) / 10 - 9), Af[i])
                     - Math.Pow(0.4 * Math.Pow(10, (Tf[i] + Lu[i]) / 10 - 9), Af[i]) + 0.005135;
            return 40 * Math.Log10(b) + 94;
        }

        /// Gain per table frequency (dB) to bring `listenPhon` up to the balance of `refPhon`.
        /// strength 1 = the standard's full correction; maxBoost <= 0 = no limit.
        public static double[] Curve(double listenPhon, double refPhon, double strength, double maxBoost)
        {
            refPhon = Math.Max(MinPhon, Math.Min(MaxRefPhon, refPhon));
            listenPhon = Math.Max(MinPhon, Math.Min(refPhon, listenPhon));
            double l1k = Spl(K1000, listenPhon), r1k = Spl(K1000, refPhon);
            var g = new double[F.Length];
            for (int i = 0; i < F.Length; i++)
            {
                double d = strength * ((Spl(i, listenPhon) - l1k) - (Spl(i, refPhon) - r1k));
                g[i] = maxBoost > 0 ? Math.Max(-maxBoost, Math.Min(maxBoost, d)) : d;
            }
            return g;
        }

        /// GraphicEQ points: the ISO frequencies, held flat beyond them to the edges of the audio band
        /// (the standard has no data outside 20 Hz - 12.5 kHz; holding never exaggerates it).
        public static List<KeyValuePair<double, double>> Nodes(double[] g)
        {
            var n = new List<KeyValuePair<double, double>> { new KeyValuePair<double, double>(LowEdgeHz, g[0]) };
            for (int i = 0; i < F.Length; i++) n.Add(new KeyValuePair<double, double>(F[i], g[i]));
            n.Add(new KeyValuePair<double, double>(HighEdgeHz, g[F.Length - 1]));
            return n;
        }

        /// Equalizer APO's GraphicEQ interpolation: linear in dB on a log-frequency axis, flat outside.
        public static double Interpolate(List<KeyValuePair<double, double>> pts, double f)
        {
            if (f <= pts[0].Key) return pts[0].Value;
            for (int i = 1; i < pts.Count; i++)
                if (f <= pts[i].Key)
                {
                    double t = Math.Log(f / pts[i - 1].Key) / Math.Log(pts[i].Key / pts[i - 1].Key);
                    return pts[i - 1].Value + t * (pts[i].Value - pts[i - 1].Value);
                }
            return pts[pts.Count - 1].Value;
        }

        public sealed class Plan
        {
            public double KnobBelow;     // DAC volume below the reference volume (dB)
            public double HeardBelow;    // level actually heard below the reference (dB): knob + preamp
            public double PreampDb;      // <= 0; the extra cut so EQ + compensation can't clip
            public double[] Curve;       // gain at each ISO frequency
            public double BassDb { get { return Curve[4]; } }   // at 50 Hz, for the UI
        }

        const int GridPoints = 1600;     // ~0.4 % spacing, 5 Hz - 24 kHz: finds the peaks of narrow EQ filters too

        /// Works out the compensation for the DAC being `knobBelow` dB under the reference volume,
        /// on top of the EQ `profileText` (Equalizer APO commands; "" for none). Null: nothing to add.
        public static Plan Make(string profileText, double knobBelow, double refPhon, double strength, double maxBoost)
        {
            if (knobBelow <= 0) return null;
            refPhon = Math.Max(MinPhon, Math.Min(MaxRefPhon, refPhon));
            var eq = EqText.Analyse(profileText ?? "", GridPoints, LowEdgeHz, 24000);
            var basePeak = new double[GridPoints];
            double eqPeak = double.MinValue;
            for (int i = 0; i < GridPoints; i++) { basePeak[i] = Math.Max(eq.L[i], eq.R[i]); eqPeak = Math.Max(eqPeak, basePeak[i]); }
            double allowed = Math.Max(0, eqPeak);   // never louder than the EQ alone peaks (0 dBFS when it has headroom)

            double heard = knobBelow, cut = 0;
            double[] g = null;
            for (int k = 0; k < 200; k++)
            {
                g = Curve(refPhon - heard, refPhon, strength, maxBoost);
                var nodes = Nodes(g);
                double peak = double.MinValue;
                for (int i = 0; i < GridPoints; i++) peak = Math.Max(peak, basePeak[i] + Interpolate(nodes, eq.Freqs[i]));
                cut = Math.Max(0, peak + 0.005 - allowed);   // + the 0.01 dB rounding of the written gains
                double next = knobBelow + cut;
                // the cut grows by at most ~0.55 dB per dB of level, so this converges geometrically
                if (Math.Abs(next - heard) < 1e-4) { heard = next; break; }
                heard = next;
            }
            g = Curve(refPhon - heard, refPhon, strength, maxBoost);
            if (g.All(x => Math.Abs(x) < 0.005)) return null;
            return new Plan { KnobBelow = knobBelow, HeardBelow = heard, PreampDb = -Math.Ceiling(cut * 100) / 100, Curve = g };
        }

        /// Equalizer APO lines for a plan: the preamp, then the GraphicEQ.
        public static string ApoText(Plan p, string comment)
        {
            if (p == null) return null;
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("# ").Append(comment).Append("\r\nChannel: all\r\n");
            if (p.PreampDb < 0) sb.Append("Preamp: ").Append(p.PreampDb.ToString("0.00", inv)).Append(" dB\r\n");
            sb.Append("GraphicEQ: ");
            sb.Append(string.Join("; ", Nodes(p.Curve).Select(n => n.Key.ToString("0.#", inv) + " " + n.Value.ToString("0.00", inv))));
            sb.Append("\r\n");
            return sb.ToString();
        }
    }
}

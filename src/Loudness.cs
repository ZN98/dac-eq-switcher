using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DacEqSwitcher
{
    /// Loudness compensation from the ISO 226:2003 normal equal-loudness-level contours.
    ///
    /// Hearing gets less sensitive to bass (and a little to treble) as level drops. If music
    /// was balanced at the reference level, listening N dB quieter needs the difference
    /// between the two contours added back:  gain(f) = [Lp(f, L) - Lp(1k, L)] - [Lp(f, R) - Lp(1k, R)]
    /// with L the listening level and R the reference level in phon. Turning the DAC down by
    /// 1 dB lowers L by 1 phon, so L = R - (reference volume - current volume).
    ///
    /// The ISO 226:2023 revision's tables are only published in the paid standard; the 2003
    /// edition below is the one open implementations use.
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

        public const double MinPhon = 20, MaxPhon = 90;   // the formula's valid range

        /// Sound pressure level (dB) of a tone at table index i that sounds as loud as `phon`.
        public static double Spl(int i, double phon)
        {
            double a = 4.47e-3 * (Math.Pow(10, 0.025 * phon) - 1.15)
                     + Math.Pow(0.4 * Math.Pow(10, (Tf[i] + Lu[i]) / 10 - 9), Af[i]);
            return 10 / Af[i] * Math.Log10(a) - Lu[i] + 94;
        }

        public static double[] Frequencies { get { return (double[])F.Clone(); } }

        /// Gain per table frequency (dB) to bring `listenPhon` up to the balance of `refPhon`.
        public static double[] Curve(double listenPhon, double refPhon, double strength, double maxBoost)
        {
            listenPhon = Math.Max(MinPhon, Math.Min(MaxPhon, listenPhon));
            refPhon = Math.Max(MinPhon, Math.Min(MaxPhon, refPhon));
            double l1k = Spl(K1000, listenPhon), r1k = Spl(K1000, refPhon);
            var g = new double[F.Length];
            for (int i = 0; i < F.Length; i++)
            {
                double d = (Spl(i, listenPhon) - l1k) - (Spl(i, refPhon) - r1k);
                g[i] = Math.Max(-maxBoost, Math.Min(maxBoost, strength * d));
            }
            return g;
        }

        /// Equalizer APO lines for the curve: a GraphicEQ, plus a preamp equal to its largest
        /// boost so the extra bass can't clip. Returns null when there is nothing to add.
        public static string ApoText(double[] g, string comment)
        {
            if (g == null || g.All(x => Math.Abs(x) < 0.05)) return null;
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("# ").Append(comment).Append("\r\nChannel: all\r\n");
            double peak = Math.Max(0, g.Max());
            if (peak > 0.05) sb.Append("Preamp: ").Append((-peak).ToString("0.0", inv)).Append(" dB\r\n");
            sb.Append("GraphicEQ: ");
            for (int i = 0; i < F.Length; i++)
                sb.Append(F[i].ToString("0.#", inv)).Append(' ').Append(g[i].ToString("0.0", inv)).Append("; ");
            // above the table's last point the contours aren't defined: hold the last value
            sb.Append("16000 ").Append(g[F.Length - 1].ToString("0.0", inv)).Append("; ");
            sb.Append("20000 ").Append(g[F.Length - 1].ToString("0.0", inv)).Append("\r\n");
            return sb.ToString();
        }

        /// The boost at 50 Hz, used to describe the effect in the UI.
        public static double BassBoost(double[] g) { return g == null ? 0 : g[4]; }
    }
}

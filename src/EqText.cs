using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DacEqSwitcher
{
    class ImportException : Exception
    {
        public ImportException(string m) : base(m) { }
    }

    /// Equalizer APO text: import (Peace / APO / AutoEQ / REW), checks and response curve.
    static class EqText
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static readonly HashSet<string> Commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "preamp", "filter", "channel", "graphiceq", "copy", "delay", "convolution", "stage", "include",
            "device", "eval", "if", "elseif", "else", "endif", "loudnesscorrection",
        };

        static readonly Regex CommandRe = new Regex(@"^\s*([A-Za-z]+)(\s*\d+)?\s*:", RegexOptions.Compiled);

        public static string Decode(byte[] data)
        {
            if (data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF)))
                return (data[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(data, 2, data.Length - 2);
            try
            {
                var s = new UTF8Encoding(false, true).GetString(data);
                return s.Length > 0 && s[0] == '﻿' ? s.Substring(1) : s;
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(1252).GetString(data);
            }
        }

        /// Returns Equalizer APO text for a Peace preset or any APO-style text file.
        public static string Import(string fileName, byte[] data, List<string> notes)
        {
            string text = Decode(data);
            if (fileName.EndsWith(".peace", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith("[Frequencies]"))
                return FromPeace(text);
            return CleanApo(text, notes);
        }

        /// Comments out anything Equalizer APO wouldn't understand (REW/AutoEQ headers) and Device: lines.
        public static string CleanApo(string text, List<string> notes)
        {
            var o = new StringBuilder();
            int commands = 0;
            foreach (var raw in SplitLines(text))
            {
                string s = raw.Trim();
                if (s.Length == 0 || s.StartsWith("#")) { o.AppendLine(raw); continue; }
                string cmd = CommandOf(s);
                if (cmd == null) { o.AppendLine("# " + raw); continue; }
                if (cmd == "device")
                {
                    o.AppendLine("# " + raw);
                    AddNote(notes, "A 'Device:' line was commented out: the app decides when this EQ applies.");
                    continue;
                }
                if (cmd == "include")
                    AddNote(notes, "This EQ includes another file. That file must exist in the Equalizer APO config folder.");
                commands++;
                o.AppendLine(raw);
            }
            if (commands == 0)
                throw new ImportException("No Equalizer APO commands (Preamp:, Filter:, GraphicEQ: …) were found in this file.");
            return o.ToString().TrimEnd() + "\r\n";
        }

        static void AddNote(List<string> notes, string n)
        {
            if (notes != null && !notes.Contains(n)) notes.Add(n);
        }

        public static string CommandOf(string line)
        {
            var m = CommandRe.Match(line);
            if (!m.Success) return null;
            string c = m.Groups[1].Value.ToLowerInvariant();
            return Commands.Contains(c) ? c : null;
        }

        public static IEnumerable<string> SplitLines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        /// Line numbers (1-based) Equalizer APO won't understand.
        public static List<int> UnknownLines(string text)
        {
            var bad = new List<int>();
            int n = 0;
            foreach (var raw in SplitLines(text))
            {
                n++;
                string s = raw.Trim();
                if (s.Length == 0 || s.StartsWith("#")) continue;
                if (CommandOf(s) == null) bad.Add(n);
            }
            return bad;
        }

        // ------------------------------------------------------------ Peace --
        // Peace stores filter types as numbers; peak is the default. 14/15 are
        // the shelf-with-Q types its AutoEQ-style presets use.
        static readonly Dictionary<string, string> PeaceTypes = new Dictionary<string, string> { { "", "PK" }, { "14", "LSC" }, { "15", "HSC" } };

        public static string FromPeace(string text)
        {
            var ini = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> cur = null;
            foreach (var raw in SplitLines(text))
            {
                string s = raw.Trim();
                if (s.StartsWith("[") && s.EndsWith("]"))
                {
                    cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    ini[s.Substring(1, s.Length - 2)] = cur;
                }
                else if (cur != null && s.Contains("="))
                {
                    int i = s.IndexOf('=');
                    cur[s.Substring(0, i).Trim()] = s.Substring(i + 1).Trim();
                }
            }
            if (!ini.ContainsKey("Frequencies"))
                throw new ImportException("This doesn't look like a Peace preset (no [Frequencies] section).");

            Func<string, Dictionary<string, string>> sec = n =>
            {
                Dictionary<string, string> d;
                return ini.TryGetValue(n, out d) ? d : new Dictionary<string, string>();
            };
            var general = sec("General");
            var speakers = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("", "all") };
            if (ini.ContainsKey("Speakers"))
            {
                var sp = sec("Speakers");
                speakers = sp.Keys.Where(k => k.StartsWith("SpeakerId", StringComparison.OrdinalIgnoreCase))
                    .Select(k => k.Substring(9)).Where(id => id.All(char.IsDigit) && id.Length > 0)
                    .OrderBy(id => int.Parse(id, Inv))
                    .Select(id =>
                    {
                        string t;
                        return new KeyValuePair<string, string>(id == "0" ? "" : id, sp.TryGetValue("SpeakerTargets" + id, out t) ? t : "all");
                    }).ToList();
            }

            var o = new StringBuilder();
            foreach (var spk in speakers)
            {
                string sfx = spk.Key;
                if (!ini.ContainsKey("Frequencies" + sfx)) continue;
                var freqs = sec("Frequencies" + sfx);
                var gains = sec("Gains" + sfx);
                var qs = sec("Qualities" + sfx);
                var types = sec("Filters" + sfx);
                var lines = new List<string>();
                foreach (var key in freqs.Keys.OrderBy(k => NumSuffix(k, "Frequency")))
                {
                    string n = key.Substring("Frequency".Length);
                    string gs;
                    double gain = gains.TryGetValue("Gain" + n, out gs) ? ParseD(gs) : 0;
                    if (gain == 0) continue;
                    string code;
                    if (!types.TryGetValue("Filter" + n, out code)) code = "";
                    string type;
                    if (!PeaceTypes.TryGetValue(code, out type))
                        throw new ImportException("Filter " + n + " uses Peace filter type " + code + ", which can't be converted yet. " +
                                                  "If Peace can export this preset as an Equalizer APO .txt file, import that instead.");
                    string q;
                    if (!qs.TryGetValue("Quality" + n, out q)) q = "1.41";
                    lines.Add("Filter: ON " + type + " Fc " + freqs[key] + " Hz Gain " + gain.ToString("0.###", Inv) + " dB Q " + q);
                }
                string pre;
                general.TryGetValue("PreAmp" + sfx, out pre);
                if (lines.Count == 0 && string.IsNullOrEmpty(pre)) continue;
                o.AppendLine("Channel: " + (spk.Value.Equals("all", StringComparison.OrdinalIgnoreCase) ? "all" : spk.Value));
                if (!string.IsNullOrEmpty(pre)) o.AppendLine("Preamp: " + ParseD(pre).ToString("0.###", Inv) + " dB");
                foreach (var l in lines) o.AppendLine(l);
                o.AppendLine();
            }
            if (o.Length == 0) throw new ImportException("The Peace preset has no active filters.");
            o.AppendLine("Channel: all");
            return o.ToString();
        }

        static int NumSuffix(string k, string prefix)
        {
            int n;
            return int.TryParse(k.Substring(prefix.Length), NumberStyles.Integer, Inv, out n) ? n : int.MaxValue;
        }

        static double ParseD(string s)
        {
            double d;
            return double.TryParse(s, NumberStyles.Float, Inv, out d) ? d : 0;
        }

        // ---------------------------------------------------------- response --
        public sealed class Response
        {
            public double[] Freqs, L, R;
            public double PeakDb, PeakHz;
            public int Skipped;
            public bool Identical;
        }

        const double Fs = 48000;
        static readonly Regex FilterRe = new Regex(
            @"^Filter\s*\d*\s*:\s*ON\s+(\w+)\s+Fc\s+([\d.]+)\s*Hz(?:\s+Gain\s+(-?[\d.]+)\s*dB)?(?:\s+Q\s+([\d.]+))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex DbRe = new Regex(@"(-?[\d.]+)\s*dB", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static Response Analyse(string text, int points = 200, double lo = 20, double hi = 20000)
        {
            var f = new double[points];
            for (int i = 0; i < points; i++) f[i] = lo * Math.Pow(hi / lo, i / (double)(points - 1));
            var L = new double[points];
            var R = new double[points];
            bool toL = true, toR = true;
            int skipped = 0;
            foreach (var raw in SplitLines(text))
            {
                string s = raw.Trim();
                if (s.Length == 0 || s.StartsWith("#")) continue;
                string low = s.ToLowerInvariant();
                if (low.StartsWith("channel:"))
                {
                    var names = s.Substring(8).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    bool all = names.Any(n => n.Equals("all", StringComparison.OrdinalIgnoreCase));
                    toL = all || names.Any(n => n == "L" || n == "1");
                    toR = all || names.Any(n => n == "R" || n == "2");
                    continue;
                }
                if (low.StartsWith("preamp:"))
                {
                    var m = DbRe.Match(s);
                    if (m.Success) Add(L, R, toL, toR, i => ParseD(m.Groups[1].Value), points);
                    continue;
                }
                if (low.StartsWith("graphiceq:"))
                {
                    var pts = GraphicPoints(s.Substring(10));
                    if (pts.Count == 0) { skipped++; continue; }
                    Add(L, R, toL, toR, i => Graphic(pts, f[i]), points);
                    continue;
                }
                var fm = FilterRe.Match(s);
                if (fm.Success)
                {
                    double[] c = Biquad(fm.Groups[1].Value.ToUpperInvariant(), ParseD(fm.Groups[2].Value),
                        fm.Groups[3].Success ? ParseD(fm.Groups[3].Value) : 0, fm.Groups[4].Success ? ParseD(fm.Groups[4].Value) : 0.7071);
                    if (c == null) { skipped++; continue; }
                    Add(L, R, toL, toR, i => MagDb(c, f[i]), points);
                }
                else if (Regex.IsMatch(low, @"^(filter\s*\d*|convolution|include)\s*:") && !low.Contains(" off"))
                    skipped++;
            }
            var res = new Response { Freqs = f, L = L, R = R, Skipped = skipped, Identical = true };
            res.PeakDb = double.MinValue;
            for (int i = 0; i < points; i++)
            {
                if (Math.Abs(L[i] - R[i]) > 0.01) res.Identical = false;
                double m = Math.Max(L[i], R[i]);
                if (m > res.PeakDb) { res.PeakDb = m; res.PeakHz = f[i]; }
            }
            return res;
        }

        static List<KeyValuePair<double, double>> GraphicPoints(string body)
        {
            var pts = new List<KeyValuePair<double, double>>();
            foreach (var part in body.Split(';'))
            {
                var t = part.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                double fr, g;
                if (t.Length == 2 && double.TryParse(t[0], NumberStyles.Float, Inv, out fr) && double.TryParse(t[1], NumberStyles.Float, Inv, out g) && fr > 0)
                    pts.Add(new KeyValuePair<double, double>(fr, g));
            }
            return pts.OrderBy(p => p.Key).ToList();
        }

        /// Equalizer APO's GraphicEQ: linear in dB between points on a log-frequency axis.
        static double Graphic(List<KeyValuePair<double, double>> pts, double f)
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

        static void Add(double[] L, double[] R, bool toL, bool toR, Func<int, double> d, int n)
        {
            for (int i = 0; i < n; i++)
            {
                double v = d(i);
                if (toL) L[i] += v;
                if (toR) R[i] += v;
            }
        }

        static double[] Biquad(string kind, double fc, double gain, double q)
        {
            double A = Math.Pow(10, gain / 40), w0 = 2 * Math.PI * fc / Fs;
            if (!(w0 > 0 && w0 < Math.PI) || q <= 0) return null;
            double c = Math.Cos(w0), s = Math.Sin(w0), al = s / (2 * q), k = 2 * Math.Sqrt(A) * al;
            switch (kind)
            {
                case "PK": case "PEQ": case "MODAL":
                    return new[] { 1 + al * A, -2 * c, 1 - al * A, 1 + al / A, -2 * c, 1 - al / A };
                case "LS": case "LSC":
                    return new[] { A * ((A + 1) - (A - 1) * c + k), 2 * A * ((A - 1) - (A + 1) * c), A * ((A + 1) - (A - 1) * c - k),
                                   (A + 1) + (A - 1) * c + k, -2 * ((A - 1) + (A + 1) * c), (A + 1) + (A - 1) * c - k };
                case "HS": case "HSC":
                    return new[] { A * ((A + 1) + (A - 1) * c + k), -2 * A * ((A - 1) + (A + 1) * c), A * ((A + 1) + (A - 1) * c - k),
                                   (A + 1) - (A - 1) * c + k, 2 * ((A - 1) - (A + 1) * c), (A + 1) - (A - 1) * c - k };
                case "LP": case "LPQ":
                    return new[] { (1 - c) / 2, 1 - c, (1 - c) / 2, 1 + al, -2 * c, 1 - al };
                case "HP": case "HPQ":
                    return new[] { (1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + al, -2 * c, 1 - al };
                default:
                    return null;
            }
        }

        static double MagDb(double[] c, double f)
        {
            double w = 2 * Math.PI * f / Fs;
            double cr = Math.Cos(w), ci = -Math.Sin(w), c2r = Math.Cos(2 * w), c2i = -Math.Sin(2 * w);
            double nr = c[0] + c[1] * cr + c[2] * c2r, ni = c[1] * ci + c[2] * c2i;
            double dr = c[3] + c[4] * cr + c[5] * c2r, di = c[4] * ci + c[5] * c2i;
            double mag = Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
            return 20 * Math.Log10(Math.Max(mag, 1e-12));
        }
    }
}

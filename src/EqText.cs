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

        /// Every command Equalizer APO reads. It matches them case-sensitively; "Filter" is a prefix
        /// ("Filter 1:", "Filter1:").
        public static readonly string[] Commands =
        {
            "Preamp", "Filter", "Channel", "GraphicEQ", "Copy", "Delay", "Convolution", "Stage", "Include",
            "Device", "Eval", "If", "ElseIf", "Else", "EndIf", "VSTPlugin", "LoudnessCorrection",
        };

        static readonly Regex CommandRe = new Regex(@"^\s*([A-Za-z]+)([^:]*):", RegexOptions.Compiled);

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
                if (cmd == null)
                {
                    // the right command in the wrong case would be ignored by Equalizer APO: fix it
                    var m = CommandRe.Match(raw);
                    string fixedName = m.Success ? Commands.FirstOrDefault(c => string.Equals(c, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) : null;
                    if (fixedName != null && (fixedName == "Filter" || m.Groups[2].Value.Trim().Length == 0))
                    {
                        string line = raw.Substring(0, m.Groups[1].Index) + fixedName + raw.Substring(m.Groups[1].Index + m.Groups[1].Length);
                        AddNote(notes, "Command names were corrected to Equalizer APO's spelling (it ignores e.g. \"preamp:\" in lower case).");
                        o.AppendLine(line);
                        commands++;
                        continue;
                    }
                    o.AppendLine("# " + raw);
                    continue;
                }
                if (cmd == "Device")
                {
                    o.AppendLine("# " + raw);
                    AddNote(notes, "A 'Device:' line was commented out: the app decides when this EQ applies.");
                    continue;
                }
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

        /// The Equalizer APO command on this line, or null if it would ignore the line.
        public static string CommandOf(string line)
        {
            int colon = line.IndexOf(':');
            if (colon < 0) return null;
            string key = line.Substring(0, colon).Trim();
            if (key.StartsWith("Filter", StringComparison.Ordinal)) return "Filter";
            return Commands.Contains(key) ? key : null;
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
            public double[] Freqs, L, R, Worst;
            public double PeakDb, PeakHz, SampleRate;
            public int Skipped;
            public bool Identical;
            public List<string> Notes = new List<string>();
        }

        /// The sample rate the DAC runs at (Windows' shared-mode format); 48 kHz until it's known.
        public static double DeviceSampleRate = 48000;

        /// Frequency response as Equalizer APO applies it (see ApoEngine). baseDir: where relative
        /// Include/Convolution paths start (null: the Equalizer APO config folder).
        public static Response Analyse(string text, int points = 1000, double lo = 20, double hi = 20000, string baseDir = null, double sampleRate = 0)
        {
            return ApoEngine.Analyse(text ?? "", new ApoEngine.Options
            {
                Points = points, Lo = lo, Hi = hi, BaseDir = baseDir ?? DefaultBaseDir,
                SampleRate = sampleRate > 0 ? sampleRate : DeviceSampleRate,
            });
        }

        public static string DefaultBaseDir = "";
    }
}

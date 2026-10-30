// DAC EQ Switcher - Copyright 2026 ZN98 - GPL-3.0-or-later (see LICENSE and NOTICE).
// Ported from Equalizer APO (FilterEngine, the filter factories, BiQuad, BiQuadFilter, IIRFilter,
// GraphicEQFilter, GainIterator, DelayFilter, CopyFilter, ChannelFilter, ConvolutionFilter),
// Copyright (C) 2013 Jonas Thedering, GPL-2.0-or-later, used under GPL-3.0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DacEqSwitcher
{
    /// Works out what Equalizer APO does with a configuration, following its FilterEngine and filter
    /// factories line by line: Device, If/ElseIf/Else/EndIf, Eval and inline `expressions`, Include,
    /// Stage, Channel, Filter (every biquad type and IIR), Preamp, Delay, Copy, Convolution and
    /// GraphicEQ (its 16384-tap minimum-phase FIR). Each channel keeps a complex response from both
    /// inputs, so Copy, Delay and phase combine as they do in the audio.
    static class ApoEngine
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public sealed class Options
        {
            public double SampleRate = 48000;
            public string BaseDir;          // where relative Include/Convolution paths start
            public string FileName = "eq.txt";
            public int Points = 1000;
            public double Lo = 20, Hi = 20000;
        }

        public static EqText.Response Analyse(string text, Options o)
        {
            var freqs = new double[o.Points];
            double hi = Math.Min(o.Hi, o.SampleRate / 2 * 0.999);
            for (int i = 0; i < o.Points; i++) freqs[i] = o.Lo * Math.Pow(hi / o.Lo, i / (double)(o.Points - 1));
            var notes = new List<string>();
            int skipped = 0;

            // pre-mix runs first (per application), then post-mix; without Stage commands only post-mix does anything
            Chan[] start = null;
            bool hasStage = EqText.SplitLines(text).Any(l => KeyOf(l) == "Stage");
            if (hasStage)
            {
                var pre = new Run(o, freqs, notes, true, null);
                pre.File(text, Path.Combine(o.BaseDir ?? "", o.FileName), 0);
                start = pre.Outputs();
                skipped += pre.Skipped;
            }
            var post = new Run(o, freqs, notes, false, start);
            post.File(text, Path.Combine(o.BaseDir ?? "", o.FileName), 0);
            skipped += post.Skipped;
            var outs = post.Outputs();

            var res = new EqText.Response { Freqs = freqs, L = new double[o.Points], R = new double[o.Points], Worst = new double[o.Points], Skipped = skipped, Identical = true,
                                            SampleRate = o.SampleRate, Notes = notes.Distinct().ToList() };
            res.PeakDb = double.MinValue;
            for (int i = 0; i < o.Points; i++)
            {
                res.L[i] = outs[0].MonoDb(i);
                res.R[i] = outs[1].MonoDb(i);
                if (Math.Abs(res.L[i] - res.R[i]) > 0.01) res.Identical = false;
                double worst = Math.Max(outs[0].WorstDb(i), outs[1].WorstDb(i));   // any mix of the two inputs
                res.Worst[i] = worst;
                if (freqs[i] < 20) continue;
                if (worst > res.PeakDb) { res.PeakDb = worst; res.PeakHz = freqs[i]; }
            }
            if (res.PeakDb == double.MinValue) res.PeakDb = 0;
            return res;
        }

        static string KeyOf(string line)
        {
            int p = line.IndexOf(':');
            return p < 0 ? null : line.Substring(0, p).Trim();
        }

        // --------------------------------------------------------- channels --
        /// One channel's response: complex gain from input L and from input R at each frequency.
        sealed class Chan
        {
            public double[] Lr, Li, Rr, Ri;

            public Chan(int n, int input)
            {
                Lr = new double[n]; Li = new double[n]; Rr = new double[n]; Ri = new double[n];
                if (input == 0) for (int i = 0; i < n; i++) Lr[i] = 1;
                if (input == 1) for (int i = 0; i < n; i++) Rr[i] = 1;
            }

            public Chan Clone()
            {
                return new Chan(0, -1) { Lr = (double[])Lr.Clone(), Li = (double[])Li.Clone(), Rr = (double[])Rr.Clone(), Ri = (double[])Ri.Clone() };
            }

            public void Mul(double[] hr, double[] hi)
            {
                for (int i = 0; i < Lr.Length; i++)
                {
                    double a = Lr[i], b = Li[i];
                    Lr[i] = a * hr[i] - b * hi[i]; Li[i] = a * hi[i] + b * hr[i];
                    a = Rr[i]; b = Ri[i];
                    Rr[i] = a * hr[i] - b * hi[i]; Ri[i] = a * hi[i] + b * hr[i];
                }
            }

            public void Scale(double g)
            {
                for (int i = 0; i < Lr.Length; i++) { Lr[i] *= g; Li[i] *= g; Rr[i] *= g; Ri[i] *= g; }
            }

            static double Db(double m) { return 20 * Math.Log10(Math.Max(m, 1e-10)); }
            public double MonoDb(int i) { double r = Lr[i] + Rr[i], im = Li[i] + Ri[i]; return Db(Math.Sqrt(r * r + im * im)); }
            public double WorstDb(int i) { return Db(Math.Sqrt(Lr[i] * Lr[i] + Li[i] * Li[i]) + Math.Sqrt(Rr[i] * Rr[i] + Ri[i] * Ri[i])); }
        }

        // -------------------------------------------------------------- run --
        /// One load of the configuration, as one Equalizer APO instance (pre-mix or post-mix) does it.
        sealed class Run
        {
            readonly Options o;
            readonly double[] f;
            readonly List<string> notes;
            readonly bool preMix;
            readonly ApoExpr expr;
            readonly List<string> all = new List<string> { "L", "R" };
            readonly Dictionary<string, Chan> ch = new Dictionary<string, Chan>();
            List<string> current;
            public int Skipped;

            // If state (IfFilterFactory)
            int trueCount, falseCount;
            bool executeElse;
            readonly Stack<int> trueStack = new Stack<int>();
            // Stage state (StageFilterFactory)
            bool stageMatches;
            readonly Stack<bool> stageStack = new Stack<bool>();
            bool deviceNoted;

            public Run(Options o, double[] freqs, List<string> notes, bool preMix, Chan[] start)
            {
                this.o = o; f = freqs; this.notes = notes; this.preMix = preMix;
                ch["L"] = start != null ? start[0].Clone() : new Chan(f.Length, 0);
                ch["R"] = start != null ? start[1].Clone() : new Chan(f.Length, 1);
                current = new List<string>(all);
                expr = new ApoExpr(o.SampleRate, 2, 2, preMix ? "pre-mix" : "post-mix", "", "", "");
                stageMatches = !preMix;   // capture || !preMix || !postMixInstalled
            }

            public Chan[] Outputs() { return new[] { ch["L"], ch["R"] }; }

            void Note(string where, string msg) { notes.Add(where + ": " + msg); }
            void NotDrawn(string where, string msg) { Skipped++; Note(where, msg); }

            public void File(string text, string path, int depth)
            {
                var savedSel = current;
                trueStack.Push(trueCount);
                trueCount = 0;
                executeElse = false;
                if (falseCount != 0) { Note(Path.GetFileName(path), "included inside an If that is false"); falseCount = 0; }
                stageStack.Push(stageMatches);

                int n = 0;
                foreach (var raw in EqText.SplitLines(text))
                {
                    n++;
                    string where = (depth == 0 ? "Line " : Path.GetFileName(path) + " line ") + n;
                    int pos = raw.IndexOf(':');
                    if (pos < 0) continue;
                    string key = raw.Substring(0, pos).Trim(), value = raw.Substring(pos + 1);
                    try { Line(key, value, path, depth, where); }
                    catch (ExprException ex) { Note(where, ex.Message); }
                }

                if (trueCount != 0 || falseCount != 0) { Note(Path.GetFileName(path), "an If isn't closed by EndIf"); falseCount = 0; }
                trueCount = trueStack.Pop();
                stageMatches = stageStack.Pop();
                current = savedSel;
            }

            void Line(string key, string value, string path, int depth, string where)
            {
                // Device
                if (key == "Device")
                {
                    if (!deviceNoted) { deviceNoted = true; Note(where, "Device: lines are assumed to match your DAC in this graph"); }
                    return;
                }
                // If / ElseIf / Else / EndIf
                if (If(key, value, where)) return;
                // inline expressions and Eval
                if (key.Length > 0 && key[0] == '#') return;
                value = Inline(value, where);
                if (key == "Eval") { expr.Eval(value.Trim()); return; }
                // Include
                if (key == "Include")
                {
                    string p = Resolve(path, value.TrimStart());
                    if (depth + 1 >= 100) { Note(where, "includes nested too deeply"); return; }
                    string text;
                    try { text = EqText.Decode(System.IO.File.ReadAllBytes(p)); }
                    catch (Exception) { NotDrawn(where, "the included file " + value.Trim() + " wasn't found"); return; }
                    File(text, p, depth + 1);
                    return;
                }
                // Stage
                if (key == "Stage")
                {
                    stageMatches = false;
                    foreach (var part in value.Trim().ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (part == "pre-mix" && preMix) stageMatches = true;
                        else if (part == "post-mix" && !preMix) stageMatches = true;
                        else if (part != "pre-mix" && part != "post-mix" && part != "capture") Note(where, "unknown stage \"" + part + "\"");
                    }
                }
                if (!stageMatches) return;
                if (key == "Stage") return;

                if (key == "Channel") { Channel(value); return; }
                if (key.StartsWith("Filter", StringComparison.Ordinal)) { Filter(value, where); return; }
                if (key == "Preamp") { Preamp(value); return; }
                if (key == "Delay") { Delay(value); return; }
                if (key == "Copy") { Copy(value, where); return; }
                if (key == "Convolution") { Convolution(Resolve(path, value.TrimStart()), value.Trim(), where); return; }
                if (key == "GraphicEQ") { Graphic(value); return; }
                if (key == "VSTPlugin") { NotDrawn(where, "VST plug-ins run in Equalizer APO but can't be drawn here"); return; }
                if (key == "LoudnessCorrection") { NotDrawn(where, "loudness correction follows the Windows volume, so it isn't drawn"); return; }
            }

            static string Resolve(string configPath, string value)
            {
                if (value.Length == 0) return value;
                if (Path.DirectorySeparatorChar == '/') value = value.Replace('\\', '/');   // Windows paths when testing elsewhere
                bool rooted = value.StartsWith("\\") || value.StartsWith("/") || (value.Length > 1 && value[1] == ':');
                return rooted ? value : Path.Combine(Path.GetDirectoryName(configPath) ?? "", value);
            }

            // ------------------------------------------------- If (IfFilterFactory)
            bool If(string key, string value, string where)
            {
                string e = value.Trim();
                if (key == "If")
                {
                    if (falseCount == 0)
                    {
                        bool t;
                        try { t = expr.Eval(e).Truth; }
                        catch (ExprException ex) { Note(where, "If: " + ex.Message); falseCount++; return true; }
                        if (t) trueCount++; else { falseCount++; executeElse = true; }
                    }
                    else falseCount++;
                }
                else if (key == "ElseIf")
                {
                    if (falseCount == 0)
                    {
                        if (trueCount == 0) Note(where, "ElseIf without If");
                        else { falseCount++; trueCount--; }
                    }
                    else if (falseCount == 1 && executeElse)
                    {
                        bool t = false;
                        try { t = expr.Eval(e).Truth; }
                        catch (ExprException ex) { Note(where, "ElseIf: " + ex.Message); }
                        if (t) { falseCount--; trueCount++; executeElse = false; }
                    }
                }
                else if (key == "Else")
                {
                    if (falseCount == 0)
                    {
                        if (trueCount == 0) Note(where, "Else without If");
                        else { falseCount++; trueCount--; }
                    }
                    else if (falseCount == 1 && executeElse) { falseCount--; trueCount++; executeElse = false; }
                }
                else if (key == "EndIf")
                {
                    if (falseCount == 0)
                    {
                        if (trueCount == 0) Note(where, "EndIf without If");
                        else trueCount--;
                    }
                    else falseCount--;
                    if (falseCount == 0) executeElse = false;
                }
                return falseCount > 0 || key == "If" || key == "ElseIf" || key == "Else" || key == "EndIf";
            }

            // ------------------------------------- inline `expressions` (ExpressionFilterFactory)
            string Inline(string p, string where)
            {
                if (p.IndexOf('`') < 0) return p;
                var output = new StringBuilder();
                var e = new StringBuilder();
                bool inExpr = false, lastBackslash = false;
                for (int i = 0; i < p.Length; i++)
                {
                    char c = p[i];
                    if (c == '`')
                    {
                        if (!inExpr)
                        {
                            if (lastBackslash) output.Append(c); else inExpr = true;
                        }
                        else if (lastBackslash) e.Append(c);
                        else
                        {
                            inExpr = false;
                            try { output.Append(expr.Eval(e.ToString()).Text); }
                            catch (ExprException ex) { Note(where, "`" + e + "`: " + ex.Message); }
                            e.Clear();
                        }
                        lastBackslash = false;
                    }
                    else if (c == '\\')
                    {
                        if (i >= p.Length - 1 || p[i + 1] != '`') { if (inExpr) e.Append(c); else output.Append(c); }
                        lastBackslash = true;
                    }
                    else
                    {
                        if (inExpr) e.Append(c); else output.Append(c);
                        lastBackslash = false;
                    }
                }
                return output.ToString();
            }

            // ------------------------------------------------------- Channel
            int Index(string word, List<string> names)
            {
                if (word.Length > 0 && char.IsDigit(word[0]))
                {
                    int i = (int)Strtol(word) - 1;
                    return i >= 0 && i < names.Count ? i : -1;
                }
                int k = names.IndexOf(word);
                if (k < 0)
                {
                    string alt = word == "SL" ? "RL" : word == "SR" ? "RR" : word == "RL" ? "SL" : word == "RR" ? "SR" : word == "SUB" ? "LFE" : null;
                    if (alt != null) k = names.IndexOf(alt);
                }
                return k;
            }

            void Channel(string value)
            {
                var sel = new bool[all.Count];
                foreach (var w in value.ToUpperInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (w == "ALL") { for (int i = 0; i < sel.Length; i++) sel[i] = true; continue; }
                    int k = Index(w, all);
                    if (k >= 0) sel[k] = true;
                }
                current = all.Where((n, i) => sel[i]).ToList();
            }

            void Apply(double[] hr, double[] hi) { foreach (var n in current) ch[n].Mul(hr, hi); }

            // -------------------------------------------------------- Preamp
            void Preamp(string value)
            {
                double db;
                if (!LeadingNumber(value.Replace(',', '.').TrimStart(), out db)) return;
                double g = Math.Pow(10, db / 20);
                foreach (var n in current) ch[n].Scale(g);
            }

            // --------------------------------------------------------- Delay
            void Delay(string value)
            {
                var v = value.Replace(',', '.').Trim();
                double d;
                int used;
                if (!LeadingNumber(v, out d, out used) || d < 0) return;
                string unit = v.Substring(used).Trim().Split(' ')[0].ToLowerInvariant();
                double samples;
                if (unit == "ms") samples = Math.Floor(o.SampleRate * d / 1000.0 + 0.5);
                else if (unit == "samples") samples = Math.Floor(d + 0.5);
                else return;
                var hr = new double[f.Length]; var hi = new double[f.Length];
                for (int i = 0; i < f.Length; i++) { double w = 2 * Math.PI * f[i] / o.SampleRate * samples; hr[i] = Math.Cos(w); hi[i] = -Math.Sin(w); }
                Apply(hr, hi);
            }

            // ---------------------------------------------------------- Copy
            void Copy(string value, string where)
            {
                var targets = new List<KeyValuePair<string, List<KeyValuePair<int, double>>>>();
                bool constants = false;
                foreach (var asg in value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = asg.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2) continue;
                    var sum = new List<KeyValuePair<int, double>>();
                    foreach (var summand in parts[1].Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var fac = summand.Split(new[] { '*' }, StringSplitOptions.RemoveEmptyEntries);
                        string factor = "", channel = "";
                        if (fac.Length == 2) { factor = fac[0]; channel = fac[1]; }
                        else if (fac.Length == 1) { if (fac[0] == "0" || fac[0].Contains('.')) factor = fac[0]; else channel = fac[0]; }
                        double g = 1;
                        if (factor != "")
                        {
                            LeadingNumber(factor, out g);
                            if (factor.Length > 2 && factor.EndsWith("db", StringComparison.OrdinalIgnoreCase)) g = Math.Pow(10, g / 20);
                        }
                        int src = channel == "" ? -1 : Index(channel, all);
                        if (src < 0) { constants = true; continue; }   // a constant: only adds DC
                        sum.Add(new KeyValuePair<int, double>(src, g));
                    }
                    if (parts[0] != "" && (sum.Count > 0 || parts[1].Length > 0))
                        targets.Add(new KeyValuePair<string, List<KeyValuePair<int, double>>>(parts[0], sum));
                }
                if (constants) Note(where, "constant values in Copy only add a DC offset, which isn't drawn");
                // all sources are read before any target is written
                var snapshot = all.Select(n => ch[n].Clone()).ToList();
                foreach (var t in targets)
                {
                    int k = Index(t.Key, all);
                    string name = k >= 0 ? all[k] : t.Key;
                    if (k < 0) { all.Add(name); ch[name] = new Chan(f.Length, -1); }
                    var acc = new Chan(f.Length, -1);
                    foreach (var s in t.Value)
                    {
                        var c = snapshot[s.Key];
                        for (int i = 0; i < f.Length; i++)
                        {
                            acc.Lr[i] += s.Value * c.Lr[i]; acc.Li[i] += s.Value * c.Li[i];
                            acc.Rr[i] += s.Value * c.Rr[i]; acc.Ri[i] += s.Value * c.Ri[i];
                        }
                    }
                    ch[name] = acc;
                }
            }

            // -------------------------------------------------------- Filter
            static readonly Regex ReType = new Regex(@"^\s*ON\s+([A-Za-z]+)");
            static readonly Regex ReFreq = new Regex("\\s+Fc\\s*([-+0-9.eE ]+)\\s*H\\s*z");
            static readonly Regex ReGain = new Regex(@"\s+Gain\s*([-+0-9.eE]+)\s*dB");
            static readonly Regex ReQ = new Regex(@"\s+Q\s*([-+0-9.eE]+)");
            static readonly Regex ReBW = new Regex(@"\s+BW\s+Oct\s*([-+0-9.eE]+)");
            static readonly Regex ReSlope = new Regex(@"^\s*([-+0-9.eE]+)\s*dB");
            static readonly Regex ReOrder = new Regex(@"\s*Order\s+([0-9]+)");
            static readonly Regex ReCoef = new Regex(@"\s+Coefficients((?: [-+0-9.eE]+)+)");

            enum BQ { LowPass, HighPass, BandPass, Notch, AllPass, Peaking, LowShelf, HighShelf }
            static readonly Dictionary<string, BQ> Types = new Dictionary<string, BQ>
            {
                { "PK", BQ.Peaking }, { "PEQ", BQ.Peaking }, { "Modal", BQ.Peaking }, { "LP", BQ.LowPass }, { "HP", BQ.HighPass },
                { "LPQ", BQ.LowPass }, { "HPQ", BQ.HighPass }, { "BP", BQ.BandPass }, { "LS", BQ.LowShelf }, { "HS", BQ.HighShelf },
                { "LSC", BQ.LowShelf }, { "HSC", BQ.HighShelf }, { "NO", BQ.Notch }, { "AP", BQ.AllPass },
            };

            void Filter(string parameters, string where)
            {
                var tm = ReType.Match(parameters);
                if (!tm.Success) return;   // OFF, or not a filter line
                string typeString = tm.Groups[1].Value;
                if (typeString == "IIR") { Iir(parameters, where); return; }

                parameters = parameters.Replace(',', '.');
                tm = ReType.Match(parameters);
                BQ type;
                if (!Types.TryGetValue(typeString, out type))
                {
                    if (typeString != "None") NotDrawn(where, "Equalizer APO doesn't know the filter type " + typeString + " and ignores it");
                    return;
                }
                string rest = parameters.Substring(tm.Index + tm.Length);
                double freq = 0, gain = 0, bw = 0;
                bool isBwOrS = false, corner = false;

                var m = ReFreq.Match(rest);
                if (!m.Success) { NotDrawn(where, "no frequency, so Equalizer APO ignores this filter"); return; }
                freq = GetFreq(m.Groups[1].Value);
                m = ReGain.Match(rest);
                if (m.Success)
                {
                    if (!(type == BQ.LowPass || type == BQ.HighPass || type == BQ.Notch || type == BQ.AllPass)) gain = Strtod(m.Groups[1].Value);
                }
                else if (type == BQ.Peaking || type == BQ.LowShelf || type == BQ.HighShelf)
                { NotDrawn(where, "no gain, so Equalizer APO ignores this filter"); return; }
                m = ReQ.Match(rest);
                if (m.Success) bw = Strtod(m.Groups[1].Value);
                m = ReBW.Match(rest);
                if (m.Success && !(type == BQ.LowShelf || type == BQ.HighShelf)) { bw = Strtod(m.Groups[1].Value); isBwOrS = true; }
                m = ReSlope.Match(rest);
                if (m.Success && (type == BQ.LowShelf || type == BQ.HighShelf)) { bw = Strtod(m.Groups[1].Value); isBwOrS = true; }

                if (bw == 0)
                {
                    if (type == BQ.Peaking || type == BQ.AllPass) { NotDrawn(where, "no Q or bandwidth, so Equalizer APO ignores this filter"); return; }
                    if (type == BQ.LowPass || type == BQ.HighPass || type == BQ.BandPass) bw = Math.Sqrt(0.5);
                    else if (type == BQ.LowShelf || type == BQ.HighShelf) { bw = 0.9; isBwOrS = true; }
                    else if (type == BQ.Notch) bw = 30.0;
                }
                else if (type == BQ.LowShelf || type == BQ.HighShelf)
                {
                    if (isBwOrS) bw /= 12.0;
                    if (typeString[typeString.Length - 1] != 'C') corner = true;
                }

                // BiQuadFilter::initialize: corner frequency -> centre frequency (as the DCX2496 does)
                double bqFreq = freq;
                if (corner)
                {
                    double s = bw;
                    if (!isBwOrS)
                    {
                        double a = Math.Pow(10, gain / 40);
                        s = 1.0 / ((1.0 / (bw * bw) - 2.0) / (a + 1.0 / a) + 1.0);
                    }
                    double factor = Math.Pow(10.0, Math.Abs(gain) / 80.0 / s);
                    if (type == BQ.LowShelf) bqFreq *= factor; else bqFreq /= factor;
                }
                var c = Biquad(type, gain, bqFreq, o.SampleRate, bw, isBwOrS);
                ApplyIir(new[] { c[0], c[1], c[2] }, new[] { c[3], c[4], c[5] });
            }

            /// BiQuad::BiQuad, coefficients b0 b1 b2 a0 a1 a2.
            static double[] Biquad(BQ type, double dbGain, double freq, double srate, double bw, bool isBwOrS)
            {
                double A = (type == BQ.Peaking || type == BQ.LowShelf || type == BQ.HighShelf) ? Math.Pow(10, dbGain / 40) : Math.Pow(10, dbGain / 20);
                double omega = 2 * Math.PI * freq / srate, sn = Math.Sin(omega), cs = Math.Cos(omega), alpha;
                if (!isBwOrS) alpha = sn / (2 * bw);
                else if (type == BQ.LowShelf || type == BQ.HighShelf) alpha = sn / 2 * Math.Sqrt((A + 1 / A) * (1 / bw - 1) + 2);
                else alpha = sn * Math.Sinh(Math.Log(2) / 2 * bw * omega / sn);
                double beta = 2 * Math.Sqrt(A) * alpha;
                switch (type)
                {
                    case BQ.LowPass: return new[] { (1 - cs) / 2, 1 - cs, (1 - cs) / 2, 1 + alpha, -2 * cs, 1 - alpha };
                    case BQ.HighPass: return new[] { (1 + cs) / 2, -(1 + cs), (1 + cs) / 2, 1 + alpha, -2 * cs, 1 - alpha };
                    case BQ.BandPass: return new[] { alpha, 0, -alpha, 1 + alpha, -2 * cs, 1 - alpha };
                    case BQ.Notch: return new[] { 1, -2 * cs, 1, 1 + alpha, -2 * cs, 1 - alpha };
                    case BQ.AllPass: return new[] { 1 - alpha, -2 * cs, 1 + alpha, 1 + alpha, -2 * cs, 1 - alpha };
                    case BQ.Peaking: return new[] { 1 + alpha * A, -2 * cs, 1 - alpha * A, 1 + alpha / A, -2 * cs, 1 - alpha / A };
                    case BQ.LowShelf:
                        return new[] { A * ((A + 1) - (A - 1) * cs + beta), 2 * A * ((A - 1) - (A + 1) * cs), A * ((A + 1) - (A - 1) * cs - beta),
                                       (A + 1) + (A - 1) * cs + beta, -2 * ((A - 1) + (A + 1) * cs), (A + 1) + (A - 1) * cs - beta };
                    default:
                        return new[] { A * ((A + 1) + (A - 1) * cs + beta), -2 * A * ((A - 1) + (A + 1) * cs), A * ((A + 1) + (A - 1) * cs - beta),
                                       (A + 1) - (A - 1) * cs + beta, 2 * ((A - 1) - (A + 1) * cs), (A + 1) - (A - 1) * cs - beta };
                }
            }

            void Iir(string parameters, string where)
            {
                var om = ReOrder.Match(parameters);
                if (!om.Success) { NotDrawn(where, "IIR filter without an order"); return; }
                int order = (int)Strtol(om.Groups[1].Value);
                if (order < 1) { NotDrawn(where, "IIR order must be at least 1"); return; }
                var cm = ReCoef.Match(parameters);
                if (!cm.Success) { NotDrawn(where, "IIR filter without coefficients"); return; }
                var parts = cm.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != (order + 1) * 2) { NotDrawn(where, "IIR order " + order + " needs " + (order + 1) * 2 + " coefficients, not " + parts.Length); return; }
                var c = parts.Select(Strtod).ToArray();
                ApplyIir(c.Take(order + 1).ToArray(), c.Skip(order + 1).ToArray());
            }

            /// H(e^jw) = B(z)/A(z) with z^-1 = e^-jw.
            void ApplyIir(double[] b, double[] a)
            {
                var hr = new double[f.Length]; var hi = new double[f.Length];
                for (int i = 0; i < f.Length; i++)
                {
                    double w = 2 * Math.PI * f[i] / o.SampleRate;
                    double br = 0, bi = 0, ar = 0, ai = 0;
                    for (int k = 0; k < b.Length; k++) { br += b[k] * Math.Cos(w * k); bi -= b[k] * Math.Sin(w * k); }
                    for (int k = 0; k < a.Length; k++) { ar += a[k] * Math.Cos(w * k); ai -= a[k] * Math.Sin(w * k); }
                    double d = ar * ar + ai * ai;
                    hr[i] = (br * ar + bi * ai) / d; hi[i] = (bi * ar - br * ai) / d;
                }
                Apply(hr, hi);
            }

            // ---------------------------------------------------- Convolution
            void Convolution(string file, string given, string where)
            {
                AudioFile af;
                try { af = Cache.Audio(file); }
                catch (FileNotFoundException) { NotDrawn(where, "the impulse response " + given + " wasn't found, so Equalizer APO skips it"); return; }
                catch (DirectoryNotFoundException) { NotDrawn(where, "the impulse response " + given + " wasn't found, so Equalizer APO skips it"); return; }
                catch (NotSupportedException) { NotDrawn(where, Path.GetFileName(file) + " plays in Equalizer APO, but this format can't be drawn here (WAV and FLAC can)"); return; }
                catch (Exception ex) { NotDrawn(where, Path.GetFileName(file) + ": " + ex.Message); return; }
                if (Math.Abs(af.SampleRate - o.SampleRate) > 1)
                {
                    NotDrawn(where, Path.GetFileName(file) + " is " + Khz(af.SampleRate) + " but the DAC runs at " + Khz(o.SampleRate) +
                                    ", so Equalizer APO skips it (it needs an impulse response at the same rate)");
                    return;
                }
                for (int i = 0; i < current.Count; i++)
                {
                    var h = Cache.Spectrum(file, af, i % af.Channels, f, o.SampleRate);
                    ch[current[i]].Mul(h[0], h[1]);
                }
            }

            // ------------------------------------------------------ GraphicEQ
            static readonly Regex ReNumber = new Regex("[-+0-9.eE]+");

            void Graphic(string parameters)
            {
                string v = parameters;
                if (v.IndexOf('.') < 0) v = v.Replace(',', '.');
                var nums = ReNumber.Matches(v).Cast<Match>().Select(m => Strtod(m.Value)).ToList();
                var nodes = new List<KeyValuePair<double, double>>();
                for (int i = 0; i + 1 < nums.Count; i += 2) nodes.Add(new KeyValuePair<double, double>(nums[i], nums[i + 1]));
                nodes = nodes.OrderBy(n => n.Key).ToList();
                var h = Cache.GraphicSpectrum(nodes, f, o.SampleRate);
                Apply(h[0], h[1]);
            }

            static string Khz(double r) { return (r / 1000).ToString("0.###", Inv) + " kHz"; }
        }

        // ---------------------------------------------------------- numbers --
        /// wcstod: the longest number at the start of s (0 if none).
        public static double Strtod(string s)
        {
            double v; LeadingNumber(s.TrimStart(), out v); return v;
        }

        static long Strtol(string s)
        {
            long v = 0; int i = 0;
            s = s.TrimStart();
            bool neg = i < s.Length && s[i] == '-';
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            for (; i < s.Length && char.IsDigit(s[i]); i++) v = v * 10 + (s[i] - '0');
            return neg ? -v : v;
        }

        static bool LeadingNumber(string s, out double v) { int u; return LeadingNumber(s, out v, out u); }

        static readonly Regex ReLead = new Regex(@"^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?");
        static bool LeadingNumber(string s, out double v, out int used)
        {
            var m = ReLead.Match(s);
            used = m.Success ? m.Length : 0;
            v = m.Success ? double.Parse(m.Value, NumberStyles.Float, Inv) : 0;
            return m.Success;
        }

        /// BiQuadFilterFactory::getFreq: "1.500" reads as 1500 (Room EQ Wizard's thousands separator).
        static double GetFreq(string freqString)
        {
            string s = freqString.Replace(" ", "");
            double r;
            if (!LeadingNumber(s, out r)) return -1;
            if (s.Length >= 5 && s.IndexOfAny(new[] { 'e', 'E' }) < 0 && s[s.Length - 4] == '.') r *= 1000.0;
            return r;
        }

        // ------------------------------------------------------------ cache --
        static class Cache
        {
            static readonly Dictionary<string, object> items = new Dictionary<string, object>();
            static readonly object gate = new object();

            static T Get<T>(string key, Func<T> make) where T : class
            {
                lock (gate)
                {
                    object v;
                    if (items.TryGetValue(key, out v)) return (T)v;
                }
                var made = make();
                lock (gate)
                {
                    if (items.Count > 64) items.Clear();
                    items[key] = made;
                }
                return made;
            }

            static string GridKey(double[] f, double sr) { return sr.ToString(Inv) + "|" + f.Length + "|" + f[0].ToString("R", Inv) + "|" + f[f.Length - 1].ToString("R", Inv); }

            public static AudioFile Audio(string path)
            {
                var info = new FileInfo(path);
                if (!info.Exists) throw new FileNotFoundException(path);
                return Get("audio|" + path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks, () => AudioFile.Read(path));
            }

            public static double[][] Spectrum(string path, AudioFile af, int channel, double[] f, double sr)
            {
                return Get("ir|" + path + "|" + af.GetHashCode() + "|" + channel + "|" + GridKey(f, sr), () => Dtft(af.Samples[channel], f, sr));
            }

            public static double[][] GraphicSpectrum(List<KeyValuePair<double, double>> nodes, double[] f, double sr)
            {
                string key = "geq|" + string.Join(";", nodes.Select(n => n.Key.ToString("R", Inv) + " " + n.Value.ToString("R", Inv))) + "|" + GridKey(f, sr);
                return Get(key, () => Dtft(GraphicEqFir(nodes, sr), f, sr));
            }
        }

        /// GraphicEQFilter::initialize: the 16384-tap minimum-phase FIR Equalizer APO builds.
        public static double[] GraphicEqFir(List<KeyValuePair<double, double>> nodes, double sampleRate, int filterLength = 16384)
        {
            int n2 = filterLength * 2;
            var tr = new double[n2]; var ti = new double[n2];
            var fr = new double[n2]; var fi = new double[n2];
            for (int i = 0; i < filterLength; i++)
            {
                double freq = i * 1.0 * sampleRate / n2;
                double g = Math.Pow(10.0, GainAt(nodes, freq) / 20.0);
                fr[i] = g; fi[i] = 0;
                fr[n2 - i - 1] = g; fi[n2 - i - 1] = 0;
            }
            // mps: minimum-phase spectrum
            double threshold = Math.Pow(10.0, -100.0 / 20.0), logThreshold = Math.Log(threshold);
            for (int i = 0; i < n2; i++) { fr[i] = fr[i] < threshold ? logThreshold : Math.Log(fr[i]); fi[i] = 0; }
            Array.Copy(fr, tr, n2); Array.Copy(fi, ti, n2);
            Fft.Transform(tr, ti, true);
            for (int i = 0; i < n2; i++) { tr[i] /= n2; ti[i] /= n2; }
            for (int i = 1; i < filterLength; i++)
            {
                tr[i] += tr[n2 - i];
                ti[i] -= ti[n2 - i];
                tr[n2 - i] = 0; ti[n2 - i] = 0;
            }
            ti[filterLength] *= -1;
            Array.Copy(tr, fr, n2); Array.Copy(ti, fi, n2);
            Fft.Transform(fr, fi, false);
            for (int i = 0; i < n2; i++)
            {
                double eR = Math.Exp(fr[i]), ph = fi[i];
                fr[i] = eR * Math.Cos(ph); fi[i] = eR * Math.Sin(ph);
            }
            // back to the time domain, normalise, half-Hann window
            Array.Copy(fr, tr, n2); Array.Copy(fi, ti, n2);
            Fft.Transform(tr, ti, true);
            var buf = new double[filterLength];
            for (int i = 0; i < filterLength; i++)
            {
                double factor = 0.5 * (1 + Math.Cos(2 * Math.PI * i * 1.0 / n2));
                buf[i] = tr[i] / n2 * factor;
            }
            return buf;
        }

        /// GainIterator::gainAt: linear in dB over log frequency, the end values held outside.
        public static double GainAt(List<KeyValuePair<double, double>> nodes, double freq)
        {
            if (nodes.Count == 0) return 0;
            int right = 0;
            while (right < nodes.Count && nodes[right].Key < freq) right++;   // lower_bound
            if (right == 0) return nodes[0].Value;
            if (right == nodes.Count) return nodes[nodes.Count - 1].Value;
            var l = nodes[right - 1]; var r = nodes[right];
            if (l.Value == r.Value) return l.Value;
            double t = (Math.Log(freq) - Math.Log(l.Key)) / (Math.Log(r.Key) - Math.Log(l.Key));
            return l.Value + t * (r.Value - l.Value);
        }

        /// Frequency response of a real FIR / impulse response at the given frequencies.
        public static double[][] Dtft(float[] h, double[] f, double sr)
        {
            var d = new double[h.Length];
            for (int i = 0; i < h.Length; i++) d[i] = h[i];
            return Dtft(d, f, sr);
        }

        public static double[][] Dtft(double[] h, double[] f, double sr)
        {
            var hr = new double[f.Length]; var hi = new double[f.Length];
            if ((long)h.Length * f.Length <= 1500000000L)   // exact up to ~25 s impulse responses at 1000 points
            {
                for (int i = 0; i < f.Length; i++)
                {
                    double w = 2 * Math.PI * f[i] / sr, cw = Math.Cos(w), sw = Math.Sin(w);
                    double cr = 1, ci = 0, sr2 = 0, si = 0;
                    for (int n = 0; n < h.Length; n++)
                    {
                        sr2 += h[n] * cr; si += h[n] * ci;
                        double t = cr * cw + ci * sw;      // multiply by e^-jw
                        ci = ci * cw - cr * sw;
                        cr = t;
                        if ((n & 4095) == 4095) { double m = Math.Sqrt(cr * cr + ci * ci); cr /= m; ci /= m; }
                    }
                    hr[i] = sr2; hi[i] = si;
                }
                return new[] { hr, hi };
            }
            // very long impulse responses: zero-padded FFT, interpolated between fine bins
            int n2 = Math.Max(Fft.NextPow2(h.Length) * 8, 1 << 18);
            var re = new double[n2]; var im = new double[n2];
            Array.Copy(h, re, h.Length);
            Fft.Transform(re, im, false);
            for (int i = 0; i < f.Length; i++)
            {
                double k = f[i] / sr * n2;
                int k0 = (int)Math.Floor(k);
                double t = k - k0;
                int k1 = Math.Min(k0 + 1, n2 / 2);
                hr[i] = re[k0] * (1 - t) + re[k1] * t;
                hi[i] = im[k0] * (1 - t) + im[k1] * t;
            }
            return new[] { hr, hi };
        }
    }
}

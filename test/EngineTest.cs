using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DacEqSwitcher;

// The app's Equalizer APO engine against:
//  1. an independent numpy port of Equalizer APO's filters (test/apo_ref.py, read from stdin):
//     every filter type and variant, IIR, GraphicEQ's FIR, Delay, Copy, Channel, Convolution
//     (WAV 16/24/float, FLAC, long IRs, wrong sample rate), compared at 400 frequencies;
//  2. control flow and expressions (If/ElseIf/Else, Eval, inline `…`, Include, Stage, Device),
//     each compared with the plain configuration it must reduce to.
static class ET
{
    static int fails;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string work;

    static void Check(bool ok, string what) { if (!ok) fails++; Console.WriteLine((ok ? "ok    " : "FAIL  ") + what); }

    static EqText.Response A(string text, double sr) { return EqText.Analyse(text, 400, 20, 20000, work, sr); }

    static double Diff(double[] a, double[] b)
    {
        double w = 0;
        for (int i = 0; i < a.Length; i++)
            if (a[i] > -100 || b[i] > -100) w = Math.Max(w, Math.Abs(a[i] - b[i]));
        return w;
    }

    static void Same(string what, string text, string plain, double sr = 48000)
    {
        var a = A(text, sr); var b = A(plain, sr);
        double d = Math.Max(Diff(a.L, b.L), Diff(a.R, b.R));
        Check(d < 1e-9, what + (d < 1e-9 ? "" : " (differs by " + d.ToString("0.000", Inv) + " dB; notes: " + string.Join(" | ", a.Notes) + ")"));
    }

    static void Expr(string e, string want, double sr = 48000)
    {
        string got;
        try { got = new ApoExpr(sr, 2, 2, "post-mix", "", "", "").Eval(e).Text; }
        catch (ExprException ex) { got = "error: " + ex.Message; }
        Check(got == want, "expression " + e + " = " + got + (got == want ? "" : " (want " + want + ")"));
    }

    static int Main(string[] args)
    {
        work = args[0];
        // 1. against the numpy reference
        var lines = Console.In.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        int cases = 0;
        for (int i = 0; i + 3 < lines.Count + 1; i += 4)
        {
            var head = lines[i].Split('\t');
            if (head[0] != "CASE") break;
            string name = head[1];
            double sr = double.Parse(head[2], Inv);
            string text = Encoding.UTF8.GetString(Convert.FromBase64String(head[3]));
            Func<int, double[]> arr = k => lines[i + k].Split('\t')[1].Split(' ').Select(x => double.Parse(x, Inv)).ToArray();
            var r = A(text, sr);
            double d = Math.Max(Math.Max(Diff(r.L, arr(1)), Diff(r.R, arr(2))), Diff(r.Worst, arr(3)));
            Check(d < 0.001, name + " @ " + sr / 1000 + " kHz: max difference " + d.ToString("0.000000", Inv) + " dB" + (r.Notes.Count > 0 ? "  [" + string.Join(" | ", r.Notes) + "]" : ""));
            cases++;
        }
        Check(cases >= 15, cases + " reference cases compared");

        // 2. control flow
        Same("If / ElseIf / Else on sampleRate (48 kHz)",
             "If: sampleRate == 44100\nPreamp: -1 dB\nElseIf: sampleRate == 48000\nPreamp: -2 dB\nElse:\nPreamp: -3 dB\nEndIf:", "Preamp: -2 dB");
        Same("If / ElseIf / Else on sampleRate (96 kHz)",
             "If: sampleRate == 44100\nPreamp: -1 dB\nElseIf: sampleRate == 48000\nPreamp: -2 dB\nElse:\nPreamp: -3 dB\nEndIf:", "Preamp: -3 dB", 96000);
        Same("nested If, false outer skips inner", "If: 1 > 2\nIf: true\nPreamp: -9 dB\nEndIf:\nElse:\nPreamp: -1 dB\nEndIf:", "Preamp: -1 dB");
        Same("Eval variables and inline expressions",
             "Eval: g = -4; f = 1000 * 2\nFilter: ON PK Fc `f` Hz Gain `g + 1` dB Q `sqrt(2)`", "Filter: ON PK Fc 2000 Hz Gain -3 dB Q 1.4142135623731");
        Same("escaped backtick is literal", "Preamp: -2 dB \\`ignored\\`", "Preamp: -2 dB");
        Same("indented commands", "    If: true\n      Preamp: -5 dB\n    EndIf:", "Preamp: -5 dB");
        Same("unknown and lowercase commands are ignored, as Equalizer APO does", "preamp: -6 dB\nFoo: 1\nPreamp: -1 dB", "Preamp: -1 dB");
        Same("Device lines are assumed to match", "Device: Speakers DX1\nPreamp: -2 dB\nDevice: all", "Preamp: -2 dB");
        Same("Stage: pre-mix and post-mix both reach the DAC; capture doesn't",
             "Stage: pre-mix\nPreamp: -1 dB\nStage: capture\nPreamp: -7 dB\nStage: post-mix\nPreamp: -2 dB", "Preamp: -3 dB");
        Same("Stage: post-mix is the default", "Preamp: -4 dB\nStage: capture\nPreamp: -9 dB", "Preamp: -4 dB");
        File.WriteAllText(Path.Combine(work, "inc_a.txt"), "Channel: R\nPreamp: -2 dB\nInclude: sub/inc_b.txt");
        Directory.CreateDirectory(Path.Combine(work, "sub"));
        File.WriteAllText(Path.Combine(work, "sub", "inc_b.txt"), "Filter: ON PK Fc 500 Hz Gain 3 dB Q 1\nConvolution: ../st48.wav");
        Same("Include: nested, relative to each file, channel selection restored afterwards",
             "Include: inc_a.txt\nPreamp: -1 dB", "Channel: R\nPreamp: -2 dB\nFilter: ON PK Fc 500 Hz Gain 3 dB Q 1\nConvolution: st48.wav\nChannel: all\nPreamp: -1 dB");
        Same("Include inside a false If is skipped", "If: false\nInclude: inc_a.txt\nEndIf:", "");
        Same("Convolution with a path from an expression", "Convolution: st48`sampleRate == 48000 ? \"\" : \"_x\"`.wav", "Convolution: st48.wav");
        var miss = A("Convolution: nothere.wav\nVSTPlugin: Library x.dll\nLoudnessCorrection: State 1", 48000);
        Check(miss.Skipped == 3 && miss.Notes.Count == 3, "missing IR, VST plug-in and loudness correction are reported as not drawn (" + string.Join(" | ", miss.Notes) + ")");
        var wrongRate = A("Convolution: mono44.wav", 48000);
        Check(wrongRate.Notes.Any(n => n.Contains("44.1 kHz") && n.Contains("48 kHz")), "an impulse response at another rate is flagged: " + string.Join(" | ", wrongRate.Notes));

        // expressions (muParserX + Equalizer APO's additions)
        Expr("1 + 2 * 3", "7");
        Expr("2 ^ 3 ^ 2", "512");
        Expr("-2 ^ 2", "-4");
        Expr("(1 + 2) * 3 / 2", "4.5");
        Expr("sampleRate / 1000", "48");
        Expr("\"ir_\" + sampleRate + \".wav\"", "ir_48000.wav");
        Expr("1 < 2 and not (3 == 4)", "true");
        Expr("1 > 2 or 2 > 1", "true");
        Expr("1 > 2 ? \"a\" : \"b\"", "b");
        Expr("x = 3, x += 2, x * 2", "10");
        Expr("(int)7.9 + (float)1", "8");
        Expr("{1, 2, 3}[1] + sizeof({4, 5})", "4");
        Expr("max(1, 5, 3) + min(2, 0) + sum(1, 2)", "8");
        Expr("toupper(\"dx1\") + strlen(\"abc\")", "DX13");
        Expr("regexReplace(\"\\\\s*\\\\(.*\\\\)\", \"Speakers (DX1 II)\", \"\")", "Speakers");
        Expr("regexSearch(\"(\\\\d+)\", \"rate 44100\")[1]", "44100");
        Expr("str2dbl(\"2.5\") * 2", "5");
        Expr("5k + 2m", "5000.002");
        Expr("0x10 | 1", "17");
        Expr("1 << 4", "16");
        Expr("abs(-3) + sqrt(16) + log10(1000)", "10");
        Expr("1 / 3", "0.333333333333333");
        Expr("\"a\" == \"a\"", "true");
        Expr("undefinedVar + 1", "1");

        Console.WriteLine(fails == 0 ? "engine: all checks passed" : "engine: " + fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }
}

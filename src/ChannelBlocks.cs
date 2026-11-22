// DAC EQ Switcher - Copyright 2026 ZN98 - GPL-3.0-or-later (see LICENSE and NOTICE).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DacEqSwitcher
{
    /// An EQ as three blocks: both channels, left only, right only. Stored as ordinary Equalizer
    /// APO text (the left/right blocks under "Channel: L" / "Channel: R"). Splitting only moves
    /// per-channel linear filters (Preamp, Filter, GraphicEQ, Delay, Convolution), whose order
    /// doesn't change the sound; anything that mixes channels or branches keeps the text whole.
    static class ChannelBlocks
    {
        public const int All = 0, Left = 1, Right = 2;
        public static readonly string[] Names = { "Both channels", "Left", "Right" };

        static readonly HashSet<string> KeepWhole = new HashSet<string>
            { "Copy", "If", "ElseIf", "Else", "EndIf", "Eval", "Include", "Stage", "Device", "VSTPlugin", "LoudnessCorrection" };

        public sealed class Split
        {
            public string[] Blocks = { "", "", "" };
            public bool Separated;   // false: the whole text is in the "both channels" block
            public string Reason;
        }

        public static Split Separate(string text)
        {
            var lines = new List<string>[] { new List<string>(), new List<string>(), new List<string>() };
            int cur = All;
            foreach (var raw in EqText.SplitLines(text ?? ""))
            {
                string cmd = raw.TrimStart().StartsWith("#") ? null : EqText.CommandOf(raw);
                if (cmd != null && KeepWhole.Contains(cmd))
                    return Whole(text, "it uses " + cmd + ", which depends on order or mixes channels");
                if (cmd == "Channel")
                {
                    var words = new HashSet<string>(raw.Substring(raw.IndexOf(':') + 1).ToUpperInvariant()
                                    .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
                    if (words.Contains("ALL") || words.SetEquals(new[] { "L", "R" }) || words.SetEquals(new[] { "1", "2" })) cur = All;
                    else if (words.SetEquals(new[] { "L" }) || words.SetEquals(new[] { "1" })) cur = Left;
                    else if (words.SetEquals(new[] { "R" }) || words.SetEquals(new[] { "2" })) cur = Right;
                    else return Whole(text, "it selects channels other than left and right");
                    continue;
                }
                lines[cur].Add(raw);
            }
            var s = new Split { Separated = true };
            for (int i = 0; i < 3; i++) s.Blocks[i] = Trim(lines[i]);
            return s;
        }

        static Split Whole(string text, string reason)
        {
            var s = new Split { Separated = false, Reason = reason };
            s.Blocks[All] = Trim(EqText.SplitLines(text ?? "").ToList());
            return s;
        }

        static string Trim(List<string> l)
        {
            int a = 0, b = l.Count;
            while (a < b && l[a].Trim().Length == 0) a++;
            while (b > a && l[b - 1].Trim().Length == 0) b--;
            return string.Join("\r\n", l.Skip(a).Take(b - a));
        }

        /// The Equalizer APO text for the three blocks. map[i] = (block, line in block) for line i+1
        /// of the result, block -1 for lines added here.
        public static string Compose(string[] blocks, out List<KeyValuePair<int, int>> map)
        {
            var sb = new StringBuilder();
            map = new List<KeyValuePair<int, int>>();
            var m = map;
            Action<string, int, int> add = (line, block, n) => { sb.Append(line).Append("\r\n"); m.Add(new KeyValuePair<int, int>(block, n)); };
            bool any = false;
            for (int b = 0; b < 3; b++)
            {
                string text = (blocks[b] ?? "").Trim('\r', '\n');
                if (text.Trim().Length == 0) continue;
                if (any) add("", -1, 0);
                if (b == Left) add("Channel: L", -1, 0);
                if (b == Right) add("Channel: R", -1, 0);
                int n = 0;
                foreach (var line in EqText.SplitLines(text)) add(line, b, ++n);
                any = true;
            }
            if (!string.IsNullOrWhiteSpace(blocks[Left]) || !string.IsNullOrWhiteSpace(blocks[Right]))
            {
                add("", -1, 0);
                add("Channel: all", -1, 0);
            }
            return sb.ToString();
        }

        public static string Compose(string[] blocks) { List<KeyValuePair<int, int>> map; return Compose(blocks, out map); }

        /// "Line 12: …" from the analysis of composed text -> "Left, line 3: …".
        public static string Where(List<KeyValuePair<int, int>> map, int composedLine)
        {
            if (composedLine < 1 || composedLine > map.Count || map[composedLine - 1].Key < 0) return "Line " + composedLine;
            var e = map[composedLine - 1];
            return Names[e.Key] + ", line " + e.Value;
        }
    }
}

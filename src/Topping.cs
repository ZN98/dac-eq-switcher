using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DacEqSwitcher
{
    enum Protocol { None, Dx1, Dx5 }

    sealed class ModelInfo
    {
        public readonly string Key, Name;
        public readonly int Vid, Pid;
        public readonly string[] ProductMatch;
        public readonly Protocol Protocol;

        public ModelInfo(string key, string name, int vid, int pid, string[] match, Protocol protocol)
        {
            Key = key; Name = name; Vid = vid; Pid = pid; ProductMatch = match; Protocol = protocol;
        }

        /// toppingctl's comparison: upper-case, Roman numeral II, alphanumerics only.
        public static string Normalise(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").ToUpperInvariant().Replace("Ⅱ", "II"))
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        public bool Matches(string product)
        {
            string n = Normalise(product);
            return ProductMatch.Any(w => n.StartsWith(w, StringComparison.Ordinal) || n.Contains(w));
        }

        public static ModelInfo Find(int vid, int pid, string product)
        {
            return ToppingTables.Models.FirstOrDefault(m => m.Vid == vid && m.Pid == pid && m.Matches(product));
        }
    }

    /// A DAC setting that can trigger an EQ.
    sealed class FieldDef
    {
        public string Id, Name;
        public bool PushOnly;   // can't be queried safely; known once the DAC reports a change
        public bool Common;     // shown on the overview even when no rule uses it
        public int[] Values = new int[0];
        public Func<int, string> Formatter;

        public string Format(int v)
        {
            string s = Formatter != null ? Formatter(v) : null;
            return s ?? ("Value " + v);
        }
    }

    static class Frames
    {
        public const byte ReadNack = 0x10, WriteNack = 0x20;

        /// 22 33 <op> 01 <b4> <reg> <sub> <int32 BE> 00 00 66 77 00 (toppingctl frame())
        public static byte[] Request(int cmd)
        {
            return new byte[] { 0x22, 0x33, ReadNack, 0x01, 0x01, (byte)(cmd >> 8), (byte)cmd, 0, 0, 0, 0, 0, 0, 0x66, 0x77, 0x00 };
        }

        /// A write, exactly as toppingctl's frame(reg, sub, value, b4, crc).
        public static byte[] Write(int reg, int sub, long value, byte b4 = 0x01, bool crc = false)
        {
            uint v = unchecked((uint)value);
            var f = new byte[] { 0x22, 0x33, WriteNack, 0x01, b4, (byte)reg, (byte)sub,
                (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v, 0, 0, 0x66, 0x77, 0x00 };
            if (crc)
            {
                ushort c = Crc16Modbus(f, 2, 9);
                f[11] = (byte)(c >> 8); f[12] = (byte)c;   // high byte first, as toppingctl
            }
            return f;
        }

        public static byte[] Write(int cmd, long value) { return Write(cmd >> 8, cmd & 0xFF, value); }

        /// One frame of the DX1 II 0x810A output-state block (toppingctl dx1_block_frame()).
        public static byte[] Dx1Block(int cur, long value)
        {
            uint v = unchecked((uint)value);
            int c = ToppingTables.Dx1OutState;
            return new byte[] { 0x22, 0x33, WriteNack, (byte)ToppingTables.Dx1BlockLen, (byte)cur, (byte)(c >> 8), (byte)c,
                (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v, 0, 0, 0x66, 0x77, 0x00 };
        }

        public static byte[] Commit() { return Write(ToppingTables.RegCtrl, ToppingTables.SubCommit, 1); }

        /// Reflected CRC-16, polynomial 0xA001, init 0xFFFF (toppingctl crc16_modbus()).
        public static ushort Crc16Modbus(byte[] d, int off, int len)
        {
            int n = 0xFFFF;
            for (int i = off; i < off + len; i++)
            {
                n ^= d[i];
                for (int b = 0; b < 8; b++) n = (n & 1) != 0 ? (n >> 1) ^ 0xA001 : n >> 1;
            }
            return (ushort)n;
        }

        /// Finds a protocol frame in an input report (with or without the report-id byte).
        public static bool TryParse(byte[] r, int len, out int cmd, out int idx, out int frameLen, out uint value)
        {
            cmd = idx = frameLen = 0; value = 0;
            int o = -1;
            if (len >= 15 && r[0] == 0x22 && r[1] == 0x33) o = 0;
            else if (len >= 16 && r[1] == 0x22 && r[2] == 0x33) o = 1;
            if (o < 0) return false;
            frameLen = r[o + 3];
            idx = r[o + 4];
            cmd = (r[o + 5] << 8) | r[o + 6];
            value = (uint)((r[o + 7] << 24) | (r[o + 8] << 16) | (r[o + 9] << 8) | r[o + 10]);
            return true;
        }
    }

    /// A batch of frames sent to the DAC in one go (null = short pause).
    sealed class WriteJob
    {
        public readonly List<byte[]> Frames = new List<byte[]>();
        public string Label;
        public bool Refresh;              // re-read slower-changing state afterwards
        public Action<string> Done;       // null = success, else the error (called on the DAC thread)

        public WriteJob(string label) { Label = label; }
        public WriteJob Add(byte[] f) { Frames.Add(f); return this; }
        public WriteJob AddRange(IEnumerable<byte[]> fs) { Frames.AddRange(fs); return this; }
        public WriteJob Pause() { Frames.Add(null); return this; }
    }

    /// Knows which read-safe queries to send and how to decode replies for one protocol.
    abstract class ProtocolHandler
    {
        /// Settings that can trigger an EQ.
        public abstract IList<FieldDef> Fields { get; }
        /// Requests to send on poll number `tick` (one tick = Monitor.TickMs).
        public abstract IEnumerable<int> Poll(int tick);
        /// Decodes one frame; calls set(fieldId, value) for each field it carries.
        public abstract void Parse(int cmd, int idx, int frameLen, uint value, Action<string, int> set);
        /// Ask for slower-changing state again (after a write).
        public virtual void RequestRefresh() { }

        public static ProtocolHandler For(Protocol p)
        {
            if (p == Protocol.Dx1) return new Dx1Protocol();
            if (p == Protocol.Dx5) return new Dx5Protocol();
            return null;
        }

        protected static int[] Range(int from, int to)
        {
            return Enumerable.Range(from, to - from + 1).ToArray();
        }
    }

    // ------------------------------------------------------------------ DX1 II --
    // Only the vendor's read-safe query list is ever sent (0x810A, 0x7100, 0x1204,
    // 0x1206, 0x1106). Input, gain and filter are write-only registers: querying them
    // would overwrite them (see toppingctl devstate.dx1_query), so they're learned
    // from the DAC's own pushes when they change.
    sealed class Dx1Protocol : ProtocolHandler
    {
        public const string Output = "dx1.output", OpticalOut = "dx1.opticalOut", Input = "dx1.input",
            Gain = "dx1.gain", Filter = "dx1.filter", DacEq = "dx1.dacEq", Power = "dx1.power", Mute = "dx1.mute",
            MuteBits = "dx1.muteBits", VolHp = "dx1.volHp", VolLo = "dx1.volLo", VolAll = "dx1.volAll",
            VolLinked = "dx1.volLinked", Slots = "dx1.slots";

        static readonly IList<FieldDef> fields = new List<FieldDef>
        {
            new FieldDef { Id = Output, Name = "Output", Common = true, Values = new[] { 2, 1, 3 },
                Formatter = v => v == 1 ? "Line out" : v == 2 ? "Headphones" : v == 3 ? "Headphones + line out" : v == 0 ? "None" : null },
            new FieldDef { Id = Input, Name = "Input", Common = true, PushOnly = true, Values = new[] { 0, 1 },
                Formatter = v => v == 0 ? "USB" : v == 1 ? "Optical" : null },
            new FieldDef { Id = Gain, Name = "Headphone gain", Common = true, PushOnly = true, Values = new[] { 0, 1 },
                Formatter = v => v == 0 ? "Low" : v == 1 ? "High" : null },
            new FieldDef { Id = Filter, Name = "PCM filter", PushOnly = true, Values = Range(0, 7),
                Formatter = v => v >= 0 && v <= 7 ? "Filter " + (v + 1) : null },
            new FieldDef { Id = DacEq, Name = "DAC's own EQ", Common = true, Values = new[] { -1, 0, 1, 2 },
                Formatter = v => v == -1 ? "Off" : v >= 0 && v <= 2 ? "EQ " + (v + 1) : null },
            new FieldDef { Id = OpticalOut, Name = "Optical output", Values = new[] { 0, 1 },
                Formatter = v => v == 0 ? "Off" : v == 1 ? "On" : null },
            new FieldDef { Id = Power, Name = "Power", Values = new[] { 1, 2 },
                Formatter = v => v == 1 ? "On" : v == 2 ? "Standby" : null },
            new FieldDef { Id = Mute, Name = "Mute", Values = new[] { 0, 1 },
                Formatter = v => v == 0 ? "Off" : v == 1 ? "Muted" : null },
        };

        public sealed class Slot
        {
            public string Name;
            public double PreampDb;
            public int ActiveBands;
        }

        int eqOn = -1, eqSlot = -1, slotsVersion;
        volatile bool wantDump = true;
        readonly Dictionary<int, uint> dumpFrame = new Dictionary<int, uint>();
        readonly List<Slot> dumped = new List<Slot>();
        Slot[] slots = new Slot[0];

        public override IList<FieldDef> Fields { get { return fields; } }
        public Slot[] StoredSlots { get { return slots; } }

        public override void RequestRefresh() { wantDump = true; }

        public override IEnumerable<int> Poll(int tick)
        {
            yield return ToppingTables.Dx1OutState;          // output, volume, mute: every tick
            if (tick % 8 == 0)                               // slower-changing state
            {
                yield return ToppingTables.Dx1RegQueryEnable;
                yield return ToppingTables.Dx1RegQueryCfg;
                yield return ToppingTables.Dx1RegState;
            }
            if (wantDump)                                    // stored EQ slot names (3 x 78 frames)
            {
                wantDump = false;
                lock (dumpFrame) { dumpFrame.Clear(); dumped.Clear(); }
                yield return 0x1106;
            }
        }

        public override void Parse(int cmd, int idx, int frameLen, uint v, Action<string, int> set)
        {
            if (cmd == ToppingTables.Dx1OutState)
            {
                if (idx == 2) { set(Output, (int)(v & 3)); set(OpticalOut, (int)((v >> 2) & 1)); set(VolLinked, (int)((v >> 4) & 1)); }
                else if (idx == ToppingTables.Dx1FieldHpVol) set(VolHp, (int)v);
                else if (idx == ToppingTables.Dx1FieldLoVol) set(VolLo, (int)v);
                else if (idx == ToppingTables.Dx1FieldLoHpVol) set(VolAll, (int)v);
                else if (idx == ToppingTables.Dx1FieldMute) { set(Mute, (int)(v & 1)); set(MuteBits, (int)(v & 3)); }
            }
            else if (cmd == ToppingTables.Dx1RegState) set(Power, (int)v);
            else if (cmd == ToppingTables.Dx1RegInput) set(Input, (int)v);
            else if (cmd == ToppingTables.Dx1RegHighGain) set(Gain, (int)v);
            else if (cmd == ToppingTables.Dx1RegFilter) set(Filter, (int)v);
            else if (cmd == ToppingTables.Dx1RegQueryEnable && idx == 1) { eqOn = (v & 2) != 0 ? 1 : 0; EmitEq(set); }
            else if (cmd == ToppingTables.Dx1RegQueryCfg && idx == 1) { eqSlot = v <= 2 ? (int)v : -1; EmitEq(set); }
            else if (cmd == 0x1106 && frameLen >= 70) ParseDump(idx, v, set);
        }

        void EmitEq(Action<string, int> set)
        {
            if (eqOn == 0) set(DacEq, -1);
            else if (eqOn == 1 && eqSlot >= 0) set(DacEq, eqSlot);
        }

        // devstate.dx1_read_configs(): frames arrive in order, slot by slot; curFrame resets per slot
        void ParseDump(int frame, uint v, Action<string, int> set)
        {
            lock (dumpFrame)
            {
                if (frame == 0 && dumpFrame.Count > 0) dumpFrame.Clear();
                if (!dumpFrame.ContainsKey(frame)) dumpFrame[frame] = v;
                if (dumpFrame.Count < 78) return;
                var w = Enumerable.Range(0, 78).Select(i => dumpFrame.ContainsKey(i) ? dumpFrame[i] : 0u).ToArray();
                dumpFrame.Clear();
                dumped.Add(Decode(w));
                if (dumped.Count < 3) return;
                slots = dumped.ToArray();
                dumped.Clear();
            }
            set(Slots, ++slotsVersion);
        }

        /// readsettings.dx1_show(): name in words 0-3, preamp Q25 in word 5, band words from 8.
        static Slot Decode(uint[] w)
        {
            var nb = new List<byte>();
            for (int i = 0; i < 4; i++) { nb.Add((byte)w[i]); nb.Add((byte)(w[i] >> 8)); nb.Add((byte)(w[i] >> 16)); nb.Add((byte)(w[i] >> 24)); }
            string name = Encoding.ASCII.GetString(nb.Take(15).TakeWhile(b => b != 0).ToArray()).Trim();
            int active = 0;
            for (int j = 0; j < 11; j++) if ((w[8 + 3 * j] & 0xFF) == 1) active++;
            return new Slot
            {
                Name = name.Length > 0 ? name : "Config",
                PreampDb = w[5] == 0 ? -99 : 20 * Math.Log10(w[5] / (double)ToppingTables.PreampScale),
                ActiveBands = active,
            };
        }

        // ------------------------------------------------------------- writes --
        // Each mirrors a toppingctl command (cmd_vol_dx1, cmd_mute, cmd_input, cmd_filter,
        // cmd_gain, cmd_power, cmd_eq).
        public static int VolumeRaw(double db)
        {
            // toppingctl dx1_db_to_raw(): 1 dB steps below -10 dB, 0.5 dB above
            double v = Math.Max(-99.0, Math.Min(0.0, db));
            int r = Math.Max(0, Math.Min(ToppingTables.Dx1VolRawMax, (int)Math.Round((v + 99.0) * 10, MidpointRounding.ToEven)));
            if (r <= 890) r = 10 * (int)Math.Round(r / 10.0, MidpointRounding.ToEven);
            else if (r < 895) r = 890;
            else r = 895 + 5 * (int)Math.Round((r - 895) / 5.0, MidpointRounding.ToEven);
            return r;
        }

        public static double VolumeDb(int raw) { return Math.Max(0, Math.Min(ToppingTables.Dx1VolRawMax, raw)) / 10.0 - 99; }

        public static WriteJob SetVolume(int field, double db) { return new WriteJob("volume").Add(Frames.Dx1Block(field, VolumeRaw(db))); }
        public static WriteJob SetMute(int bits) { return new WriteJob("mute").Add(Frames.Dx1Block(ToppingTables.Dx1FieldMute, bits)); }
        public static WriteJob SetInput(int v) { return new WriteJob("input").Add(Frames.Write(ToppingTables.Dx1RegInput, v)); }
        public static WriteJob SetFilter(int v) { return new WriteJob("filter").Add(Frames.Write(ToppingTables.Dx1RegFilter, v)); }
        public static WriteJob SetGain(int v) { return new WriteJob("gain").Add(Frames.Write(ToppingTables.Dx1RegHighGain, v)); }
        public static WriteJob SetPower(bool on) { return new WriteJob("power") { Refresh = true }.Add(Frames.Write(ToppingTables.Dx1RegState, on ? 1 : 2)); }
        /// slot 0-2, or -1 = the DAC's EQ off (0xffffffff)
        public static WriteJob SelectEq(int slot) { return new WriteJob("eq") { Refresh = true }.Add(Frames.Write(ToppingTables.Dx1RegSwitchCfg, slot < 0 ? 0xFFFFFFFFL : slot)); }

    }

    // ------------------------------------------------------------------ DX5 II --
    // GetSettings (0x710C) returns every setting as numbered records.
    sealed class Dx5Protocol : ProtocolHandler
    {
        public const int GetSettings = 0x710C;

        // Shown on the overview without a rule using them
        static readonly HashSet<string> common = new HashSet<string> { "outputType", "inputType", "highGain", "pcmFilter", "sampleRate" };
        // Not useful as EQ triggers (or not a setting)
        static readonly HashSet<string> notTriggers = new HashSet<string> { "volume", "balance", "language", "theme", "brightness",
            "homePage", "classicVuLevel", "vuBarMode", "autoScreenTimeout", "dimScreenType", "remoteAKeyFunction",
            "remoteBKeyFunction", "multifunctionKey", "multifunctionDoubleKey", "volumeStep", "inputOptionMask",
            "outputOptionMask", "crossfeedConvolutionOptionMask", "crossfeedSimpleOptionMask" };

        static readonly Dictionary<string, string> names = new Dictionary<string, string>
        {
            { "powered", "Power" }, { "muted", "Mute" }, { "inputType", "Input" }, { "outputType", "Output" },
            { "highGain", "Headphone gain" }, { "powerTrigger", "Power trigger" }, { "pcmFilter", "PCM filter" },
            { "lineMode", "Line out mode" }, { "bluetoothMode", "Bluetooth" }, { "bluetoothAptx", "Bluetooth aptX" },
            { "remoteEnabled", "Remote" }, { "uacMode", "USB audio mode" }, { "sampleRate", "Sample rate" },
            { "inputMode", "Input selection" }, { "spdifMode", "S/PDIF mode" }, { "polarity", "Polarity" },
            { "volumeMemory", "Volume memory" }, { "peqMemory", "PEQ memory" }, { "crossfeedMemory", "Crossfeed memory" },
            { "dcDetectSensitivity", "DC detect sensitivity" }, { "crossfeedType", "Crossfeed" }, { "triggerOut", "Trigger out" },
            { "balance", "Balance" }, { "language", "Language" }, { "theme", "Theme" }, { "brightness", "Screen brightness" },
            { "homePage", "Home screen" }, { "classicVuLevel", "VU meter level" }, { "vuBarMode", "VU bar mode" },
            { "autoScreenTimeout", "Screen dims after" }, { "dimScreenType", "Dimmed screen shows" },
            { "remoteAKeyFunction", "Remote A key" }, { "remoteBKeyFunction", "Remote B key" },
            { "multifunctionKey", "Multifunction key" }, { "multifunctionDoubleKey", "Multifunction double-press" },
            { "volumeStep", "Volume step" }, { "volume", "Volume" },
        };

        static readonly Dictionary<string, string> valueNames = new Dictionary<string, string>
        {
            { "usb", "USB" }, { "fiber", "Optical" }, { "coax", "Coaxial" }, { "bt", "Bluetooth" },
            { "all", "All outputs" }, { "hp_all", "Headphones (both jacks)" }, { "line_all", "Line out (both)" },
            { "hp_single", "Headphones (single-ended)" }, { "hp_balanced", "Headphones (balanced)" },
            { "line_single", "Line out (RCA)" }, { "line_balanced", "Line out (balanced)" },
            { "uac1", "UAC 1.0" }, { "uac2", "UAC 2.0" }, { "dac", "DAC (fixed)" }, { "preamp", "Preamp" },
            { "trigger12v", "12 V trigger" }, { "input_only", "Input only" }, { "half_db", "0.5 dB" }, { "one_db", "1 dB" },
            { "plus4dbu", "+4 dBu" }, { "plus10dbu", "+10 dBu" }, { "en", "English" }, { "zh", "Chinese (Simplified)" },
            { "ja", "Japanese" }, { "zh_tw", "Chinese (Traditional)" }, { "vu", "VU meter" }, { "fft", "Spectrum" },
        };

        // Declared after the tables above: static initialisers run in textual order
        public static readonly Dictionary<string, FieldDef> All = BuildAll();
        static readonly Dictionary<int, string> byIndex = ToppingTables.Dx5Fields.Where(kv => kv.Key < 1 || kv.Key > 8).ToDictionary(kv => kv.Key, kv => kv.Value);
        static readonly IList<FieldDef> fields = All.Values.Where(f => !notTriggers.Contains(f.Id.Substring(4)))
                                                     .OrderByDescending(f => f.Common).ToList();

        static Dictionary<string, FieldDef> BuildAll()
        {
            var d = new Dictionary<string, FieldDef>();
            foreach (var kv in ToppingTables.Dx5Fields)
            {
                string name = kv.Value;
                if (kv.Key >= 1 && kv.Key <= 8 || name.EndsWith("Mask")) continue;
                var f = new FieldDef { Id = "dx5." + name, Common = common.Contains(name) };
                string display;
                f.Name = names.TryGetValue(name, out display) ? display : name;
                string en;
                if (ToppingTables.FieldEnum.TryGetValue(name, out en))
                {
                    var table = ToppingTables.Enums[en];
                    f.Values = table.Keys.ToArray();
                    f.Formatter = v => { string s; return table.TryGetValue(v, out s) ? Pretty(s) : null; };
                }
                else if (name == "sampleRate")
                {
                    f.Values = ToppingTables.SampleRates.Keys.ToArray();
                    f.Formatter = v =>
                    {
                        int hz;
                        if (!ToppingTables.SampleRates.TryGetValue(v, out hz)) hz = v >= 8000 ? v : 0;
                        if (hz == 0) return "No signal";
                        if (hz > 768000) return "DSD" + (hz % 44100 == 0 ? hz / 44100 : hz / 48000);
                        return (hz / 1000.0).ToString("0.###") + " kHz";
                    };
                }
                else if (name == "balance")
                {
                    // vendor_commands.decode_balance(): signed tenths of a dB, ±9.5 dB in 0.5 dB steps
                    f.Values = Enumerable.Range(-19, 39).Select(i => i * 5).ToArray();
                    f.Formatter = v => v == 0 ? "Centre" : (v < 0 ? "L " : "R ") + (Math.Abs(v) / 10.0).ToString("0.0") + " dB";
                }
                else if (name == "volume") { f.Formatter = v => v.ToString(); }
                else if (name == "highGain" || name == "dcDetectSensitivity") { f.Values = new[] { 0, 1 }; f.Formatter = v => v == 0 ? "Low" : v == 1 ? "High" : null; }
                else if (name == "powered") { f.Values = new[] { 1, 0 }; f.Formatter = v => v == 1 ? "On" : v == 0 ? "Standby" : null; }
                else if (name == "muted") { f.Values = new[] { 0, 1 }; f.Formatter = v => v == 0 ? "Off" : v == 1 ? "Muted" : null; }
                else { f.Values = new[] { 0, 1 }; f.Formatter = v => v == 0 ? "Off" : v == 1 ? "On" : null; }
                d[f.Id] = f;
            }
            return d;
        }

        static string Pretty(string s)
        {
            string p;
            if (valueNames.TryGetValue(s, out p)) return p;
            if (s.Length == 2 && s[0] == 'f' && char.IsDigit(s[1])) return "Filter " + s.Substring(1);
            if (s.Length == 2 && (s[0] == 'c' || s[0] == 's') && char.IsDigit(s[1])) return s.ToUpperInvariant();
            s = s.Replace('_', ' ');
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        public override IList<FieldDef> Fields { get { return fields; } }

        public override IEnumerable<int> Poll(int tick)
        {
            if (tick % 2 == 0) yield return GetSettings;
        }

        public override void Parse(int cmd, int idx, int frameLen, uint v, Action<string, int> set)
        {
            string name;
            if (cmd == GetSettings && byIndex.TryGetValue(idx, out name)) set("dx5." + name, unchecked((int)v));
        }

        // ------------------------------------------------------------- writes --
        // cmd_vol, cmd_gain, cmd_power, scenes.py recall, setctl.py. All committed.
        public static WriteJob SetVolume(int steps)
        {
            return new WriteJob("volume").Add(Frames.Write(ToppingTables.RegCtrl, ToppingTables.SubVolume, steps)).Add(Frames.Commit());
        }

        public static WriteJob SetGain(bool high)
        {
            return new WriteJob("gain").Add(Frames.Write(ToppingTables.RegCtrl, ToppingTables.SubGain, high ? 1 : 0)).Add(Frames.Commit());
        }

        public static WriteJob SetPower(bool on)
        {
            return new WriteJob("power").Add(Frames.Write(ToppingTables.RegCtrl, ToppingTables.SubPower, on ? 1 : 0, (byte)ToppingTables.SubPowerB4, true));
        }

        public static WriteJob Recall(int cmd) { return new WriteJob("scene").Add(Frames.Write(cmd, 1)).Add(Frames.Commit()); }

        public static WriteJob Set(int cmd, int raw) { return new WriteJob("setting").Add(Frames.Write(cmd, raw)).Add(Frames.Commit()); }
    }
}

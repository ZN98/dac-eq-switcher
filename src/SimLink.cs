using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DacEqSwitcher
{
    /// Simulated DAC for testing without hardware (DACEQ_SIM=dx1 or dx5).
    /// State changes made "on the DAC" come from DACEQ_SIM_STATE (lines like "output=2");
    /// every frame the app writes is logged to DACEQ_SIM_WRITES as hex.
    sealed class SimLink : IDacLink
    {
        readonly Queue<byte[]> queue = new Queue<byte[]>();
        readonly AutoResetEvent ready = new AutoResetEvent(false);
        readonly string kind;
        static readonly Dictionary<string, int> state = new Dictionary<string, int>();
        static string lastFile = "";
        readonly Dictionary<string, int> pushed = new Dictionary<string, int>();

        public static IDacLink Open(string kind, out ModelInfo model, out string product, out string unsupported)
        {
            unsupported = null;
            product = kind == "dx5" ? "TOPPING DX5 II" : "TOPPING DX1 II";
            model = ModelInfo.Find(ToppingTables.ThesyconVid, 0x8750, product);
            if (State().ContainsKey("absent")) { model = null; return null; }
            return new SimLink(kind);
        }

        SimLink(string kind) { this.kind = kind; }

        /// Merges the state file into the simulated DAC whenever the file changes.
        static Dictionary<string, int> State()
        {
            lock (state)
            {
                string p = Environment.GetEnvironmentVariable("DACEQ_SIM_STATE");
                string text = p != null && File.Exists(p) ? File.ReadAllText(p) : "";
                if (text != lastFile)
                {
                    lastFile = text;
                    foreach (var k in new[] { "absent", "unplugged" }) state.Remove(k);
                    foreach (var line in text.Split('\n'))
                    {
                        var kv = line.Split('=');
                        int v;
                        if (kv.Length == 2 && int.TryParse(kv[1], out v)) state[kv[0].Trim()] = v;
                    }
                }
                return new Dictionary<string, int>(state);
            }
        }

        static void SetState(string k, int v) { lock (state) state[k] = v; }
        static int Get(Dictionary<string, int> d, string k, int def) { int v; return d.TryGetValue(k, out v) ? v : def; }

        static byte[] Frame(int cmd, int idx, int v, int len = 1)
        {
            return new byte[] { 0, 0x22, 0x33, 0x20, (byte)len, (byte)idx, (byte)(cmd >> 8), (byte)cmd,
                (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v, 0, 0, 0x66, 0x77, 0 };
        }

        public int InputLength { get { return 65; } }

        void LogWrite(byte[] f)
        {
            string p = Environment.GetEnvironmentVariable("DACEQ_SIM_WRITES");
            if (p != null) File.AppendAllText(p, BitConverter.ToString(f, 0, 15).Replace("-", " ").ToLowerInvariant() + "\n");
        }

        public void Write(byte[] f)
        {
            int cmd = (f[5] << 8) | f[6];
            uint val = (uint)((f[7] << 24) | (f[8] << 16) | (f[9] << 8) | f[10]);
            var s = State();
            if (s.ContainsKey("unplugged")) throw new IOException("device removed");
            lock (queue)
            {
                if (f[2] == Frames.WriteNack) { LogWrite(f); ApplyWrite(cmd, f[3], f[4], val, f); }
                else if (kind == "dx1") Dx1Read(cmd, s);
                else Dx5Read(cmd, s);
                for (int i = 0; i < 20; i++) queue.Enqueue(new byte[65]);   // idle reports, like the real DAC
            }
            ready.Set();
        }

        void Dx1Read(int cmd, Dictionary<string, int> s)
        {
            if (cmd == 0x810A)
            {
                queue.Enqueue(Frame(cmd, 2, Get(s, "output", 2) | (Get(s, "optical", 0) << 2) | (Get(s, "linked", 0) << 4), 12));
                queue.Enqueue(Frame(cmd, 3, Get(s, "volHp", 885), 12));
                queue.Enqueue(Frame(cmd, 4, Get(s, "volLo", 780), 12));
                queue.Enqueue(Frame(cmd, 5, Get(s, "volAll", 585), 12));
                queue.Enqueue(Frame(cmd, 7, Get(s, "mute", 0), 12));
            }
            else if (cmd == 0x1204) queue.Enqueue(Frame(cmd, 1, Get(s, "dacEqOn", 0) == 1 ? 6 : 4));
            else if (cmd == 0x1206) queue.Enqueue(Frame(cmd, 1, Get(s, "dacEqSlot", 0)));
            else if (cmd == 0x7100) queue.Enqueue(Frame(cmd, 1, Get(s, "power", 1)));
            else if (cmd == 0x1106)
            {
                foreach (var name in new[] { "Headphones", "Speakers", "Dynamic" })
                {
                    var bytes = new byte[16];
                    System.Text.Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
                    for (int i = 0; i < 78; i++)
                    {
                        int w = i < 4 ? BitConverter.ToInt32(bytes, i * 4) : i == 5 || i == 7 ? 21000000 : (i >= 8 && (i - 8) % 3 == 0 && i < 8 + 3 * 6) ? 1 : 0;
                        queue.Enqueue(Frame(cmd, i, w, 78));
                    }
                }
            }
            else throw new InvalidOperationException("unsafe query 0x" + cmd.ToString("X4"));
            foreach (var reg in new[] { "input", "gain", "filter" })   // write-only registers arrive as pushes on change
            {
                int v;
                if (s.TryGetValue(reg, out v) && (!pushed.ContainsKey(reg) || pushed[reg] != v))
                {
                    pushed[reg] = v;
                    queue.Enqueue(Frame(reg == "input" ? 0x7B00 : reg == "gain" ? 0x7500 : 0x7300, 1, v));
                }
            }
        }


        void Dx5Read(int cmd, Dictionary<string, int> s)
        {
            if (cmd != Dx5Protocol.GetSettings) throw new InvalidOperationException("unsafe query");
            foreach (var kv in ToppingTables.Dx5Fields)
            {
                if (kv.Key >= 1 && kv.Key <= 8 || kv.Key >= 48) continue;
                int def = kv.Value == "powered" ? 1 : kv.Value == "volume" ? 60 : kv.Value == "sampleRate" ? 1 : kv.Value == "outputType" ? 1 : 0;
                queue.Enqueue(Frame(cmd, kv.Key, Get(s, kv.Value, def)));
            }
        }

        void ApplyWrite(int cmd, int len, int idx, uint v, byte[] f)
        {
            if (kind == "dx1")
            {
                if (cmd == 0x810A && len == 12)
                {
                    string k = idx == 3 ? "volHp" : idx == 4 ? "volLo" : idx == 5 ? "volAll" : idx == 7 ? "mute" : null;
                    if (k == null) throw new InvalidOperationException("unexpected block frame " + idx);
                    SetState(k, (int)v);
                    queue.Enqueue(Frame(cmd, idx, (int)v, 12));   // the DAC pushes the changed frame back
                }
                else if (cmd == 0x7B00 || cmd == 0x7500 || cmd == 0x7300)
                {
                    SetState(cmd == 0x7B00 ? "input" : cmd == 0x7500 ? "gain" : "filter", (int)v);
                }
                else if (cmd == 0x7100) SetState("power", (int)v);
                else if (cmd == 0x110E)
                {
                    if (v == 0xFFFFFFFF) SetState("dacEqOn", 0);
                    else { SetState("dacEqOn", 1); SetState("dacEqSlot", (int)v); }
                }
                else if ((cmd >> 8) >= 0x91 && (cmd >> 8) <= 0x9C) { }
                else throw new InvalidOperationException("unsafe write 0x" + cmd.ToString("X4"));
                return;
            }
            // DX5 II
            if (cmd == 0x7134 || cmd == 0x7111 || cmd == 0x7112 || (cmd >> 8) >= 0x91 && (cmd >> 8) <= 0x9C) return;
            if (cmd == 0x7102) { SetState("volume", (int)v); return; }
            if (cmd == 0x7101)
            {
                if (Frames.Crc16Modbus(f, 2, 9) != ((f[11] << 8) | f[12])) throw new InvalidOperationException("bad power CRC");
                SetState("powered", (int)v);
                return;
            }
            var field = ToppingTables.Dx5Settable.FirstOrDefault(kv => kv.Value.Key == cmd).Key;
            if (field == null) throw new InvalidOperationException("unsafe write 0x" + cmd.ToString("X4"));
            if (field == "muted" && State().ContainsKey("ignoreMute")) return;   // a DAC that doesn't take it
            SetState(field, (int)v);
        }

        public int Read(byte[] buffer, WaitHandle cancel, int minIntervalMs)
        {
            while (true)
            {
                lock (queue)
                {
                    if (queue.Count > 0)
                    {
                        var r = queue.Dequeue();
                        Array.Copy(r, buffer, Math.Min(r.Length, buffer.Length));
                        return r.Length;
                    }
                }
                if (WaitHandle.WaitAny(new WaitHandle[] { ready, cancel }) == 1) return 0;
            }
        }

        public void Dispose() { ready.Dispose(); }
    }
}

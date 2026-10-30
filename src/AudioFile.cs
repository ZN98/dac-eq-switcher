using System;
using System.IO;
using System.Text;

namespace DacEqSwitcher
{
    /// Reads impulse responses for the response graph: WAV (RIFF/RF64; PCM 8-32 bit, float 32/64,
    /// extensible) and FLAC. Equalizer APO reads more formats through libsndfile; for those the
    /// filter still runs, it just can't be drawn here.
    sealed class AudioFile
    {
        public int SampleRate, Channels;
        public float[][] Samples;   // [channel][frame]

        public static readonly string[] Extensions = { ".wav", ".flac", ".ogg", ".oga", ".opus", ".aif", ".aiff", ".aifc", ".w64", ".rf64", ".caf", ".mp3", ".au", ".snd" };

        public static bool IsAudio(string path)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return Array.IndexOf(Extensions, ext) >= 0;
        }

        /// Throws NotSupportedException for formats this reader doesn't handle.
        public static AudioFile Read(string path)
        {
            var data = File.ReadAllBytes(path);
            if (data.Length >= 12 && Ascii(data, 0, 4) == "fLaC") return Flac.Decode(data);
            if (data.Length >= 12 && (Ascii(data, 0, 4) == "RIFF" || Ascii(data, 0, 4) == "RF64") && Ascii(data, 8, 4) == "WAVE") return ReadWav(data);
            throw new NotSupportedException("format not readable here");
        }

        static string Ascii(byte[] d, int o, int n) { return Encoding.ASCII.GetString(d, o, n); }

        static AudioFile ReadWav(byte[] d)
        {
            bool rf64 = Ascii(d, 0, 4) == "RF64";
            long dataSize64 = -1;
            int fmtTag = 0, ch = 0, rate = 0, bits = 0, blockAlign = 0;
            long dataOff = -1, dataLen = 0;
            int pos = 12;
            while (pos + 8 <= d.Length)
            {
                string id = Ascii(d, pos, 4);
                long size = BitConverter.ToUInt32(d, pos + 4);
                int body = pos + 8;
                if (id == "ds64" && rf64)
                    dataSize64 = BitConverter.ToInt64(d, body + 8);
                else if (id == "fmt ")
                {
                    fmtTag = BitConverter.ToUInt16(d, body);
                    ch = BitConverter.ToUInt16(d, body + 2);
                    rate = (int)BitConverter.ToUInt32(d, body + 4);
                    blockAlign = BitConverter.ToUInt16(d, body + 12);
                    bits = BitConverter.ToUInt16(d, body + 14);
                    if (fmtTag == 0xFFFE && size >= 26) fmtTag = BitConverter.ToUInt16(d, body + 24);   // sub-format GUID
                }
                else if (id == "data")
                {
                    dataOff = body;
                    dataLen = size == 0xFFFFFFFF && dataSize64 >= 0 ? dataSize64 : size;
                    dataLen = Math.Min(dataLen, d.Length - body);
                    break;
                }
                if (size == 0xFFFFFFFF) break;
                pos = body + (int)size + (int)(size & 1);
            }
            if (dataOff < 0 || ch <= 0 || rate <= 0 || blockAlign <= 0) throw new NotSupportedException("incomplete WAV file");
            int frames = (int)(dataLen / blockAlign);
            int bytes = bits / 8;
            var s = new float[ch][];
            for (int c = 0; c < ch; c++) s[c] = new float[frames];
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < ch; c++)
                {
                    int o = (int)dataOff + f * blockAlign + c * bytes;
                    double v;
                    if (fmtTag == 3 && bits == 32) v = BitConverter.ToSingle(d, o);
                    else if (fmtTag == 3 && bits == 64) v = BitConverter.ToDouble(d, o);
                    else if (fmtTag == 1 && bits == 8) v = (d[o] - 128) / 128.0;
                    else if (fmtTag == 1 && bits == 16) v = BitConverter.ToInt16(d, o) / 32768.0;
                    else if (fmtTag == 1 && bits == 24) v = ((d[o] | d[o + 1] << 8 | d[o + 2] << 16) << 8 >> 8) / 8388608.0;
                    else if (fmtTag == 1 && bits == 32) v = BitConverter.ToInt32(d, o) / 2147483648.0;
                    else throw new NotSupportedException("WAV encoding " + fmtTag + "/" + bits + " bit");
                    s[c][f] = (float)v;
                }
            return new AudioFile { SampleRate = rate, Channels = ch, Samples = s };
        }

        // ------------------------------------------------------------- FLAC --
        sealed class Flac
        {
            readonly byte[] d;
            int pos;      // byte
            int bit;      // bits used in d[pos]

            Flac(byte[] data, int start) { d = data; pos = start; }

            long Bits(int n)
            {
                long v = 0;
                for (int i = 0; i < n; i++)
                {
                    if (pos >= d.Length) throw new NotSupportedException("truncated FLAC file");
                    v = (v << 1) | (uint)((d[pos] >> (7 - bit)) & 1);
                    if (++bit == 8) { bit = 0; pos++; }
                }
                return v;
            }

            long Signed(int n)
            {
                if (n == 0) return 0;
                long v = Bits(n);
                return (v & (1L << (n - 1))) != 0 ? v - (1L << n) : v;
            }

            int Unary()
            {
                int n = 0;
                while (Bits(1) == 0) n++;
                return n;
            }

            void Align() { if (bit != 0) { bit = 0; pos++; } }

            public static AudioFile Decode(byte[] data)
            {
                var r = new Flac(data, 4);
                int rate = 0, ch = 0, bps = 0;
                long total = 0;
                for (bool last = false; !last;)
                {
                    last = r.Bits(1) == 1;
                    int type = (int)r.Bits(7);
                    int len = (int)r.Bits(24);
                    int start = r.pos;
                    if (type == 0)
                    {
                        r.Bits(16); r.Bits(16); r.Bits(24); r.Bits(24);
                        rate = (int)r.Bits(20);
                        ch = (int)r.Bits(3) + 1;
                        bps = (int)r.Bits(5) + 1;
                        total = r.Bits(36);
                    }
                    r.pos = start + len; r.bit = 0;
                }
                if (rate == 0) throw new NotSupportedException("FLAC file without stream info");
                var outp = new float[ch][];
                int cap = total > 0 ? (int)total : 1 << 16;
                for (int c = 0; c < ch; c++) outp[c] = new float[cap];
                int written = 0;
                double scale = 1.0 / (1L << (bps - 1));
                while (r.pos + 2 < data.Length)
                {
                    // frame header
                    if (data[r.pos] != 0xFF || (data[r.pos + 1] & 0xFE) != 0xF8) { r.pos++; continue; }
                    r.Bits(15); r.Bits(1);
                    int bsCode = (int)r.Bits(4), srCode = (int)r.Bits(4), chAssign = (int)r.Bits(4), ssCode = (int)r.Bits(3);
                    r.Bits(1);
                    // UTF-8-coded frame/sample number
                    int first = (int)r.Bits(8);
                    int extra = first < 0x80 ? 0 : first < 0xE0 ? 1 : first < 0xF0 ? 2 : first < 0xF8 ? 3 : first < 0xFC ? 4 : first < 0xFE ? 5 : 6;
                    for (int i = 0; i < extra; i++) r.Bits(8);
                    int block;
                    if (bsCode == 1) block = 192;
                    else if (bsCode >= 2 && bsCode <= 5) block = 576 << (bsCode - 2);
                    else if (bsCode == 6) block = (int)r.Bits(8) + 1;
                    else if (bsCode == 7) block = (int)r.Bits(16) + 1;
                    else if (bsCode >= 8) block = 256 << (bsCode - 8);
                    else throw new NotSupportedException("bad FLAC block size");
                    if (srCode == 12) r.Bits(8); else if (srCode == 13 || srCode == 14) r.Bits(16);
                    r.Bits(8);   // CRC-8
                    int fbps = ssCode == 0 ? bps : new[] { 0, 8, 12, 0, 16, 20, 24, 32 }[ssCode];
                    int fch = chAssign < 8 ? chAssign + 1 : 2;
                    var sub = new long[fch][];
                    for (int c = 0; c < fch; c++)
                    {
                        int sbps = fbps;
                        if ((chAssign == 8 && c == 1) || (chAssign == 9 && c == 0) || (chAssign == 10 && c == 1)) sbps++;
                        sub[c] = r.Subframe(block, sbps);
                    }
                    r.Align();
                    r.Bits(16);   // CRC-16
                    if (chAssign >= 8)
                        for (int i = 0; i < block; i++)
                        {
                            long a = sub[0][i], b = sub[1][i];
                            if (chAssign == 8) { sub[1][i] = a - b; }                 // left/side
                            else if (chAssign == 9) { sub[0][i] = a + b; }            // side/right
                            else { long mid = (a << 1) | (b & 1); sub[0][i] = (mid + b) >> 1; sub[1][i] = (mid - b) >> 1; }   // mid/side
                        }
                    if (written + block > outp[0].Length)
                        for (int c = 0; c < ch; c++) Array.Resize(ref outp[c], Math.Max(outp[c].Length * 2, written + block));
                    for (int c = 0; c < Math.Min(ch, fch); c++)
                        for (int i = 0; i < block; i++) outp[c][written + i] = (float)(sub[c][i] * scale);
                    written += block;
                }
                for (int c = 0; c < ch; c++) Array.Resize(ref outp[c], written);
                return new AudioFile { SampleRate = rate, Channels = ch, Samples = outp };
            }

            long[] Subframe(int n, int bps)
            {
                Bits(1);
                int type = (int)Bits(6);
                int wasted = 0;
                if (Bits(1) == 1) wasted = Unary() + 1;
                bps -= wasted;
                var s = new long[n];
                if (type == 0)
                {
                    long v = Signed(bps);
                    for (int i = 0; i < n; i++) s[i] = v;
                }
                else if (type == 1)
                    for (int i = 0; i < n; i++) s[i] = Signed(bps);
                else if (type >= 8 && type <= 12)
                {
                    int order = type - 8;
                    for (int i = 0; i < order; i++) s[i] = Signed(bps);
                    Residual(s, order, n);
                    for (int i = order; i < n; i++)
                    {
                        long p = 0;
                        switch (order)
                        {
                            case 1: p = s[i - 1]; break;
                            case 2: p = 2 * s[i - 1] - s[i - 2]; break;
                            case 3: p = 3 * s[i - 1] - 3 * s[i - 2] + s[i - 3]; break;
                            case 4: p = 4 * s[i - 1] - 6 * s[i - 2] + 4 * s[i - 3] - s[i - 4]; break;
                        }
                        s[i] += p;
                    }
                }
                else if (type >= 32)
                {
                    int order = type - 31;
                    for (int i = 0; i < order; i++) s[i] = Signed(bps);
                    int prec = (int)Bits(4) + 1;
                    int shift = (int)Signed(5);
                    var coef = new long[order];
                    for (int i = 0; i < order; i++) coef[i] = Signed(prec);
                    Residual(s, order, n);
                    for (int i = order; i < n; i++)
                    {
                        long sum = 0;
                        for (int j = 0; j < order; j++) sum += coef[j] * s[i - 1 - j];
                        s[i] += sum >> shift;
                    }
                }
                else throw new NotSupportedException("FLAC subframe type " + type);
                if (wasted > 0) for (int i = 0; i < n; i++) s[i] <<= wasted;
                return s;
            }

            void Residual(long[] s, int order, int n)
            {
                int method = (int)Bits(2);
                int paramBits = method == 0 ? 4 : 5, escape = method == 0 ? 15 : 31;
                int partOrder = (int)Bits(4);
                int parts = 1 << partOrder;
                int idx = order;
                for (int p = 0; p < parts; p++)
                {
                    int count = partOrder == 0 ? n - order : p == 0 ? (n >> partOrder) - order : n >> partOrder;
                    int k = (int)Bits(paramBits);
                    if (k == escape)
                    {
                        int raw = (int)Bits(5);
                        for (int i = 0; i < count; i++) s[idx++] = Signed(raw);
                    }
                    else
                        for (int i = 0; i < count; i++)
                        {
                            long q = Unary();
                            long u = (q << k) | Bits(k);
                            s[idx++] = (u >> 1) ^ -(u & 1);
                        }
                }
            }
        }
    }
}

# Independent reference for the app's Equalizer APO engine, written from Equalizer APO's source
# (BiQuadFilterFactory, BiQuad, BiQuadFilter, IIRFilter, GraphicEQFilter + GainIterator, DelayFilter,
# CopyFilter, ChannelFilter, ConvolutionFilter) using numpy.
#   python3 test/apo_ref.py <work dir>   -> writes test impulse responses there, prints the cases
import base64, math, os, re, struct, subprocess, sys, wave
import numpy as np

WORK = sys.argv[1]
os.makedirs(WORK, exist_ok=True)
POINTS, LO, HI = 400, 20.0, 20000.0

def grid(sr):
    hi = min(HI, sr / 2 * 0.999)
    return LO * (hi / LO) ** (np.arange(POINTS) / (POINTS - 1))

# ---------------------------------------------------------------- test files
rng = np.random.default_rng(7)
def ir(n, decay):
    t = np.arange(n)
    x = rng.standard_normal(n) * np.exp(-t / decay)
    x[0] += 1.0
    return x / np.max(np.abs(x)) * 0.9

def write_wav(path, chans, sr, kind):
    data = np.stack(chans, axis=1)
    if kind == 'f32':
        frames = data.astype('<f4').tobytes(); fmt, bits = 3, 32
    elif kind == 'i16':
        q = np.round(data * 32767).astype('<i2'); frames = q.tobytes(); fmt, bits = 1, 16
        data = q / 32768.0
    elif kind == 'i24':
        q = np.round(data * 8388607).astype('<i4')
        frames = b''.join(int(v).to_bytes(4, 'little', signed=True)[:3] for v in q.ravel()); fmt, bits = 1, 24
        data = q / 8388608.0
    ch = data.shape[1]; ba = ch * bits // 8
    hdr = struct.pack('<4sI4s4sIHHIIHH4sI', b'RIFF', 36 + len(frames), b'WAVE', b'fmt ', 16, fmt, ch, sr, sr * ba, ba, bits, b'data', len(frames))
    open(path, 'wb').write(hdr + frames)
    return [data[:, c].astype(np.float32).astype(np.float64) for c in range(ch)]

files = {}
files['st48.wav'] = (48000, write_wav(os.path.join(WORK, 'st48.wav'), [ir(2048, 300), ir(2048, 150)], 48000, 'f32'))
files['mono44.wav'] = (44100, write_wav(os.path.join(WORK, 'mono44.wav'), [ir(1500, 200)], 44100, 'i16'))
files['st48_24.wav'] = (48000, write_wav(os.path.join(WORK, 'st48_24.wav'), [ir(3000, 400), ir(3000, 250)], 48000, 'i24'))
files['long48.wav'] = (48000, write_wav(os.path.join(WORK, 'long48.wav'), [ir(120000, 20000)], 48000, 'f32'))
# FLAC made from the 24-bit file (lossless, so the same samples)
flac = os.path.join(WORK, 'st48_24.flac')
for cmd in (['flac', '-s', '-f', '-o', flac, os.path.join(WORK, 'st48_24.wav')],
            ['ffmpeg', '-v', 'quiet', '-y', '-i', os.path.join(WORK, 'st48_24.wav'), '-c:a', 'flac', '-sample_fmt', 's32', flac]):
    try:
        subprocess.run(cmd, check=True); break
    except Exception:
        continue
files['st48_24.flac'] = files['st48_24.wav']

# ----------------------------------------------------------------- filters
TYPES = {"PK": "PK", "PEQ": "PK", "Modal": "PK", "LP": "LP", "HP": "HP", "LPQ": "LP", "HPQ": "HP", "BP": "BP",
         "LS": "LS", "HS": "HS", "LSC": "LS", "HSC": "HS", "NO": "NO", "AP": "AP"}

def num(s):
    m = re.match(r'[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?', s.strip())
    return float(m.group(0)) if m else 0.0

def biquad(t, g, f, sr, bw, isbw):
    A = 10 ** (g / 40) if t in ("PK", "LS", "HS") else 10 ** (g / 20)
    w = 2 * math.pi * f / sr; sn, cs = math.sin(w), math.cos(w)
    if not isbw: al = sn / (2 * bw)
    elif t in ("LS", "HS"): al = sn / 2 * math.sqrt((A + 1 / A) * (1 / bw - 1) + 2)
    else: al = sn * math.sinh(math.log(2) / 2 * bw * w / sn)
    be = 2 * math.sqrt(A) * al
    return {
        "LP": ((1 - cs) / 2, 1 - cs, (1 - cs) / 2, 1 + al, -2 * cs, 1 - al),
        "HP": ((1 + cs) / 2, -(1 + cs), (1 + cs) / 2, 1 + al, -2 * cs, 1 - al),
        "BP": (al, 0, -al, 1 + al, -2 * cs, 1 - al),
        "NO": (1, -2 * cs, 1, 1 + al, -2 * cs, 1 - al),
        "AP": (1 - al, -2 * cs, 1 + al, 1 + al, -2 * cs, 1 - al),
        "PK": (1 + al * A, -2 * cs, 1 - al * A, 1 + al / A, -2 * cs, 1 - al / A),
        "LS": (A * ((A + 1) - (A - 1) * cs + be), 2 * A * ((A - 1) - (A + 1) * cs), A * ((A + 1) - (A - 1) * cs - be),
               (A + 1) + (A - 1) * cs + be, -2 * ((A - 1) + (A + 1) * cs), (A + 1) + (A - 1) * cs - be),
        "HS": (A * ((A + 1) + (A - 1) * cs + be), -2 * A * ((A - 1) + (A + 1) * cs), A * ((A + 1) + (A - 1) * cs - be),
               (A + 1) - (A - 1) * cs + be, 2 * ((A - 1) - (A + 1) * cs), (A + 1) - (A - 1) * cs - be),
    }[t]

def freqz(b, a, f, sr):
    z = np.exp(-1j * 2 * np.pi * f / sr)
    return np.polyval(list(b)[::-1], z) / np.polyval(list(a)[::-1], z)

def parse_filter(v, sr):
    m = re.match(r'^\s*ON\s+([A-Za-z]+)', v)
    if not m: return None
    ts = m.group(1)
    if ts == "IIR":
        order = int(re.search(r'\s*Order\s+([0-9]+)', v).group(1))
        c = [float(x) for x in re.search(r'\s+Coefficients((?: [-+0-9.eE]+)+)', v).group(1).split()]
        return c[:order + 1], c[order + 1:]
    v = v.replace(',', '.')
    m = re.match(r'^\s*ON\s+([A-Za-z]+)', v); t = TYPES[ts]; rest = v[m.end():]
    fs = re.search('\\s+Fc\\s*([-+0-9.eE ]+)\\s*H\\s*z', rest).group(1).replace(' ', '')
    f = num(fs)
    if len(fs) >= 5 and 'e' not in fs.lower() and fs[-4] == '.': f *= 1000
    g = 0.0; mg = re.search(r'\s+Gain\s*([-+0-9.eE]+)\s*dB', rest)
    if mg and t not in ("LP", "HP", "NO", "AP"): g = num(mg.group(1))
    bw, isbw, corner = 0.0, False, False
    mq = re.search(r'\s+Q\s*([-+0-9.eE]+)', rest)
    if mq: bw = num(mq.group(1))
    mb = re.search(r'\s+BW\s+Oct\s*([-+0-9.eE]+)', rest)
    if mb and t not in ("LS", "HS"): bw, isbw = num(mb.group(1)), True
    ms = re.match(r'^\s*([-+0-9.eE]+)\s*dB', rest)
    if ms and t in ("LS", "HS"): bw, isbw = num(ms.group(1)), True
    if bw == 0:
        if t in ("LP", "HP", "BP"): bw = math.sqrt(0.5)
        elif t in ("LS", "HS"): bw, isbw = 0.9, True
        elif t == "NO": bw = 30.0
    elif t in ("LS", "HS"):
        if isbw: bw /= 12.0
        if ts[-1] != 'C': corner = True
    bf = f
    if corner:
        s = bw
        if not isbw:
            a = 10 ** (g / 40); s = 1.0 / ((1.0 / (bw * bw) - 2.0) / (a + 1.0 / a) + 1.0)
        fac = 10 ** (abs(g) / 80.0 / s)
        bf = bf * fac if t == "LS" else bf / fac
    c = biquad(t, g, bf, sr, bw, isbw)
    return c[:3], c[3:]

def gain_at(nodes, f):
    if not nodes: return 0.0
    r = 0
    while r < len(nodes) and nodes[r][0] < f: r += 1
    if r == 0: return nodes[0][1]
    if r == len(nodes): return nodes[-1][1]
    (f0, g0), (f1, g1) = nodes[r - 1], nodes[r]
    if g0 == g1: return g0
    return g0 + (math.log(f) - math.log(f0)) / (math.log(f1) - math.log(f0)) * (g1 - g0)

def graphic_fir(nodes, sr, L=16384):
    n2 = 2 * L
    fd = np.zeros(n2)
    g = np.array([10 ** (gain_at(nodes, i * sr / n2) / 20) for i in range(L)])
    fd[:L] = g; fd[n2 - 1 - np.arange(L)] = g
    lf = np.where(fd < 10 ** (-5), math.log(10 ** -5), np.log(np.maximum(fd, 1e-300)))
    t = np.fft.ifft(lf)                       # FFTW backward / N
    i = np.arange(1, L)
    t[i] = (t[i].real + t[n2 - i].real) + 1j * (t[i].imag - t[n2 - i].imag)
    t[n2 - i] = 0
    t[L] = t[L].real - 1j * t[L].imag
    F = np.fft.fft(t)
    F = np.exp(F.real) * np.exp(1j * F.imag)
    td = np.fft.ifft(F)                       # FFTW backward / N
    win = 0.5 * (1 + np.cos(2 * np.pi * np.arange(L) / n2))
    return td[:L].real * win

def dtft(h, f, sr):
    n = np.arange(len(h))
    return np.array([np.sum(h * np.exp(-1j * 2 * np.pi * fi / sr * n)) for fi in f])

# ------------------------------------------------------------ the engine
def run(text, sr):
    f = grid(sr); n = len(f)
    ch = {"L": [np.ones(n, complex), np.zeros(n, complex)], "R": [np.zeros(n, complex), np.ones(n, complex)]}
    names = ["L", "R"]; cur = list(names)
    def idx(w, nm):
        if w[:1].isdigit():
            i = int(re.match(r'\d+', w).group(0)) - 1
            return i if 0 <= i < len(nm) else -1
        return nm.index(w) if w in nm else -1
    for line in text.split('\n'):
        if ':' not in line: continue
        k, v = line.split(':', 1); k = k.strip()
        if k == "Channel":
            words = v.upper().split()
            sel = [False] * len(names)
            for w in words:
                if w == "ALL": sel = [True] * len(names)
                else:
                    i = idx(w, names)
                    if i >= 0: sel[i] = True
            cur = [x for x, s in zip(names, sel) if s]
        elif k.startswith("Filter"):
            ba = parse_filter(v, sr)
            if ba is None: continue
            H = freqz(ba[0], ba[1], f, sr)
            for c in cur: ch[c] = [ch[c][0] * H, ch[c][1] * H]
        elif k == "Preamp":
            g = 10 ** (num(v.replace(',', '.')) / 20)
            for c in cur: ch[c] = [ch[c][0] * g, ch[c][1] * g]
        elif k == "Delay":
            d, unit = v.replace(',', '.').split()[:2]
            samples = math.floor(sr * float(d) / 1000 + 0.5) if unit.lower() == "ms" else math.floor(float(d) + 0.5)
            H = np.exp(-1j * 2 * np.pi * f / sr * samples)
            for c in cur: ch[c] = [ch[c][0] * H, ch[c][1] * H]
        elif k == "Copy":
            snap = {c: [ch[c][0].copy(), ch[c][1].copy()] for c in names}
            for asg in v.split():
                tgt, src = asg.split('=')
                acc = [np.zeros(n, complex), np.zeros(n, complex)]
                for s in src.split('+'):
                    parts = s.split('*')
                    if len(parts) == 2: fac, cname = parts
                    else: fac, cname = ("", parts[0]) if not (parts[0] == "0" or '.' in parts[0]) else (parts[0], "")
                    gl = 1.0
                    if fac:
                        gl = num(fac)
                        if fac.lower().endswith("db"): gl = 10 ** (gl / 20)
                    i = idx(cname, names) if cname else -1
                    if i < 0: continue
                    sc = snap[names[i]]
                    acc = [acc[0] + gl * sc[0], acc[1] + gl * sc[1]]
                ti = idx(tgt, names)
                name = names[ti] if ti >= 0 else tgt
                if ti < 0: names.append(name)
                ch[name] = acc
        elif k == "GraphicEQ":
            vals = [float(x) for x in re.findall(r'[-+0-9.eE]+', v)]
            nodes = sorted((vals[i], vals[i + 1]) for i in range(0, len(vals) - 1, 2))
            H = dtft(graphic_fir(nodes, sr), f, sr)
            for c in cur: ch[c] = [ch[c][0] * H, ch[c][1] * H]
        elif k == "Convolution":
            name = os.path.basename(v.strip())
            fsr, chans = files[name]
            if abs(fsr - sr) > 1: continue
            for i, c in enumerate(cur):
                H = dtft(chans[i % len(chans)], f, sr)
                ch[c] = [ch[c][0] * H, ch[c][1] * H]
    db = lambda x: 20 * np.log10(np.maximum(np.abs(x), 1e-10))
    L, R = ch["L"], ch["R"]
    worst = np.maximum(db(np.abs(L[0]) + np.abs(L[1])), db(np.abs(R[0]) + np.abs(R[1])))
    return db(L[0] + L[1]), db(R[0] + R[1]), worst

CASES = [
    ("peaking: Q, BW Oct, Modal, PEQ", 48000, "Preamp: -6 dB\nFilter 1: ON PK Fc 1000 Hz Gain 5 dB Q 2\nFilter 2: ON PEQ Fc 120 Hz Gain -4 dB BW Oct 0.5\nFilter: ON Modal Fc 60 Hz Gain 3 dB Q 5.41 T60 target 100 ms\nFilter: ON PK Fc 15000 Hz Gain 4 dB BW Oct 0.3"),
    ("pass filters with and without Q", 44100, "Filter: ON LP Fc 8000 Hz\nFilter: ON HPQ Fc 40 Hz Q 1.2\nFilter: ON HP Fc 20 Hz\nFilter: ON LPQ Fc 15000 Hz Q 0.5"),
    ("band-pass, notch, all-pass", 48000, "Filter: ON BP Fc 1000 Hz Q 0.4\nFilter: ON NO Fc 3000 Hz\nFilter: ON NO Fc 6000 Hz Q 3\nFilter: ON AP Fc 900 Hz Q 0.707\nPreamp: 10 dB"),
    ("shelves: plain, Q, slope, 6/12 dB, corner", 48000,
     "Filter: ON LS Fc 300 Hz Gain 5 dB\nFilter: ON LSC 10.8 dB Fc 200 Hz Gain 4 dB\nFilter: ON LSC Fc 90 Hz Gain 6 dB Q 0.7\nFilter: ON HS Fc 1000 Hz Gain -3 dB\n"
     "Filter: ON HSC 6 dB Fc 5000 Hz Gain -6 dB\nFilter: ON HSC Fc 8000 Hz Gain 2 dB Q 0.4272\nFilter: ON LS 6dB Fc 50 Hz Gain 7.2 dB\nFilter: ON LS 12dB Fc 2000 Hz Gain -5 dB\n"
     "Filter: ON HS 6dB Fc 12000 Hz Gain 10 dB\nFilter: ON HS 12dB Fc 500 Hz Gain 5 dB\nFilter: ON LS Fc 100 Hz Gain 6 dB Q 0.7\nFilter: ON HS Fc 3000 Hz Gain -4 dB Q 1.1"),
    ("REW number forms: comma decimals, 1.500 Hz", 48000, "Filter  1: ON PK Fc 1.500 Hz Gain -3,5 dB Q 1,41\nFilter  2: ON PK Fc 62,5 Hz Gain 2,0 dB Q 4,0\nFilter 3: OFF PK Fc 400 Hz Gain 9 dB Q 1"),
    ("custom IIR coefficients", 48000, "Filter: ON IIR Order 2 Coefficients 0.0380602 0.0761205 0.0380602 1.2706 -1.84776 0.729402\nFilter: ON IIR Order 1 Coefficients 0.5 0.5 1 0"),
    ("per-channel EQ and channel numbers", 48000, "Preamp: -3 dB\nChannel: L\nFilter: ON PK Fc 200 Hz Gain 4 dB Q 1\nChannel: 2\nFilter: ON PK Fc 5000 Hz Gain -5 dB Q 2\nPreamp: -1 dB\nChannel: all\nFilter: ON HS Fc 10000 Hz Gain 2 dB"),
    ("delay and crossfeed with Copy", 48000, "Channel: R\nDelay: 0.3 ms\nChannel: all\nCopy: L=0.8*L+-6dB*R R=0.8*R+-6dB*L\nFilter: ON LP Fc 700 Hz"),
    ("Copy: swap, new channel, delay in samples", 44100, "Copy: L=R R=L X=L\nChannel: X\nFilter: ON PK Fc 300 Hz Gain 12 dB Q 1\nChannel: L\nDelay: 13 samples\nChannel: all\nCopy: L=L+0.5*X"),
    ("GraphicEQ (APO's minimum-phase FIR)", 48000, "Preamp: -6 dB\nGraphicEQ: 20 6.2; 50 5.1; 100 3.0; 1000 0; 3000 -2.5; 8000 4; 16000 -3"),
    ("GraphicEQ at 44.1 kHz, unsorted, edges held", 44100, "GraphicEQ: 1000 -2; 100 4; 25 8; 12000 3; 19000 -8"),
    ("convolution, stereo float 48 kHz", 48000, "Preamp: -3 dB\nConvolution: st48.wav"),
    ("convolution, 24-bit and FLAC on swapped channels", 48000, "Channel: R\nConvolution: st48_24.wav\nChannel: L R\nConvolution: st48_24.flac"),
    ("convolution, mono 16-bit at 44.1 kHz on both channels", 44100, "Convolution: mono44.wav\nFilter: ON PK Fc 2000 Hz Gain 3 dB Q 1"),
    ("convolution at the wrong rate is skipped", 48000, "Convolution: mono44.wav\nPreamp: -2 dB"),
    ("long impulse response (2.5 s)", 48000, "Convolution: long48.wav"),
    ("everything together", 48000, "Preamp: -8 dB\nFilter: ON LSC Fc 105 Hz Gain 6 dB Q 0.7\nGraphicEQ: 20 1; 2000 -1; 20000 2\nChannel: L\nDelay: 1 ms\nChannel: all\nCopy: L=L+0.25*R\nConvolution: st48.wav"),
]

for name, sr, text in CASES:
    L, R, W = run(text, sr)
    print("CASE\t" + name + "\t" + str(sr) + "\t" + base64.b64encode(text.encode()).decode())
    for tag, arr in (("L", L), ("R", R), ("W", W)):
        print(tag + "\t" + " ".join("%.6f" % x for x in arr))

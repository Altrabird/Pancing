"""
Pancing soundscape — every sound synthesised from scratch with numpy.

    python art/make_audio.py

Writes 22.05 kHz mono WAVs into unity/Pancing/Assets/Pancing/Resources/Audio/.
Nothing is sampled or downloaded, so there is no licence to track and the whole
audio set is reproducible from this file (fixed seeds).

Ambiences are seamless loops (the tail is cross-faded into the head):
  amb_kampung  merbok (zebra dove) trills, bulbul whistles, light insects, lapping water
  amb_hutan    the tropical cicada swell, forest birds
  amb_sungai   a rushing river bed, for Sungai Berbatu
  amb_malam    crickets and katak (frogs), still water
  amb_hujan    rain on water and leaves
One-shots: cast, plop, nibble, bloop, strike, splash, snap, reel_tick, drag (loop),
catch (bonang-style pentatonic), levelup, coin, click.
"""

import math
import os
import wave

import numpy as np

SR = 22050
OUT = os.path.join(os.path.dirname(__file__), '..', 'unity', 'Pancing', 'Assets', 'Pancing', 'Resources', 'Audio')
rng = np.random.default_rng(1957)


# ------------------------------------------------------------------ dsp ---

def t_axis(dur):
    return np.arange(int(dur * SR)) / SR


def noise(n):
    return rng.standard_normal(n)


def band(x, lo, hi, soft=0.15):
    """Band-pass by FFT mask with smooth (raised-cosine) edges."""
    n = len(x)
    X = np.fft.rfft(x)
    f = np.fft.rfftfreq(n, 1 / SR)
    m = np.ones_like(f)
    if lo > 0:
        w = lo * soft
        m *= np.clip((f - (lo - w)) / (2 * w), 0, 1)
    if hi < SR / 2:
        w = hi * soft
        m *= np.clip(((hi + w) - f) / (2 * w), 0, 1)
    return np.fft.irfft(X * m, n)


def pink(n):
    X = np.fft.rfft(noise(n))
    f = np.fft.rfftfreq(n, 1 / SR)
    f[0] = 1
    return np.fft.irfft(X / np.sqrt(f), n)


def norm(x, peak=0.9):
    m = np.max(np.abs(x)) or 1
    return x * (peak / m)


def env_ad(n, a, d):
    """Attack/decay envelope, seconds."""
    t = np.arange(n) / SR
    e = np.minimum(1, t / max(a, 1e-4)) * np.exp(-np.maximum(0, t - a) / max(d, 1e-4))
    return e


def place(buf, clip, at):
    i = int(at * SR)
    j = min(len(buf), i + len(clip))
    if i < len(buf):
        buf[i:j] += clip[:j - i]


def loopify(x, fade=1.0):
    """Cross-fade the last `fade` seconds into the start so it loops seamlessly."""
    k = int(fade * SR)
    head, tail = x[:k], x[-k:]
    w = np.linspace(0, 1, k)
    out = x[:-k].copy()
    out[:k] = head * w + tail * (1 - w)
    return out


def write(name, x, peak=0.9):
    os.makedirs(OUT, exist_ok=True)
    x = norm(x, peak)
    pcm = (np.clip(x, -1, 1) * 32767).astype(np.int16)
    with wave.open(os.path.join(OUT, name + '.wav'), 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())
    print(f'{name:12s} {len(x) / SR:5.2f}s')


# --------------------------------------------------------------- voices ---

def chirp(f0, f1, dur, vib=0.0, vib_hz=0.0, harm=(1, 0.25), a=0.01, d=None):
    t = t_axis(dur)
    f = f0 + (f1 - f0) * (t / dur) + vib * np.sin(2 * math.pi * vib_hz * t)
    ph = 2 * math.pi * np.cumsum(f) / SR
    s = sum(amp * np.sin(ph * (k + 1)) for k, amp in enumerate(harm))
    e = env_ad(len(t), a, d if d else dur * 0.4) * np.sin(math.pi * np.clip(t / dur, 0, 1)) ** 0.3
    return s * e


def merbok(at_buf, at):
    """Zebra dove: a soft, rolling 'kur-r-r-coo' — fast pulses on a low whistle."""
    for i, (f, dur) in enumerate([(720, .22), (760, .18), (800, .16), (780, .14), (840, .38)]):
        t = t_axis(dur)
        tone = np.sin(2 * math.pi * np.cumsum(np.full(len(t), f + 40 * np.sin(i))) / SR)
        trill = 0.5 + 0.5 * np.sin(2 * math.pi * 26 * t)
        place(at_buf, tone * trill * env_ad(len(t), .02, dur * .6) * .35, at)
        at += dur + .04


def bulbul(buf, at):
    """Yellow-vented bulbul: a bubbly run of 3-6 liquid whistles."""
    for _ in range(rng.integers(3, 7)):
        f0 = rng.uniform(1700, 3000)
        f1 = f0 * rng.uniform(.75, 1.35)
        d = rng.uniform(.06, .14)
        place(buf, chirp(f0, f1, d, vib=60, vib_hz=40, harm=(1, .15, .05)) * .45, at)
        at += d + rng.uniform(.01, .06)


def tweet(buf, at, lo=3200, hi=5200):
    f0 = rng.uniform(lo, hi)
    place(buf, chirp(f0, f0 * rng.uniform(.6, .9), rng.uniform(.04, .09)) * .25, at)


def cicada_layer(n, depth=1.0):
    """The forest's signature: a broadband buzz pulsed ~90 Hz that swells and fades."""
    t = np.arange(n) / SR
    base = band(noise(n), 3400, 5800)
    pulse = 0.55 + 0.45 * np.sin(2 * math.pi * 92 * t) ** 2
    swell = 0.15 + 0.85 * (0.5 - 0.5 * np.cos(2 * math.pi * t / 9.0)) ** 2
    return base * pulse * (swell * depth)


def water_lap(n, level=1.0):
    t = np.arange(n) / SR
    x = band(pink(n), 120, 900)
    am = 0.5 + 0.5 * np.sin(2 * math.pi * 0.23 * t + np.sin(2 * math.pi * 0.07 * t) * 2)
    return x * am * level


# ------------------------------------------------------------ ambiences ---

def amb_kampung():
    d = 24.0
    n = int(d * SR)
    buf = water_lap(n, 0.6) * 0.5
    buf += band(noise(n), 5200, 7500) * 0.04 * (0.6 + 0.4 * np.sin(2 * math.pi * 31 * t_axis(d)))
    lap = np.zeros(n)
    for at in (1.0, 9.5, 17.0):
        merbok(lap, at + rng.uniform(-.3, .3))
    for at in (4.0, 12.5, 20.5):
        bulbul(lap, at)
    for _ in range(14):
        tweet(lap, rng.uniform(0, d - 1))
    buf = norm(buf, .35) + norm(lap, .55)
    write('amb_kampung', loopify(buf), .55)


def amb_hutan():
    d = 27.0
    n = int(d * SR)
    buf = norm(cicada_layer(n), .45)
    birds = np.zeros(n)
    for at in (2.5, 13.0, 21.0):
        bulbul(birds, at)
    for _ in range(10):
        tweet(birds, rng.uniform(0, d - 1), 2500, 4200)
    buf += norm(birds, .35) + norm(water_lap(n), .12)
    write('amb_hutan', loopify(buf), .5)


def amb_sungai():
    d = 16.0
    n = int(d * SR)
    t = t_axis(d)
    rush = band(pink(n), 80, 2600)
    gurgle = band(noise(n), 500, 1400) * (0.5 + 0.5 * np.sin(2 * math.pi * 3.1 * t) * np.sin(2 * math.pi * 0.4 * t))
    write('amb_sungai', loopify(norm(rush, .7) + norm(gurgle, .2)), .5)


def amb_malam():
    d = 20.0
    n = int(d * SR)
    t = t_axis(d)
    buf = norm(water_lap(n), .15)
    crick = np.zeros(n)
    for start in np.arange(0, d - .2, 1 / 2.7):        # cricket chirps ~2.7 / s
        tt = t_axis(.06)
        c = np.sin(2 * math.pi * 4600 * tt) * (0.5 + 0.5 * np.sin(2 * math.pi * 60 * tt)) * env_ad(len(tt), .005, .03)
        place(crick, c, start + rng.uniform(0, .02))
    crick2 = np.roll(crick, int(.37 * SR)) * .6
    frogs = np.zeros(n)
    for _ in range(26):                                 # katak: low two-pulse croaks
        at = rng.uniform(0, d - .5)
        f = rng.uniform(170, 300)
        for k in range(2):
            tt = t_axis(.09)
            ph = 2 * math.pi * np.cumsum(f * (1 + .25 * np.exp(-tt * 30))) / SR
            cr = (np.sin(ph) + .6 * np.sin(2 * ph) + .3 * np.sin(3 * ph)) * env_ad(len(tt), .005, .04)
            place(frogs, cr, at + k * .12)
    buf += norm(crick + crick2, .3) + norm(frogs, .45)
    write('amb_malam', loopify(buf), .5)


def amb_hujan():
    d = 14.0
    n = int(d * SR)
    hiss = band(noise(n), 1200, 9000)
    drops = np.zeros(n)
    for _ in range(int(d * 90)):
        tt = t_axis(.02)
        f = rng.uniform(1800, 4200)
        place(drops, np.sin(2 * math.pi * f * tt) * env_ad(len(tt), .001, .006) * rng.uniform(.2, 1), rng.uniform(0, d))
    write('amb_hujan', loopify(norm(hiss, .5) + norm(drops, .4) + norm(band(pink(n), 60, 400), .2)), .55)


# ----------------------------------------------------------------- sfx ---

def sfx():
    # cast: a whoosh sweeping down, plus the spool zipping out
    d = .55
    t = t_axis(d)
    x = noise(len(t))
    out = np.zeros(len(t))
    for i, (lo, hi) in enumerate([(2400, 4200), (1500, 2800), (900, 1800), (500, 1100)]):
        seg = band(x, lo, hi) * np.exp(-((t - (.08 + i * .1)) ** 2) / .004)
        out += seg
    zip_ = np.zeros(len(t))
    for k, at in enumerate(np.cumsum(np.linspace(.008, .03, 18))):
        if at < d:
            place(zip_, band(noise(80), 2500, 6000) * env_ad(80, .0005, .002), at)
    write('cast', norm(out, .8) + norm(zip_, .25), .8)

    # plop: the lure landing
    tt = t_axis(.25)
    f = 900 * np.exp(-tt * 18) + 260
    plop = np.sin(2 * math.pi * np.cumsum(f) / SR) * env_ad(len(tt), .002, .05)
    plop += band(noise(len(tt)), 800, 3000) * env_ad(len(tt), .001, .02) * .5
    write('plop', plop, .8)

    # nibble: a tiny tick on the float
    tt = t_axis(.09)
    nib = np.sin(2 * math.pi * np.cumsum(1300 * np.exp(-tt * 30) + 500) / SR) * env_ad(len(tt), .001, .02)
    write('nibble', nib, .6)

    # bloop: the float pulled under
    tt = t_axis(.35)
    f = 320 * np.exp(-tt * 6) + 110
    bl = np.sin(2 * math.pi * np.cumsum(f) / SR) * env_ad(len(tt), .004, .12)
    bl += band(noise(len(tt)), 300, 1500) * env_ad(len(tt), .002, .05) * .4
    write('bloop', bl, .9)

    # strike: a short whip
    tt = t_axis(.22)
    x = band(noise(len(tt)), 1500, 6000) * np.exp(-((tt - .05) ** 2) / .0012)
    write('strike', x, .8)

    # splash: water thrown, then a few bubbles
    d = 1.1
    tt = t_axis(d)
    sp = band(pink(len(tt)), 200, 6000) * env_ad(len(tt), .004, .18)
    sp += band(noise(len(tt)), 2000, 7000) * env_ad(len(tt), .002, .06) * .5
    for _ in range(9):
        b = t_axis(.05)
        place(sp, np.sin(2 * math.pi * np.cumsum(rng.uniform(500, 1400) * (1 + b * 8)) / SR) * env_ad(len(b), .002, .015) * .25, rng.uniform(.15, .8))
    write('splash', sp, .9)

    # snap: the line parting — a crack and a falling twang
    tt = t_axis(.7)
    tw = np.sin(2 * math.pi * np.cumsum(480 * np.exp(-tt * 5) + 90) / SR)
    tw = (tw + .4 * np.sign(tw)) * env_ad(len(tt), .001, .18)
    tw += band(noise(len(tt)), 2000, 8000) * env_ad(len(tt), .0005, .01) * 2
    write('snap', tw, .9)

    # reel_tick: one click of the reel's anti-reverse
    tt = t_axis(.03)
    tk = band(noise(len(tt)), 2500, 7000) * env_ad(len(tt), .0003, .003)
    tk += np.sin(2 * math.pi * 3100 * tt) * env_ad(len(tt), .0003, .004) * .5
    write('reel_tick', tk, .7)

    # drag: the clutch giving line — a fast ratchet buzz (loops)
    d = 1.0
    buf = np.zeros(int(d * SR))
    for at in np.arange(0, d, 1 / 46):
        place(buf, band(noise(120), 1800, 6000) * env_ad(120, .0003, .002), at)
    buf += np.sin(2 * math.pi * 46 * t_axis(d)) * .05
    write('drag', buf, .75)

    # catch: bonang-style kettle gongs up a slendro-ish scale
    scale = [293.7, 336.0, 386.0, 440.0, 504.0, 587.3]
    d = 2.2
    buf = np.zeros(int(d * SR))
    for i, f in enumerate(scale[:5] + [scale[5] * 1.0]):
        buf_note = gong(f, 1.4)
        place(buf, buf_note * (0.8 if i < 5 else 1.0), i * .11)
    write('catch', buf, .85)

    # levelup: a longer run, doubled at the octave
    d = 2.8
    buf = np.zeros(int(d * SR))
    for i, f in enumerate(scale + [f * 2 for f in scale[:3]]):
        place(buf, gong(f, 1.6), i * .085)
    write('levelup', buf, .85)

    # coin: two bright pings
    d = .6
    buf = np.zeros(int(d * SR))
    place(buf, gong(1318, .5), 0)
    place(buf, gong(1760, .5), .08)
    write('coin', buf, .7)

    # click: soft wooden UI tick
    tt = t_axis(.05)
    ck = np.sin(2 * math.pi * 1650 * tt) * env_ad(len(tt), .0005, .008) + band(noise(len(tt)), 1000, 4000) * env_ad(len(tt), .0003, .003) * .4
    write('click', ck, .5)


def fight_music():
    """Gendang and kompang under the fight: a driving 2-bar groove (looped), and a
    separate shaker/tremolo layer the game brings in as the tension climbs."""
    bpm = 118
    step = 60 / bpm / 4                      # sixteenth
    bars = 4
    n = int(step * 16 * bars * SR)
    buf = np.zeros(n)

    def dung(f0=95):                          # gendang bass head: pitch-dropping thump
        tt = t_axis(.35)
        f = f0 * (1 + 1.2 * np.exp(-tt * 28))
        return np.sin(2 * math.pi * np.cumsum(f) / SR) * env_ad(len(tt), .002, .12)

    def tak():                                # gendang slap: bright, short
        tt = t_axis(.09)
        return (band(noise(len(tt)), 1800, 6000) * env_ad(len(tt), .0005, .018)
                + np.sin(2 * math.pi * 620 * tt) * env_ad(len(tt), .0005, .03) * .5)

    def kompang():                            # frame drum: mid ring + skin slap
        tt = t_axis(.22)
        ring = np.sin(2 * math.pi * 238 * tt) * env_ad(len(tt), .001, .07)
        return ring + band(noise(len(tt)), 600, 3000) * env_ad(len(tt), .0005, .025) * .7

    #            1 e & a 2 e & a 3 e & a 4 e & a
    pat_dung = "x.....x...x....." + "x.....x.x...x..."
    pat_tak = "....x.......x..x" + "....x.......x.xx"
    pat_komp = "..x...x...x...x." + "..x...x.x.x...x."
    for b in range(bars):
        for i in range(16):
            at = (b * 16 + i) * step
            k = (b % 2) * 16 + i
            if pat_dung[k] == 'x':
                place(buf, dung(90 if i else 82) * .9, at)
            if pat_tak[k] == 'x':
                place(buf, tak() * .55, at)
            if pat_komp[k] == 'x':
                place(buf, kompang() * .5 * (1.0 if i % 4 == 2 else .75), at + rng.uniform(0, .006))
    write('fight_drums', buf, .8)

    # shaker on every sixteenth with an accent, plus a low drone pulse
    sh = np.zeros(n)
    for j in range(16 * bars):
        tt = t_axis(.06)
        hit = band(noise(len(tt)), 4000, 9000) * env_ad(len(tt), .004, .02) * (1.0 if j % 4 == 2 else .55)
        place(sh, hit, j * step)
    tt = t_axis(n / SR)
    drone = np.sin(2 * math.pi * 55 * tt) * (0.5 + 0.5 * np.sin(2 * math.pi * (bpm / 60) * tt) ** 8) * .5
    write('fight_tension', norm(sh, .7) + norm(drone, .35), .7)


def gong(f, dur):
    """A small bronze kettle gong: inharmonic partials, the high ones dying fast."""
    tt = t_axis(dur)
    parts = [(1.0, 1.0, 1.2), (2.76, .45, .35), (5.4, .2, .15), (8.93, .1, .08)]
    s = sum(a * np.sin(2 * math.pi * f * r * tt) * np.exp(-tt / dec) for r, a, dec in parts)
    s *= np.minimum(1, tt / .003)
    s += np.sin(2 * math.pi * f * 1.004 * tt) * np.exp(-tt / 1.2) * .3       # beating, the bronze shimmer
    return s * .5


if __name__ == '__main__':
    amb_kampung()
    amb_hutan()
    amb_sungai()
    amb_malam()
    amb_hujan()
    sfx()
    fight_music()

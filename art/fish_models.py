"""
Pancing fish models — anatomically shaped, generated in Blender.

    exec(open(r"...\\Pancing\\art\\fish_models.py").read(), g)   # g with __file__ set
    build_all()          # builds every species into the "Fish" collection
    export_all_fish()    # -> unity/.../Resources/Fish/<id>.fbx

The old FishMeshGen lofted every species from one 5-point radius spline, which
made every fish a tall egg: a tilapia came out about as deep as it was long. Real
fish differ in exactly the things that spline could not say — a keli is 14% as
deep as it is long and broader than it is tall at the head, a lampam is a deep
silver disc with a sail of a dorsal, a belida is a knife with one fin running
the length of its belly. So each species here is described by what an angler
would actually recognise:

  - side profile: dorsal and ventral outline + half-width, sampled along the body
  - fins: dorsal (spiny/soft, long or short), adipose, anal, caudal shape,
    pectoral and pelvic pairs
  - barbels for the catfishes
  - colours: back / flank / belly / fins / accent, plus a pattern

Conventions (Blender space; the FBX export turns this into the game's space):
  snout at Y = 0, the caudal-fin ROOT at Y = 1, tail fin beyond; Z up; X lateral.
  In Unity that is snout at z = 0, tail toward +z, length 1 to the tail root —
  the same frame FishMeshGen used, so TackleView's scale-by-real-length holds.
Colours are per-corner vertex colours (sRGB hex in, linear stored); no textures.
"""

import bpy
import bmesh
import math
import os
import random
from mathutils import Vector

OUT = r"C:\Users\HARSIDI BIN JUNICK\Desktop\Pancing\unity\Pancing\Assets\Pancing\Resources\Fish"
RING = 18
SEG = 44

# ---------------------------------------------------------------- helpers ---


def s2l(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def hexl(h):
    h = h.lstrip('#')
    return tuple(s2l(int(h[i:i + 2], 16) / 255) for i in (0, 2, 4))


def lerp(a, b, t):
    return a + (b - a) * t


def lerpc(a, b, t):
    return tuple(lerp(a[k], b[k], t) for k in range(3))


def shade(c, f):
    return tuple(max(0.0, min(1.0, x * (1 + f))) for x in c)


def smooth(e0, e1, x):
    t = max(0.0, min(1.0, (x - e0) / (e1 - e0)))
    return t * t * (3 - 2 * t)


U8 = [0.0, 0.05, 0.15, 0.30, 0.50, 0.70, 0.85, 1.0]


def sample(vals, u, us=U8):
    """Catmull-Rom through (us, vals)."""
    u = max(0.0, min(1.0, u))
    i = 0
    while i < len(us) - 2 and u > us[i + 1]:
        i += 1
    t = (u - us[i]) / (us[i + 1] - us[i])
    p0 = vals[max(i - 1, 0)]
    p1 = vals[i]
    p2 = vals[i + 1]
    p3 = vals[min(i + 2, len(vals) - 1)]
    t2, t3 = t * t, t * t * t
    return 0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3)


def hash2(x, y, s):
    h = (s * 374761393 + x * 668265263 + y * 2147483647) & 0xFFFFFFFF
    h = ((h ^ (h >> 13)) * 1274126177) & 0xFFFFFFFF
    return ((h ^ (h >> 16)) & 0xFFFFFFFF) / 4294967296.0


def vnoise(x, y, s=0):
    xi, yi = math.floor(x), math.floor(y)
    fx, fy = x - xi, y - yi
    fx = fx * fx * (3 - 2 * fx)
    fy = fy * fy * (3 - 2 * fy)
    a = hash2(xi, yi, s); b = hash2(xi + 1, yi, s)
    c = hash2(xi, yi + 1, s); d = hash2(xi + 1, yi + 1, s)
    return lerp(lerp(a, b, fx), lerp(c, d, fx), fy)


def fbm(x, y, s=0, o=3):
    t, a, n = 0.0, 1.0, 0.0
    for i in range(o):
        t += vnoise(x * 2 ** i, y * 2 ** i, s + i * 101) * a
        n += a
        a *= 0.5
    return t / n


# --------------------------------------------------------------- species ---
# top/bot: outline heights (fraction of length) at U8; w: half-width.
# dorsal: list of (u0, u1, h0, h1, spiny). adipose: (u0, u1, h) or None.
# anal: (u0, u1, h0, h1). caudal: (type, span, sweep). pect: size. pelv: (u, size).
# barbels: list of (u, z, length, droop, spread). colours are sRGB hex.

S = {}

S['tilapia'] = dict(
    top=[.02, .08, .17, .22, .22, .17, .10, .065], bot=[-.012, -.06, -.13, -.17, -.17, -.12, -.075, -.055],
    w=[.012, .05, .08, .085, .075, .055, .035, .02],
    dorsal=[(.30, .62, .10, .11, True), (.62, .86, .13, .07, False)], anal=(.64, .86, .09, .06),
    caudal=('truncate', .17, .16), pect=.12, pelv=(.33, .10), eye=(.10, .045, .035),
    back='#3d4a3b', flank='#7d8f6f', belly='#dcdcbf', fin='#5b5f4c', accent='#2a3027', edge='#b4553c',
    pattern='bars', amt=.35, lips=True)

S['keli'] = dict(
    top=[.012, .035, .058, .068, .068, .064, .058, .05], bot=[-.012, -.03, -.052, -.062, -.062, -.058, -.052, -.045],
    w=[.035, .07, .085, .068, .055, .045, .035, .024],
    dorsal=[(.30, .97, .045, .05, False)], anal=(.47, .97, .04, .045),
    caudal=('round', .085, .11), pect=.07, pelv=(.40, .05), eye=(.06, .03, .012),
    back='#2b2621', flank='#5a4d3d', belly='#c9bca0', fin='#3a332b', accent='#211c17', edge='#2a241e',
    pattern='mottle', amt=.25, flat_head=1.0,
    barbels=[(.015, .02, .26, .25, .9), (.02, -.01, .20, .5, .6), (.03, -.03, .16, .7, .3), (.035, -.035, .13, .8, .15)])

S['lampam'] = dict(
    top=[.02, .085, .19, .26, .23, .15, .085, .055], bot=[-.012, -.06, -.135, -.175, -.165, -.10, -.055, -.04],
    w=[.01, .04, .06, .065, .055, .04, .024, .015],
    dorsal=[(.38, .54, .23, .06, False)], anal=(.70, .80, .10, .04),
    caudal=('fork', .22, .20), pect=.12, pelv=(.42, .10), eye=(.10, .05, .04),
    back='#7a7f8a', flank='#cfd5dc', belly='#f6f3e6', fin='#e2b448', accent='#9aa0aa', edge='#e8a12c',
    pattern='scales', amt=.25, barbels=[(.03, .0, .04, .4, .5)])

S['puyu'] = dict(
    top=[.02, .075, .135, .16, .16, .13, .09, .065], bot=[-.015, -.06, -.11, -.13, -.13, -.10, -.07, -.05],
    w=[.015, .05, .075, .08, .07, .055, .035, .02],
    dorsal=[(.30, .66, .08, .085, True), (.66, .86, .10, .06, False)], anal=(.60, .86, .08, .06),
    caudal=('round', .14, .15), pect=.10, pelv=(.32, .08), eye=(.09, .04, .03),
    back='#3e3a2e', flank='#7a775a', belly='#c4bb8f', fin='#5e5a45', accent='#26231b', edge='#5e5a45',
    pattern='mottle', amt=.45, spot_tail=True)

S['haruan'] = dict(
    top=[.012, .04, .075, .086, .086, .08, .07, .05], bot=[-.012, -.04, -.07, -.08, -.08, -.075, -.065, -.045],
    w=[.022, .06, .08, .08, .07, .06, .045, .025],
    dorsal=[(.33, .95, .055, .06, False)], anal=(.56, .95, .05, .055),
    caudal=('round', .11, .13), pect=.09, pelv=(.30, .05), eye=(.07, .03, .022),
    back='#272c21', flank='#5b6646', belly='#d2cfaa', fin='#3a3f2d', accent='#191c14', edge='#3a3f2d',
    pattern='chevron', amt=.75, flat_head=.7, big_mouth=True)

S['sebarau'] = dict(
    top=[.015, .06, .12, .15, .14, .10, .062, .04], bot=[-.012, -.05, -.10, -.12, -.115, -.08, -.045, -.03],
    w=[.01, .04, .06, .065, .055, .04, .025, .015],
    dorsal=[(.40, .55, .15, .05, False)], anal=(.70, .80, .08, .04),
    caudal=('fork', .20, .21), pect=.11, pelv=(.43, .09), eye=(.09, .045, .032),
    back='#4f5864', flank='#b2bcc5', belly='#eef1f3', fin='#d06a3a', accent='#1e242b', edge='#c8452f',
    pattern='band', amt=.85)

S['baung'] = dict(
    top=[.012, .045, .095, .11, .10, .08, .06, .04], bot=[-.012, -.04, -.072, -.09, -.085, -.065, -.045, -.03],
    w=[.032, .07, .085, .075, .06, .045, .03, .02],
    dorsal=[(.30, .40, .13, .04, True)], adipose=(.58, .86, .045), anal=(.66, .80, .07, .04),
    caudal=('fork', .19, .20), pect=.11, pelv=(.45, .07), eye=(.07, .035, .022),
    back='#3d3429', flank='#8a7355', belly='#e2d3b0', fin='#6a5640', accent='#26201a', edge='#7a3d2a',
    pattern='plain', amt=0, flat_head=.8,
    barbels=[(.015, .02, .52, .15, 1.0), (.02, -.02, .22, .5, .6), (.03, -.035, .17, .75, .3), (.025, .035, .12, .2, .2)])

S['jelawat'] = dict(
    top=[.015, .05, .10, .13, .13, .10, .065, .045], bot=[-.012, -.045, -.09, -.11, -.11, -.085, -.05, -.035],
    w=[.01, .04, .06, .065, .055, .04, .025, .015],
    dorsal=[(.44, .56, .14, .05, False)], anal=(.72, .80, .07, .04),
    caudal=('fork', .21, .21), pect=.11, pelv=(.45, .09), eye=(.09, .042, .032),
    back='#5d6a62', flank='#bfc9bd', belly='#f2efe0', fin='#d8663f', accent='#232a26', edge='#d8663f',
    pattern='stripe', amt=.8, barbels=[(.03, .0, .05, .4, .5)])

S['patin'] = dict(
    top=[.015, .05, .10, .13, .12, .09, .06, .04], bot=[-.015, -.05, -.10, -.12, -.115, -.09, -.055, -.035],
    w=[.022, .06, .075, .075, .065, .05, .03, .02],
    dorsal=[(.33, .41, .14, .04, True)], adipose=(.74, .80, .025), anal=(.55, .86, .07, .05),
    caudal=('fork', .22, .22), pect=.12, pelv=(.45, .07), eye=(.08, .025, .032),
    back='#3f4752', flank='#a9b3bd', belly='#eef2f5', fin='#5b6470', accent='#2f353c', edge='#2f353c',
    pattern='plain', amt=0, barbels=[(.02, .015, .10, .3, .7), (.03, -.03, .07, .7, .3)])

S['toman'] = dict(
    top=[.012, .045, .08, .09, .09, .085, .075, .055], bot=[-.012, -.045, -.075, -.085, -.085, -.08, -.07, -.05],
    w=[.026, .065, .085, .085, .075, .065, .05, .03],
    dorsal=[(.32, .95, .06, .065, False)], anal=(.55, .95, .055, .06),
    caudal=('round', .12, .14), pect=.10, pelv=(.30, .05), eye=(.07, .032, .024),
    back='#1e2a25', flank='#3f5a48', belly='#c3c08f', fin='#2a3530', accent='#c24a2c', edge='#a03a24',
    pattern='band', amt=.6, flat_head=.7, big_mouth=True)

S['belida'] = dict(
    top=[.01, .06, .17, .22, .18, .10, .05, .018], bot=[-.012, -.05, -.12, -.16, -.17, -.13, -.07, -.02],
    w=[.01, .035, .05, .045, .035, .025, .015, .008],
    dorsal=[(.42, .50, .05, .03, False)], anal=(.36, 1.0, .07, .06),
    caudal=('merge', .05, .06), pect=.09, pelv=(.38, .02), eye=(.07, .05, .025),
    back='#2f3640', flank='#7d879a', belly='#dfe6ee', fin='#5d6676', accent='#141820', edge='#d8dfe6',
    pattern='ocelli', amt=.9, humped=True)

S['kelah'] = dict(
    top=[.02, .07, .14, .17, .16, .12, .07, .045], bot=[-.015, -.06, -.11, -.13, -.125, -.09, -.05, -.035],
    w=[.015, .05, .075, .08, .07, .05, .03, .02],
    dorsal=[(.40, .54, .17, .05, False)], anal=(.70, .80, .09, .04),
    caudal=('fork', .21, .21), pect=.12, pelv=(.43, .10), eye=(.09, .045, .03),
    back='#6a2f25', flank='#c96b45', belly='#f6d9a8', fin='#d2502e', accent='#3a1c16', edge='#e2452a',
    pattern='scales', amt=.75, lips=True, barbels=[(.025, -.005, .05, .5, .5), (.035, -.02, .04, .6, .3)])

# ------------------------------------------------------------ the builder ---


class MB:
    """Collects verts, faces and per-face-corner colours, then makes a mesh."""

    def __init__(self):
        self.v = []
        self.f = []
        self.c = []      # list of colours per face (per corner list)
        self.smooth = []

    def add_v(self, p):
        self.v.append(Vector(p))
        return len(self.v) - 1

    def face(self, idx, cols, smooth=False):
        self.f.append(idx)
        self.c.append(cols)
        self.smooth.append(smooth)

    def to_object(self, name, coll):
        me = bpy.data.meshes.new(name)
        me.from_pydata([tuple(p) for p in self.v], [], self.f)
        me.update()
        ca = me.color_attributes.new('Col', 'FLOAT_COLOR', 'CORNER')
        for p in me.polygons:
            cl = self.c[p.index]
            for k, li in enumerate(p.loop_indices):
                c = cl[k] if isinstance(cl, list) else cl
                ca.data[li].color = (c[0], c[1], c[2], 1.0)
            p.use_smooth = self.smooth[p.index]
        me.color_attributes.active_color = ca
        old = bpy.data.objects.get(name)
        if old is not None:
            bpy.data.objects.remove(old, do_unlink=True)
        ob = bpy.data.objects.new(name, me)
        coll.objects.link(ob)
        return ob


def fish_colour(sp, u, v, side_x, seed):
    """u nose->tail, v belly(0)->back(1)."""
    back, flank, belly = hexl(sp['back']), hexl(sp['flank']), hexl(sp['belly'])
    acc = hexl(sp['accent'])
    c = lerpc(belly, flank, smooth(.12, .45, v)) if v < .45 else lerpc(flank, back, smooth(.45, .9, v))
    c = shade(c, (fbm(u * 26, v * 12, seed) - .5) * .18)
    amt = sp.get('amt', 0)
    pat = sp.get('pattern', 'plain')
    m = 0.0
    if pat == 'bars':
        m = smooth(.35, .7, .5 + .5 * math.sin(u * math.pi * 13 + 1.3)) * smooth(.2, .55, v) * smooth(.12, .3, u)
    elif pat == 'stripe':
        m = 1 - smooth(0, .07, abs(v - .52 + .04 * math.sin(u * 3)))
        m *= smooth(.12, .25, u)
    elif pat == 'band':
        m = (1 - smooth(.0, .16, abs(v - .5))) * smooth(.35, .6, fbm(u * 6, v * 3, seed + 5)) * smooth(.15, .3, u)
    elif pat == 'chevron':
        m = smooth(.45, .65, .5 + .5 * math.sin(u * math.pi * 12 - abs(v - .5) * 7)) * smooth(.25, .6, v) * smooth(.15, .3, u)
    elif pat == 'mottle':
        m = smooth(.5, .7, fbm(u * 10, v * 6, seed + 9, 4))
    elif pat == 'scales':
        # Diamond scale net: dark edges on each scale.
        a = u * 46 + v * 9
        b = u * 46 - v * 9
        e = min(abs(math.sin(a * math.pi / 2)), abs(math.sin(b * math.pi / 2)))
        m = (1 - smooth(.0, .35, e)) * .7 * smooth(.2, .3, u)
    elif pat == 'ocelli':
        # Belida's white-ringed black spots toward the tail, along the anal-fin base.
        m = 0.0
        for k in range(6):
            cu, cv = .58 + k * .07, .25
            d = math.hypot((u - cu) * 3.2, v - cv)
            if d < .07:
                m = max(m, 1 - smooth(.035, .06, d))
    c = lerpc(c, acc, max(0, min(1, m)) * amt)
    if pat == 'ocelli':
        # pale ring around each spot
        for k in range(6):
            cu, cv = .58 + k * .07, .25
            d = math.hypot((u - cu) * 3.2, v - cv)
            if .06 < d < .085:
                c = lerpc(c, hexl('#e8eef2'), .7)
    if sp.get('spot_tail') and u > .9 and abs(v - .55) < .18:
        c = lerpc(c, acc, .8)
    # lateral line
    if pat not in ('stripe',) and abs(v - .6 + .05 * u) < .012 and u > .2:
        c = shade(c, .18)
    # gill cover: a darker crescent
    if .17 < u < .205 and .25 < v < .8:
        c = shade(c, -.28)
    # head a touch darker on top, mouth dark
    if u < .03 and .3 < v < .55:
        c = shade(c, -.5)
    return c


def build_fish(sid, sp, coll):
    mb = MB()
    seed = sum(ord(ch) for ch in sid)
    rnd = random.Random(seed)
    flat = sp.get('flat_head', 0.0)

    def outline(u):
        top = sample(sp['top'], u)
        bot = sample(sp['bot'], u)
        w = sample(sp['w'], u)
        return top, bot, w

    rings = []
    for s in range(SEG + 1):
        u = (s / SEG) ** 1.15          # denser rings at the head
        top, bot, w = outline(u)
        cy = (top + bot) / 2
        hh = max((top - bot) / 2, 0.002)
        ring = []
        for k in range(RING):
            a = k / RING * 2 * math.pi
            ca, sa = math.cos(a), math.sin(a)
            # superellipse: flat-headed fish are boxier near the snout
            p = 2.0 + flat * 1.6 * (1 - smooth(.0, .3, u))
            ex = math.copysign(abs(sa) ** (2 / p), sa)
            ez = math.copysign(abs(ca) ** (2 / p), ca)
            x = ex * w
            z = cy + ez * hh
            if flat and u < .3:          # flatten the top of the head
                z = min(z, cy + hh * (1 - .25 * flat * (1 - u / .3)))
            v = .5 + .5 * ca
            ring.append(mb.add_v((x, u, z)))
        rings.append((u, ring))

    # body quads
    for s in range(SEG):
        u0, r0 = rings[s]
        u1, r1 = rings[s + 1]
        for k in range(RING):
            k2 = (k + 1) % RING
            idx = [r0[k], r0[k2], r1[k2], r1[k]]
            cols = []
            for vi, uu in ((r0[k], u0), (r0[k2], u0), (r1[k2], u1), (r1[k], u1)):
                kk = (r0 + r1).index(vi) % RING
                a = kk / RING * 2 * math.pi
                cols.append(fish_colour(sp, uu, .5 + .5 * math.cos(a), mb.v[vi].x, seed))
            mb.face(idx, cols, smooth=True)
    # snout cap
    tip = mb.add_v((0, -0.004, (sp['top'][0] + sp['bot'][0]) / 2))
    r0 = rings[0][1]
    for k in range(RING):
        mb.face([tip, r0[(k + 1) % RING], r0[k]], fish_colour(sp, 0, .45, 0, seed), smooth=True)
    # tail root cap
    tailc = mb.add_v((0, 1.0, (sp['top'][-1] + sp['bot'][-1]) / 2))
    rl = rings[-1][1]
    for k in range(RING):
        mb.face([tailc, rl[k], rl[(k + 1) % RING]], fish_colour(sp, 1, .5, 0, seed), smooth=True)

    fin = hexl(sp['fin'])
    edge = hexl(sp['edge'])

    def membrane(base_pts, tip_pts, base_col, tip_col, rays=True):
        """Strip between two polylines; alternating darker rays."""
        n = len(base_pts)
        bi = [mb.add_v(p) for p in base_pts]
        ti = [mb.add_v(p) for p in tip_pts]
        for i in range(n - 1):
            ray = shade(base_col, -.18) if (rays and i % 2) else base_col
            tc = lerpc(tip_col, ray, .3)
            mb.face([bi[i], bi[i + 1], ti[i + 1], ti[i]], [ray, ray, tc, tc])

    # dorsal fins
    for (u0, u1, h0, h1, spiny) in sp.get('dorsal', []):
        n = max(3, int((u1 - u0) * 60))
        base, tipp = [], []
        for i in range(n + 1):
            t = i / n
            u = lerp(u0, u1, t)
            top, _, _ = outline(u)
            h = lerp(h0, h1, t)
            if spiny and i % 2 == 1:
                h *= .82
            if not spiny:
                h *= math.sin(math.pi * (.15 + .85 * t)) ** .35 if t > .5 else 1
            base.append((0, u, top - .004))
            tipp.append((0, u + h * .35, top + h))
        membrane(base, tipp, fin, edge)
    if sp.get('adipose'):
        u0, u1, h = sp['adipose']
        n = 8
        base, tipp = [], []
        for i in range(n + 1):
            t = i / n
            u = lerp(u0, u1, t)
            top, _, _ = outline(u)
            base.append((0, u, top - .003))
            tipp.append((0, u + .01, top + h * math.sin(math.pi * t) ** .6))
        membrane(base, tipp, hexl(sp['back']), hexl(sp['back']), rays=False)
    if sp.get('anal'):
        u0, u1, h0, h1 = sp['anal']
        n = max(3, int((u1 - u0) * 60))
        base, tipp = [], []
        for i in range(n + 1):
            t = i / n
            u = lerp(u0, min(u1, .995), t)
            _, bot, _ = outline(u)
            h = lerp(h0, h1, t) * (math.sin(math.pi * (.1 + .9 * t)) ** .3)
            base.append((0, u, bot + .004))
            tipp.append((0, u + h * .4, bot - h))
        membrane(base, tipp, fin, edge)

    # caudal fin
    ctype, span, sweep = sp['caudal']
    top1, bot1, _ = outline(1.0)
    cz = (top1 + bot1) / 2
    if ctype == 'fork':
        prof = [(0, .35), (.55, .7), (1, 1.0), (.62, .55), (.48, .0), (.62, -.55), (1, -1.0), (.55, -.7), (0, -.35)]
    elif ctype == 'round':
        prof = [(0, .5)] + [(.55 + .45 * math.cos(a), math.sin(a) * .95) for a in [math.pi / 2 - i * math.pi / 8 for i in range(9)]] + [(0, -.5)]
    elif ctype == 'truncate':
        prof = [(0, .4), (.75, .95), (.98, .9), (1, .3), (1, -.3), (.98, -.9), (.75, -.95), (0, -.4)]
    elif ctype == 'merge':
        prof = [(0, .6), (.9, .35), (1, 0), (.9, -.35), (0, -.6)]
    else:
        prof = [(0, .3), (1, 1), (.6, 0), (1, -1), (0, -.3)]
    cen = mb.add_v((0, 1.0, cz))
    ids = [mb.add_v((0, 1.0 + px * sweep, cz + pz * span)) for (px, pz) in prof]
    for i in range(len(ids) - 1):
        a = fin if i % 2 == 0 else shade(fin, -.15)
        mb.face([cen, ids[i], ids[i + 1]], [shade(fin, -.1), lerpc(a, edge, .55), lerpc(a, edge, .55)])

    # pectoral + pelvic pairs (angled back and out)
    def paired(u, zf, size, droop):
        top, bot, w = outline(u)
        z0 = lerp(bot, top, zf)
        for sx in (-1, 1):
            r = mb.add_v((sx * w * .95, u, z0 + size * .12))
            r2 = mb.add_v((sx * w * .95, u, z0 - size * .12))
            t1 = mb.add_v((sx * (w + size * .45), u + size * .95, z0 - droop * size))
            t2 = mb.add_v((sx * (w + size * .35), u + size * .75, z0 - droop * size - size * .35))
            fc = lerpc(fin, edge, .25)
            mb.face([r, t1, t2, r2], [fin, fc, fc, fin])
    paired(.21, .35, sp.get('pect', .1), .25)
    if sp.get('pelv'):
        pu, ps = sp['pelv']
        paired(pu, .05, ps, .6)

    # barbels: thin tapered triangles strips
    for (bu, bz, blen, droop, spread) in sp.get('barbels', []):
        top, bot, w = outline(bu)
        for sx in (-1, 1):
            p0 = Vector((sx * w * .8, bu, bz))
            d = Vector((sx * spread, .55, -droop)).normalized()
            pts = [p0 + d * (blen * t) + Vector((0, blen * t * t * .3, -blen * t * t * .15)) for t in (0, .33, .66, 1)]
            wdt = .006
            col = shade(hexl(sp['back']), -.1)
            for i in range(3):
                a = mb.add_v(pts[i] + Vector((0, 0, wdt * (1 - i / 3))))
                b = mb.add_v(pts[i] - Vector((0, 0, wdt * (1 - i / 3))))
                c = mb.add_v(pts[i + 1] + Vector((0, 0, wdt * (1 - (i + 1) / 3))))
                dd = mb.add_v(pts[i + 1] - Vector((0, 0, wdt * (1 - (i + 1) / 3))))
                mb.face([a, b, dd, c], col)

    # eyes: iris ring + pupil + highlight, sat just proud of the skin
    eu, ez, er = sp['eye']
    top, bot, w = outline(eu)
    cz = lerp(bot, top, .5) + ez
    iris = hexl('#c9a442') if sid not in ('keli', 'patin', 'baung') else hexl('#8a7a55')
    for sx in (-1, 1):
        x = sx * (sample(sp['w'], eu) * 0.98 + .003)
        c0 = mb.add_v((x * 1.01, eu, cz))
        ring = []
        for k in range(12):
            a = k / 12 * 2 * math.pi
            ring.append(mb.add_v((x, eu + math.cos(a) * er, cz + math.sin(a) * er)))
        inner = []
        for k in range(12):
            a = k / 12 * 2 * math.pi
            inner.append(mb.add_v((x * 1.005, eu + math.cos(a) * er * .55, cz + math.sin(a) * er * .55)))
        for k in range(12):
            k2 = (k + 1) % 12
            o = [ring[k], ring[k2], inner[k2], inner[k]] if sx > 0 else [ring[k2], ring[k], inner[k], inner[k2]]
            mb.face(o, iris)
            o2 = [c0, inner[k], inner[k2]] if sx > 0 else [c0, inner[k2], inner[k]]
            mb.face(o2, hexl('#0b0b0b'))
        hl = mb.add_v((x * 1.012, eu - er * .2, cz + er * .25))
        hl2 = mb.add_v((x * 1.012, eu - er * .05, cz + er * .3))
        hl3 = mb.add_v((x * 1.012, eu - er * .18, cz + er * .1))
        mb.face([hl, hl2, hl3], hexl('#f4f4f4'))

    return mb.to_object(sid, coll)


# ------------------------------------------------------ prawn and junk -----

def build_udang(coll):
    """Udang galah: carapace + rostrum, six-segment abdomen curling down,
    tail fan, walking legs, and the long blue claws it is famous for."""
    mb = MB()
    blue = hexl('#2e5f8f'); blue2 = hexl('#5d8fbf'); pale = hexl('#cfe0ec'); dark = hexl('#1d2833'); orange = hexl('#d88a3a')
    def ellipsoid(cx, cy, cz, rx, ry, rz, col_top, col_bot, n=10, m=8):
        idx = []
        for i in range(m + 1):
            th = math.pi * i / m
            row = []
            for j in range(n):
                ph = 2 * math.pi * j / n
                row.append(mb.add_v((cx + rx * math.sin(th) * math.cos(ph), cy + ry * math.cos(th), cz + rz * math.sin(th) * math.sin(ph))))
            idx.append(row)
        for i in range(m):
            for j in range(n):
                j2 = (j + 1) % n
                a, b, c, d = idx[i][j], idx[i][j2], idx[i + 1][j2], idx[i + 1][j]
                zc = (mb.v[a].z - cz) / max(rz, 1e-4)
                col = lerpc(col_bot, col_top, smooth(-.6, .6, zc))
                mb.face([a, d, c, b], col, smooth=True)
    # carapace (front) — prawn faces -Y like the fish (head at 0)
    ellipsoid(0, .2, 0, .085, .2, .085, blue, pale)
    # rostrum
    a = mb.add_v((0, -.05, .04)); b = mb.add_v((0, .06, .07)); c = mb.add_v((0, .06, .02))
    mb.face([a, b, c], orange)
    # abdomen segments, curling
    for i in range(6):
        y = .42 + i * .085
        z = -.012 * i * i * .4
        r = .075 - i * .006
        ellipsoid(0, y, z, r, .05, r * .95, blue2 if i % 2 else blue, pale, n=10, m=6)
    # tail fan
    cen = mb.add_v((0, .92, -.07))
    for k in range(5):
        ang = -.6 + k * .3
        p1 = mb.add_v((math.sin(ang) * .09, .92 + math.cos(ang) * .14, -.08))
        p2 = mb.add_v((math.sin(ang + .3) * .09, .92 + math.cos(ang + .3) * .14, -.08))
        mb.face([cen, p1, p2], blue)
    # walking legs
    for i in range(5):
        for sx in (-1, 1):
            y = .14 + i * .05
            p0 = Vector((sx * .06, y, -.04)); p1 = Vector((sx * .14, y + .02, -.12)); p2 = Vector((sx * .16, y + .04, -.2))
            for (q0, q1) in ((p0, p1), (p1, p2)):
                v0 = mb.add_v(q0 + Vector((0, -.005, 0))); v1 = mb.add_v(q0 + Vector((0, .005, 0)))
                v2 = mb.add_v(q1 + Vector((0, .005, 0))); v3 = mb.add_v(q1 + Vector((0, -.005, 0)))
                mb.face([v0, v1, v2, v3], orange)
    # big claws: two long segmented arms reaching forward
    for sx in (-1, 1):
        pts = [Vector((sx * .07, .05, -.02)), Vector((sx * .2, -.25, .0)), Vector((sx * .26, -.62, .02)), Vector((sx * .28, -.95, .03))]
        for i in range(3):
            q0, q1 = pts[i], pts[i + 1]
            r0 = .018 + i * .004; r1 = .018 + (i + 1) * .004
            ring0, ring1 = [], []
            for k in range(6):
                a = 2 * math.pi * k / 6
                ring0.append(mb.add_v(q0 + Vector((math.cos(a) * r0, 0, math.sin(a) * r0))))
                ring1.append(mb.add_v(q1 + Vector((math.cos(a) * r1, 0, math.sin(a) * r1))))
            for k in range(6):
                k2 = (k + 1) % 6
                mb.face([ring0[k], ring0[k2], ring1[k2], ring1[k]], blue if i < 2 else hexl('#1e4a7a'), smooth=True)
        # pincer fingers
        tipv = pts[3]
        for dz in (.02, -.02):
            a = mb.add_v(tipv + Vector((0, 0, dz)))
            b = mb.add_v(tipv + Vector((sx * .01, -.12, dz * .4)))
            c = mb.add_v(tipv + Vector((-sx * .01, -.1, dz * .2)))
            mb.face([a, b, c], orange)
    # antennae
    for sx in (-1, 1):
        prev = Vector((sx * .02, -.02, .03))
        for i in range(8):
            nxt = prev + Vector((sx * .03, -.13, .01 - i * .006))
            v0 = mb.add_v(prev); v1 = mb.add_v(prev + Vector((0, 0, .004))); v2 = mb.add_v(nxt + Vector((0, 0, .002))); v3 = mb.add_v(nxt)
            mb.face([v0, v1, v2, v3], orange)
            prev = nxt
    # eyes on stalks
    for sx in (-1, 1):
        ellipsoid(sx * .05, .02, .06, .018, .018, .018, dark, dark, n=6, m=4)
    ob = mb.to_object('udang_galah', coll)
    # put head at y=0 like the fish
    return ob


def build_junk(sid, coll):
    mb = MB()
    rnd = random.Random(len(sid))
    if sid == 'tin':
        rust = hexl('#8d5a34'); rust2 = hexl('#5a3b25'); metal = hexl('#9a9286'); label = hexl('#b8402e')
        n = 14
        for i in range(4):
            y0, y1 = i / 4, (i + 1) / 4
            for k in range(n):
                a0, a1 = 2 * math.pi * k / n, 2 * math.pi * (k + 1) / n
                r = .19 + (.01 if i in (0, 3) else 0)
                ids = [mb.add_v((math.cos(a0) * r, y0, math.sin(a0) * r)), mb.add_v((math.cos(a1) * r, y0, math.sin(a1) * r)),
                       mb.add_v((math.cos(a1) * r, y1, math.sin(a1) * r)), mb.add_v((math.cos(a0) * r, y1, math.sin(a0) * r))]
                c = label if i in (1, 2) and rnd.random() > .35 else (rust if rnd.random() > .4 else rust2)
                if rnd.random() > .8: c = metal
                mb.face(ids, c, smooth=True)
        for y in (0.0, 1.0):
            cc = mb.add_v((0, y, 0))
            for k in range(n):
                a0, a1 = 2 * math.pi * k / n, 2 * math.pi * (k + 1) / n
                o = [cc, mb.add_v((math.cos(a0) * .2, y, math.sin(a0) * .2)), mb.add_v((math.cos(a1) * .2, y, math.sin(a1) * .2))]
                mb.face(o if y else o[::-1], metal if y else rust2)
    elif sid == 'ranting':
        bark = hexl('#4a3a2c'); bark2 = hexl('#6d5a44')
        def branch(p0, p1, r0, r1):
            n = 7
            d = (p1 - p0)
            side = d.cross(Vector((0, 0, 1)))
            if side.length < 1e-4: side = Vector((1, 0, 0))
            side.normalize(); up = side.cross(d).normalized()
            ring0 = [mb.add_v(p0 + (side * math.cos(2 * math.pi * k / n) + up * math.sin(2 * math.pi * k / n)) * r0) for k in range(n)]
            ring1 = [mb.add_v(p1 + (side * math.cos(2 * math.pi * k / n) + up * math.sin(2 * math.pi * k / n)) * r1) for k in range(n)]
            for k in range(n):
                k2 = (k + 1) % n
                mb.face([ring0[k], ring0[k2], ring1[k2], ring1[k]], bark if k % 2 else bark2, smooth=True)
        pts = [Vector((0, 0, 0)), Vector((.02, .35, .03)), Vector((-.03, .7, .0)), Vector((.0, 1.0, .04))]
        for i in range(3):
            branch(pts[i], pts[i + 1], .045 - i * .01, .045 - (i + 1) * .01)
        branch(pts[1], pts[1] + Vector((.18, .22, .05)), .022, .006)
        branch(pts[2], pts[2] + Vector((-.15, .18, .08)), .018, .005)
        # a couple of dead leaves
        for (p, s) in ((pts[1] + Vector((.18, .22, .05)), .08), (pts[3], .07)):
            a = mb.add_v(p); b = mb.add_v(p + Vector((s * .5, s, .01))); c = mb.add_v(p + Vector((-s * .3, s * 1.2, .0)))
            mb.face([a, b, c], hexl('#7a6a3a'))
    elif sid == 'plastik':
        white = hexl('#d9e1e6'); grey = hexl('#aab4bb'); blue = hexl('#3f6fa8')
        n, m = 12, 8
        idx = []
        for i in range(m + 1):
            th = math.pi * i / m
            row = []
            for j in range(n):
                ph = 2 * math.pi * j / n
                r = .5 * (0.75 + .35 * vnoise(i * .9, j * .9, 7))
                row.append(mb.add_v((r * .55 * math.sin(th) * math.cos(ph), .5 + r * math.cos(th), r * .35 * math.sin(th) * math.sin(ph))))
            idx.append(row)
        for i in range(m):
            for j in range(n):
                j2 = (j + 1) % n
                c = white if rnd.random() > .3 else grey
                if 3 < i < 6 and 2 < j < 6: c = blue
                mb.face([idx[i][j], idx[i + 1][j], idx[i + 1][j2], idx[i][j2]], c)
        # handles
        for sx in (-1, 1):
            a = mb.add_v((sx * .12, -.02, 0)); b = mb.add_v((sx * .2, -.12, 0)); c = mb.add_v((sx * .06, -.1, 0))
            mb.face([a, b, c], white)
    elif sid == 'boot':
        rub = hexl('#2a2622'); rub2 = hexl('#3d3631'); sole = hexl('#171513'); mud = hexl('#5d4a37')
        def boxp(c, s, col):
            x, y, z = c; sx, sy, sz = s
            v = [mb.add_v((x + dx * sx, y + dy * sy, z + dz * sz)) for dz in (-1, 1) for dy in (-1, 1) for dx in (-1, 1)]
            F = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
            for f in F:
                mb.face([v[i] for i in f], col)
        # foot (along +Y), shaft rising at the heel end
        boxp((0, .45, .1), (.15, .45, .1), rub)
        boxp((0, .08, .32), (.14, .1, .25), rub2)
        boxp((0, .45, -.02), (.16, .48, .03), sole)
        boxp((0, .78, .12), (.13, .12, .08), mud)
    return mb.to_object(sid, coll)


# -------------------------------------------------------------- top level ---


def fish_collection():
    c = bpy.data.collections.get("Fish")
    if c is None:
        c = bpy.data.collections.new("Fish")
        bpy.context.scene.collection.children.link(c)
    return c


def build_all():
    coll = fish_collection()
    obs = []
    for sid, sp in S.items():
        obs.append(build_fish(sid, sp, coll))
    obs.append(build_udang(coll))
    for j in ('boot', 'tin', 'plastik', 'ranting'):
        obs.append(build_junk(j, coll))
    for i, o in enumerate(obs):
        o.location = ((i % 6) * 1.6, (i // 6) * 1.8, 0)
    return obs


def export_all_fish():
    os.makedirs(OUT, exist_ok=True)
    out = []
    for o in fish_collection().objects:
        bpy.ops.object.select_all(action='DESELECT')
        loc = o.location.copy()
        o.location = (0, 0, 0)
        o.select_set(True)
        bpy.context.view_layer.objects.active = o
        p = os.path.join(OUT, o.name + ".fbx")
        bpy.ops.export_scene.fbx(filepath=p, use_selection=True, object_types={'MESH'},
                                 apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y',
                                 bake_space_transform=True, mesh_smooth_type='FACE', colors_type='SRGB',
                                 add_leaf_bones=False, bake_anim=False)
        o.location = loc
        out.append(p)
    bpy.ops.object.select_all(action='DESELECT')
    return out

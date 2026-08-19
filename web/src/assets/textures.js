/**
 * Procedural texture synthesis.
 *
 * Everything the renderer needs is generated into an OffscreenCanvas at boot
 * from a seed and a palette: no image files, no loading screen, no CORS, and a
 * whole location can be re-skinned by editing the palette in data/spots.js.
 *
 * All maps are generated tileable (the noise wraps at the texture period), so
 * they repeat across a 400 m lake without visible seams.
 *
 * NOTE ON THE PIPELINE: this is a *generative* pipeline, not a call out to a
 * hosted image model — a diffusion service would mean network dependency,
 * per-asset latency and non-determinism, none of which a real-time game wants.
 * `generateAll()` is the single seam where such a service could be swapped in:
 * it returns a map of named canvases, so an implementation that fetches images
 * instead of synthesising them is a drop-in replacement.
 */

import * as THREE from 'three';
import {
  fbm2, ridged2, warped2, worley2, tileable2,
  clamp01, smoothstep, ramp, hexToRgb, mixRgb, shade, mix,
} from './noise.js';

/**
 * Prefer a real <canvas> whenever there is a document.
 *
 * OffscreenCanvas is tempting here, but it has no `toDataURL`, and the fish
 * sprites have to become <img> sources for the catch card and the record book.
 * We never synthesise off the main thread, so the offscreen variant buys
 * nothing and silently breaks the 2D art path.
 */
function makeCanvas(w, h) {
  if (typeof document !== 'undefined') {
    const c = document.createElement('canvas');
    c.width = w; c.height = h;
    return c;
  }
  if (typeof OffscreenCanvas !== 'undefined') return new OffscreenCanvas(w, h);
  throw new Error('no canvas implementation available');
}

/**
 * Per-pixel generator helper. `fn(x, y, u, v)` returns {r,g,b,a?} in 0..255.
 */
function synth(size, fn) {
  const canvas = makeCanvas(size, size);
  const ctx = canvas.getContext('2d', { willReadFrequently: true });
  const img = ctx.createImageData(size, size);
  const d = img.data;
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const i = (y * size + x) * 4;
      const c = fn(x, y, x / size, y / size);
      d[i] = c.r; d[i + 1] = c.g; d[i + 2] = c.b; d[i + 3] = c.a ?? 255;
    }
  }
  ctx.putImageData(img, 0, 0);
  return canvas;
}

/**
 * Derive a tangent-space normal map from a height function by central
 * differences. Doing this analytically rather than sampling a rendered
 * heightmap keeps the derivatives exact and the surface free of stair-stepping.
 */
function normalFromHeight(size, heightFn, strength = 2.2) {
  const e = 1 / size;
  return synth(size, (x, y, u, v) => {
    const hL = heightFn(u - e, v), hR = heightFn(u + e, v);
    const hD = heightFn(u, v - e), hU = heightFn(u, v + e);
    let nx = (hL - hR) * strength;
    let ny = (hD - hU) * strength;
    const nz = 1;
    const len = Math.hypot(nx, ny, nz) || 1;
    nx /= len; ny /= len;
    const nzn = nz / len;
    return {
      r: (nx * 0.5 + 0.5) * 255,
      g: (ny * 0.5 + 0.5) * 255,
      b: (nzn * 0.5 + 0.5) * 255,
    };
  });
}

/* --- water ----------------------------------------------------------------- */

/**
 * Water surface normal map. Two scales of warped noise: broad swell shapes plus
 * fine capillary ripple. Scrolled at different speeds in the shader, which is
 * what sells motion without moving a single vertex.
 */
export function waterNormals(size = 512, seed = 7) {
  const height = (u, v) => {
    const big = warped2(u * 4, v * 4, { octaves: 4, seed, period: 4, warp: 0.6 });
    const fine = fbm2(u * 13, v * 13, { octaves: 3, seed: seed + 31, period: 13 });
    return big * 0.72 + fine * 0.28;
  };
  return normalFromHeight(size, height, 2.6);
}

/**
 * Caustics: the shifting light net on a shallow bottom. Worley edge distance
 * raised to a high power gives exactly the thin bright filaments you get from
 * light refracting through a wavy surface.
 */
export function causticsTexture(size = 256, seed = 19) {
  return synth(size, (x, y, u, v) => {
    const w1 = worley2(u * 6, v * 6, { cells: 6, seed });
    const w2 = worley2(u * 9 + 3.1, v * 9 + 1.7, { cells: 9, seed: seed + 5 });
    const net = Math.pow(clamp01(1 - w1.edge * 3.2), 5) + Math.pow(clamp01(1 - w2.edge * 3.6), 6) * 0.6;
    const c = clamp01(net) * 255;
    return { r: c, g: c, b: c };
  });
}

/** Foam: soft cellular clumps, used along the shoreline and behind splashes. */
export function foamTexture(size = 256, seed = 41) {
  return synth(size, (x, y, u, v) => {
    const n = fbm2(u * 8, v * 8, { octaves: 4, seed, period: 8 });
    const w = worley2(u * 7, v * 7, { cells: 7, seed: seed + 3 });
    const a = clamp01(smoothstep(0.42, 0.78, n) * (1 - w.f1 * 0.8)) * 255;
    return { r: 255, g: 255, b: 255, a };
  });
}

/* --- terrain --------------------------------------------------------------- */

/**
 * Lake bed / bank texture. Blends sand, gravel and weed by height so one map
 * covers the whole shoreline gradient.
 */
export function bedTexture(size = 512, palette, seed = 3) {
  const sand = hexToRgb(palette.sand);
  const grass = hexToRgb(palette.grass);
  const deep = hexToRgb(palette.deep);
  return synth(size, (x, y, u, v) => {
    const grain = fbm2(u * 26, v * 26, { octaves: 4, seed, period: 26 });
    const patch = warped2(u * 3.2, v * 3.2, { octaves: 4, seed: seed + 11, period: 3.2, warp: 0.7 });
    const pebbles = worley2(u * 18, v * 18, { cells: 18, seed: seed + 21 });
    const rock = Math.pow(clamp01(1 - pebbles.edge * 2.4), 3) * 0.35;

    let c = mixRgb(sand, grass, smoothstep(0.45, 0.78, patch));
    c = mixRgb(c, deep, smoothstep(0.62, 0.95, patch) * 0.45);
    c = shade(c, (grain - 0.5) * 0.30);
    c = shade(c, rock * 0.5);
    return c;
  });
}

/** Matching normal map so the bed catches the sun at a grazing angle. */
export function bedNormals(size = 512, seed = 3) {
  const height = (u, v) => {
    const grain = fbm2(u * 26, v * 26, { octaves: 4, seed, period: 26 });
    const pebbles = worley2(u * 18, v * 18, { cells: 18, seed: seed + 21 });
    return grain * 0.6 + Math.pow(clamp01(1 - pebbles.edge * 2.4), 3) * 0.4;
  };
  return normalFromHeight(size, height, 1.6);
}

/** Bank / grass texture for the near shore the player stands on. */
export function bankTexture(size = 512, palette, seed = 13) {
  const grass = hexToRgb(palette.grass);
  const sand = hexToRgb(palette.sand);
  const trees = hexToRgb(palette.trees);
  return synth(size, (x, y, u, v) => {
    const blades = ridged2(u * 40, v * 40, { octaves: 3, seed, period: 40 });
    const clumps = warped2(u * 4, v * 4, { octaves: 4, seed: seed + 7, period: 4, warp: 0.8 });
    const dirt = fbm2(u * 9, v * 9, { octaves: 3, seed: seed + 17, period: 9 });

    let c = mixRgb(grass, trees, smoothstep(0.40, 0.85, clumps) * 0.7);
    c = mixRgb(c, sand, smoothstep(0.55, 0.90, dirt) * 0.45);
    c = shade(c, (blades - 0.5) * 0.26);
    return c;
  });
}

/* --- sky ------------------------------------------------------------------- */

/**
 * Sky dome texture, generated per weather/time state.
 * v = 0 is the horizon, v = 1 the zenith.
 *
 * @param {object} opts { horizon, zenith, cloud (0..1), light (0..1), seed }
 */
export function skyTexture(size = 512, opts = {}) {
  const {
    horizon = '#dfe9ee', zenith = '#5f8fbe', cloudAmount = 0.4,
    light = 1, seed = 91, sun = '#ffe6bd',
  } = opts;
  const hz = hexToRgb(horizon);
  const zn = hexToRgb(zenith);
  const sunC = hexToRgb(sun);

  return synth(size, (x, y, u, v) => {
    // Gradient is nonlinear: real skies deepen fast just above the horizon.
    const t = Math.pow(v, 0.65);
    let c = mixRgb(hz, zn, t);

    // Sun glow, parked at a fixed azimuth; the scene light matches it.
    const sunU = 0.30, sunV = 0.22;
    const d = Math.hypot((u - sunU) * 2.2, v - sunV);
    c = mixRgb(c, sunC, clamp01(1 - d * 3.2) ** 3 * light * 0.85);

    // Clouds: warped FBM, flattened toward the horizon so they read as a deck.
    const perspective = mix(3.4, 1.0, v);
    const n = warped2(u * 5 * perspective, v * 5, { octaves: 5, seed, warp: 0.9, period: 0 });
    const cover = smoothstep(0.62 - cloudAmount * 0.34, 0.86 - cloudAmount * 0.18, n);
    const cloudLit = shade({ r: 244, g: 246, b: 248 }, -(1 - light) * 0.45 - (1 - n) * 0.25);
    c = mixRgb(c, cloudLit, cover * clamp01(cloudAmount * 1.35) * smoothstep(0.02, 0.25, v));

    return shade(c, -(1 - light) * 0.35);
  });
}

/** Star field for the night sky, composited over the dark gradient. */
export function starTexture(size = 512, seed = 77) {
  return synth(size, (x, y, u, v) => {
    const n = tileable2(u * 220, v * 220, 220, seed);
    const bright = Math.pow(clamp01((n - 0.972) * 36), 1.4);
    const twinkle = tileable2(u * 90, v * 90, 90, seed + 5);
    const a = bright * (0.55 + twinkle * 0.45) * smoothstep(0.05, 0.4, v) * 255;
    return { r: 255, g: 250, b: 240, a };
  });
}

/* --- foliage --------------------------------------------------------------- */

/**
 * A tree billboard drawn with recursive branching plus noisy canopy blobs.
 * Cheap, and from 30 m across water it reads better than a low-poly mesh.
 */
export function treeBillboard(size = 256, palette, seed = 5) {
  const canvas = makeCanvas(size, size);
  const ctx = canvas.getContext('2d');
  ctx.clearRect(0, 0, size, size);

  let s = seed >>> 0;
  const rnd = () => {
    s = (Math.imul(s, 1664525) + 1013904223) >>> 0;
    return s / 4294967296;
  };

  const trunk = shade(hexToRgb(palette.trees), -0.45);
  const leafDark = shade(hexToRgb(palette.trees), -0.18);
  const leafLit = shade(hexToRgb(palette.trees), 0.34);

  // Trunk and branches.
  ctx.strokeStyle = `rgb(${trunk.r|0},${trunk.g|0},${trunk.b|0})`;
  ctx.lineCap = 'round';
  const branch = (x, y, angle, len, width, depth) => {
    if (depth === 0 || len < 4) return;
    const x2 = x + Math.cos(angle) * len;
    const y2 = y + Math.sin(angle) * len;
    ctx.lineWidth = width;
    ctx.beginPath(); ctx.moveTo(x, y); ctx.lineTo(x2, y2); ctx.stroke();
    const spread = 0.42 + rnd() * 0.35;
    branch(x2, y2, angle - spread, len * (0.62 + rnd() * 0.16), width * 0.66, depth - 1);
    branch(x2, y2, angle + spread, len * (0.62 + rnd() * 0.16), width * 0.66, depth - 1);
    if (rnd() > 0.55) branch(x2, y2, angle + (rnd() - 0.5) * 0.4, len * 0.5, width * 0.5, depth - 1);
  };
  branch(size * 0.5, size * 0.98, -Math.PI / 2, size * 0.22, size * 0.045, 6);

  // Canopy: overlapping soft blobs, lit from the upper left.
  for (let i = 0; i < 150; i++) {
    const a = rnd() * Math.PI * 2;
    const r = Math.pow(rnd(), 0.55) * size * 0.34;
    const cx = size * 0.5 + Math.cos(a) * r;
    const cy = size * 0.40 + Math.sin(a) * r * 0.82;
    const rad = size * (0.045 + rnd() * 0.07);
    const lit = clamp01(1 - (cy / size) - (cx / size) * 0.25 + rnd() * 0.3);
    const c = mixRgb(leafDark, leafLit, lit);
    ctx.fillStyle = `rgba(${c.r|0},${c.g|0},${c.b|0},${0.55 + rnd() * 0.4})`;
    ctx.beginPath(); ctx.arc(cx, cy, rad, 0, Math.PI * 2); ctx.fill();
  }
  return canvas;
}

/** Reed / weed billboard for the margins. */
export function reedBillboard(size = 128, palette, seed = 9) {
  const canvas = makeCanvas(size, size);
  const ctx = canvas.getContext('2d');
  let s = seed >>> 0;
  const rnd = () => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s / 4294967296; };

  const base = hexToRgb(palette.grass);
  for (let i = 0; i < 26; i++) {
    const x = size * (0.1 + rnd() * 0.8);
    const h = size * (0.45 + rnd() * 0.5);
    const lean = (rnd() - 0.5) * size * 0.22;
    const c = shade(base, (rnd() - 0.45) * 0.5);
    ctx.strokeStyle = `rgba(${c.r|0},${c.g|0},${c.b|0},0.95)`;
    ctx.lineWidth = 1.2 + rnd() * 2.2;
    ctx.lineCap = 'round';
    ctx.beginPath();
    ctx.moveTo(x, size);
    ctx.quadraticCurveTo(x + lean * 0.4, size - h * 0.55, x + lean, size - h);
    ctx.stroke();
  }
  return canvas;
}

/* --- three.js glue --------------------------------------------------------- */

export function toTexture(canvas, {
  repeat = 1, srgb = false, aniso = 8, renderer = null,
} = {}) {
  const tex = new THREE.CanvasTexture(canvas);
  tex.wrapS = tex.wrapT = THREE.RepeatWrapping;
  tex.repeat.set(repeat, repeat);
  tex.colorSpace = srgb ? THREE.SRGBColorSpace : THREE.NoColorSpace;
  tex.anisotropy = renderer ? Math.min(aniso, renderer.capabilities.getMaxAnisotropy()) : aniso;
  tex.needsUpdate = true;
  return tex;
}

/**
 * Build the whole texture set for a location.
 *
 * This is the single seam of the asset pipeline: swap this function for one
 * that fetches generated images from a model service and nothing else in the
 * renderer has to change.
 */
export function generateAll(spot, { renderer = null, quality = 'high' } = {}) {
  const S = quality === 'low' ? 0.5 : quality === 'medium' ? 0.75 : 1;
  const px = (n) => Math.max(64, Math.round(n * S));
  const seed = spot.id.split('').reduce((a, c) => a + c.charCodeAt(0), 0);

  const canvases = {
    waterNormals: waterNormals(px(512), seed + 7),
    caustics: causticsTexture(px(256), seed + 19),
    foam: foamTexture(px(256), seed + 41),
    bed: bedTexture(px(512), spot.palette, seed + 3),
    bedNormals: bedNormals(px(512), seed + 3),
    bank: bankTexture(px(512), spot.palette, seed + 13),
    tree: treeBillboard(px(256), spot.palette, seed + 5),
    reed: reedBillboard(px(128), spot.palette, seed + 9),
    stars: starTexture(px(512), seed + 77),
  };

  return {
    canvases,
    textures: {
      waterNormals: toTexture(canvases.waterNormals, { repeat: 1, renderer }),
      caustics: toTexture(canvases.caustics, { repeat: 1, renderer }),
      foam: toTexture(canvases.foam, { repeat: 1, renderer }),
      bed: toTexture(canvases.bed, { repeat: 14, srgb: true, renderer }),
      bedNormals: toTexture(canvases.bedNormals, { repeat: 14, renderer }),
      bank: toTexture(canvases.bank, { repeat: 22, srgb: true, renderer }),
      tree: toTexture(canvases.tree, { repeat: 1, srgb: true, renderer }),
      reed: toTexture(canvases.reed, { repeat: 1, srgb: true, renderer }),
      stars: toTexture(canvases.stars, { repeat: 1, srgb: true, renderer }),
    },
  };
}

export { makeCanvas, synth, normalFromHeight };

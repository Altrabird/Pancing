/**
 * Seeded noise kit — the substrate of the whole asset pipeline.
 *
 * Every texture, every fish, every tree in this game is a function of an
 * integer seed. Nothing is loaded from disk. That means the entire art budget
 * is a few kilobytes of code, the game has no asset-loading state to manage,
 * and any species or location can be re-skinned by changing one number.
 *
 * Value noise rather than simplex: it is patent-free, trivially seedable, and
 * once you stack five octaves of it with domain warping the visual difference
 * is nil for texture work.
 */

/* --- hashing --------------------------------------------------------------- */

function hash2(x, y, seed) {
  let h = seed ^ Math.imul(x, 374761393) ^ Math.imul(y, 668265263);
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  return ((h ^ (h >>> 16)) >>> 0) / 4294967296;
}

function hash3(x, y, z, seed) {
  let h = seed ^ Math.imul(x, 374761393) ^ Math.imul(y, 668265263) ^ Math.imul(z, 2147483647);
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  return ((h ^ (h >>> 16)) >>> 0) / 4294967296;
}

const fade = (t) => t * t * t * (t * (t * 6 - 15) + 10);
const mix = (a, b, t) => a + (b - a) * t;

/* --- value noise ----------------------------------------------------------- */

export function value2(x, y, seed = 0) {
  const xi = Math.floor(x), yi = Math.floor(y);
  const xf = x - xi, yf = y - yi;
  const u = fade(xf), v = fade(yf);
  return mix(
    mix(hash2(xi, yi, seed), hash2(xi + 1, yi, seed), u),
    mix(hash2(xi, yi + 1, seed), hash2(xi + 1, yi + 1, seed), u),
    v,
  );
}

export function value3(x, y, z, seed = 0) {
  const xi = Math.floor(x), yi = Math.floor(y), zi = Math.floor(z);
  const xf = x - xi, yf = y - yi, zf = z - zi;
  const u = fade(xf), v = fade(yf), w = fade(zf);
  const c = (dx, dy, dz) => hash3(xi + dx, yi + dy, zi + dz, seed);
  return mix(
    mix(mix(c(0, 0, 0), c(1, 0, 0), u), mix(c(0, 1, 0), c(1, 1, 0), u), v),
    mix(mix(c(0, 0, 1), c(1, 0, 1), u), mix(c(0, 1, 1), c(1, 1, 1), u), v),
    w,
  );
}

/**
 * Tileable value noise. Wraps by hashing coordinates modulo the period, which
 * is what lets every texture below repeat seamlessly across the lake.
 */
export function tileable2(x, y, period, seed = 0) {
  const wrap = (n) => ((n % period) + period) % period;
  const xi = Math.floor(x), yi = Math.floor(y);
  const xf = x - xi, yf = y - yi;
  const u = fade(xf), v = fade(yf);
  const h = (dx, dy) => hash2(wrap(xi + dx), wrap(yi + dy), seed);
  return mix(mix(h(0, 0), h(1, 0), u), mix(h(0, 1), h(1, 1), u), v);
}

/* --- fractal composition --------------------------------------------------- */

export function fbm2(x, y, { octaves = 5, lacunarity = 2.0, gain = 0.5, seed = 0, period = 0 } = {}) {
  let amp = 1, freq = 1, sum = 0, norm = 0;
  for (let i = 0; i < octaves; i++) {
    const n = period
      ? tileable2(x * freq, y * freq, period * freq, seed + i * 1013)
      : value2(x * freq, y * freq, seed + i * 1013);
    sum += n * amp;
    norm += amp;
    amp *= gain;
    freq *= lacunarity;
  }
  return sum / norm;
}

/** Ridged multifractal — sharp creases. Used for rock, bark and wave crests. */
export function ridged2(x, y, { octaves = 5, lacunarity = 2.05, gain = 0.5, seed = 0, period = 0 } = {}) {
  let amp = 1, freq = 1, sum = 0, norm = 0;
  for (let i = 0; i < octaves; i++) {
    const n = period
      ? tileable2(x * freq, y * freq, period * freq, seed + i * 7919)
      : value2(x * freq, y * freq, seed + i * 7919);
    const r = 1 - Math.abs(n * 2 - 1);
    sum += r * r * amp;
    norm += amp;
    amp *= gain;
    freq *= lacunarity;
  }
  return sum / norm;
}

/**
 * Domain-warped FBM. Feeding noise back into its own coordinates is the single
 * cheapest way to make procedural texture stop looking procedural — it turns
 * bland cloud into the swirled, organic structure real materials have.
 */
export function warped2(x, y, opts = {}) {
  const { warp = 0.55, seed = 0 } = opts;
  const qx = fbm2(x, y, { ...opts, seed: seed + 1 });
  const qy = fbm2(x + 5.2, y + 1.3, { ...opts, seed: seed + 2 });
  return fbm2(x + warp * qx * 4, y + warp * qy * 4, { ...opts, seed: seed + 3 });
}

/**
 * Worley / cellular noise. Returns distance to the nearest feature point.
 * Drives caustics, scale patterns, gravel and foam cells.
 */
export function worley2(x, y, { cells = 8, seed = 0, tile = true } = {}) {
  const xi = Math.floor(x), yi = Math.floor(y);
  let best = Infinity, second = Infinity;
  for (let dy = -1; dy <= 1; dy++) {
    for (let dx = -1; dx <= 1; dx++) {
      let cx = xi + dx, cy = yi + dy;
      if (tile) { cx = ((cx % cells) + cells) % cells; cy = ((cy % cells) + cells) % cells; }
      const px = xi + dx + hash2(cx, cy, seed);
      const py = yi + dy + hash2(cx, cy, seed + 977);
      const d = Math.hypot(px - x, py - y);
      if (d < best) { second = best; best = d; }
      else if (d < second) { second = d; }
    }
  }
  return { f1: best, f2: second, edge: second - best };
}

/* --- curves and colour ----------------------------------------------------- */

export const clamp01 = (x) => (x < 0 ? 0 : x > 1 ? 1 : x);

export function smoothstep(a, b, x) {
  const t = clamp01((x - a) / (b - a));
  return t * t * (3 - 2 * t);
}

/** Catmull-Rom through a list of scalars; used for fish body profiles. */
export function spline(points, t) {
  const n = points.length - 1;
  const scaled = clamp01(t) * n;
  const i = Math.min(Math.floor(scaled), n - 1);
  const f = scaled - i;
  const p0 = points[Math.max(i - 1, 0)];
  const p1 = points[i];
  const p2 = points[Math.min(i + 1, n)];
  const p3 = points[Math.min(i + 2, n)];
  return 0.5 * (
    2 * p1 +
    (-p0 + p2) * f +
    (2 * p0 - 5 * p1 + 4 * p2 - p3) * f * f +
    (-p0 + 3 * p1 - 3 * p2 + p3) * f * f * f
  );
}

export function hexToRgb(hex) {
  const h = hex.replace('#', '');
  const n = parseInt(h.length === 3 ? h.split('').map((c) => c + c).join('') : h, 16);
  return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
}

export function rgbToHex({ r, g, b }) {
  const c = (v) => Math.max(0, Math.min(255, Math.round(v))).toString(16).padStart(2, '0');
  return `#${c(r)}${c(g)}${c(b)}`;
}

export function mixRgb(a, b, t) {
  return { r: mix(a.r, b.r, t), g: mix(a.g, b.g, t), b: mix(a.b, b.b, t) };
}

/** Sample a palette of hex colours as a continuous ramp. */
export function ramp(palette, t) {
  const rgb = palette.map(hexToRgb);
  const n = rgb.length - 1;
  const s = clamp01(t) * n;
  const i = Math.min(Math.floor(s), n - 1);
  return mixRgb(rgb[i], rgb[i + 1], s - i);
}

export function shade(rgb, amount) {
  return amount >= 0
    ? mixRgb(rgb, { r: 255, g: 255, b: 255 }, amount)
    : mixRgb(rgb, { r: 0, g: 0, b: 0 }, -amount);
}

export { mix };

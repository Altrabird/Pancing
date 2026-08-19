/**
 * Fishing locations.
 *
 *  depthAt(u, v)   returns normalised depth 0..1 across the castable water,
 *                  where u is distance from shore and v is lateral offset.
 *                  The renderer uses the same function to shape the lake bed,
 *                  so what you see is genuinely what you are fishing.
 *  structure       snag points; a fish that reaches one breaks you off.
 *  pool            species ids available here, plus a spot-local weight tweak.
 *  palette         drives the procedural environment generator.
 */

export const SPOTS = [
  {
    id: 'kolam', name: 'Kolam Kampung', tagline: 'Air tenang, ikan lapar, tiada tekanan.',
    level: 1, entryFee: 0, maxDepth: 3.2, waterClarity: 0.55, current: 0.05,
    windBase: 0.22, snagDensity: 0.18,
    pool: {
      tilapia: 1.4, keli: 1.2, lampam: 1.1, puyu: 1.3, haruan: 0.5,
      boot: 1.0, tin: 1.0, plastik: 0.8, ranting: 0.9,
    },
    palette: {
      water: '#3f6b5e', deep: '#12302c', shallow: '#7fae94', foam: '#e8f2ea',
      sand: '#8d7f5e', grass: '#5c6f43', sky: ['#8fb8d8', '#dfe9ee'],
      trees: '#3f5233',
    },
    depthAt: (u, v) => {
      // Gentle dish: deepest in the middle of the pond, shelving to the banks.
      const r = Math.hypot(u - 0.55, v * 0.9);
      return clamp01(1.05 - r * 1.5 + Math.sin(v * 7.0) * 0.03);
    },
    structure: [
      { u: 0.32, v: -0.45, r: 0.10, kind: 'reeds' },
      { u: 0.58, v: 0.38, r: 0.12, kind: 'timber' },
      { u: 0.78, v: -0.12, r: 0.09, kind: 'reeds' },
    ],
  },
  {
    id: 'sungai', name: 'Sungai Berbatu', tagline: 'Arus deras. Ikan kuat, batuan tajam.',
    level: 4, entryFee: 40, maxDepth: 5.0, waterClarity: 0.78, current: 0.55,
    windBase: 0.35, snagDensity: 0.52,
    pool: {
      sebarau: 1.6, baung: 1.3, lampam: 0.9, jelawat: 1.2, haruan: 0.8,
      keli: 0.7, udang_galah: 1.1, kelah: 0.6,
      ranting: 1.4, tin: 0.8, boot: 0.6,
    },
    palette: {
      water: '#2f6f78', deep: '#0d2b31', shallow: '#79b6b2', foam: '#f2f7f6',
      sand: '#9a927c', grass: '#4c6b3c', sky: ['#7fa9cc', '#e6eef2'],
      trees: '#33472c',
    },
    depthAt: (u, v) => {
      // Channel: a scoured trough offset to one side, shallow gravel bar opposite.
      const channel = Math.exp(-Math.pow((v + 0.18) / 0.34, 2));
      return clamp01(0.18 + u * 0.35 + channel * 0.62 - Math.abs(v) * 0.15);
    },
    structure: [
      { u: 0.24, v: 0.40, r: 0.11, kind: 'rock' },
      { u: 0.46, v: -0.52, r: 0.13, kind: 'rock' },
      { u: 0.66, v: 0.14, r: 0.10, kind: 'timber' },
      { u: 0.86, v: -0.34, r: 0.12, kind: 'rock' },
    ],
  },
  {
    id: 'tasik', name: 'Tasik Dalam', tagline: 'Air hitam. Sesuatu yang besar tinggal di bawah.',
    level: 8, entryFee: 180, maxDepth: 9.5, waterClarity: 0.32, current: 0.12,
    windBase: 0.48, snagDensity: 0.36,
    pool: {
      toman: 1.5, patin: 1.4, belida: 1.3, haruan: 1.0, baung: 1.1,
      jelawat: 0.9, keli: 0.6, kelah: 0.8, udang_galah: 0.7,
      plastik: 1.1, ranting: 1.0, boot: 0.7,
    },
    palette: {
      water: '#25454f', deep: '#07171d', shallow: '#5a8894', foam: '#dfeaee',
      sand: '#6f6552', grass: '#3d5334', sky: ['#5d7f9e', '#c9d8e0'],
      trees: '#26361f',
    },
    depthAt: (u, v) => {
      // Steep drop-off close to shore, then a deep flat basin.
      const shelf = smoothstep(0.06, 0.30, u);
      return clamp01(shelf * 0.92 + u * 0.10 - Math.abs(v) * 0.06);
    },
    structure: [
      { u: 0.18, v: -0.30, r: 0.10, kind: 'timber' },
      { u: 0.40, v: 0.48, r: 0.14, kind: 'timber' },
      { u: 0.72, v: -0.20, r: 0.12, kind: 'weed' },
      { u: 0.90, v: 0.30, r: 0.13, kind: 'weed' },
    ],
  },
];

export const SPOTS_BY_ID = Object.fromEntries(SPOTS.map((s) => [s.id, s]));

/* --- time & weather ------------------------------------------------------- */

export const TIME_PHASES = [
  { id: 'dawn',  label: 'Subuh',  from: 5.0,  to: 8.0,  sun: 0.18, warm: 0.95 },
  { id: 'day',   label: 'Siang',  from: 8.0,  to: 17.0, sun: 1.00, warm: 0.25 },
  { id: 'dusk',  label: 'Senja',  from: 17.0, to: 19.5, sun: 0.22, warm: 1.00 },
  { id: 'night', label: 'Malam',  from: 19.5, to: 5.0,  sun: 0.05, warm: 0.10 },
];

export function phaseForHour(hour) {
  const h = ((hour % 24) + 24) % 24;
  for (const p of TIME_PHASES) {
    if (p.from < p.to ? h >= p.from && h < p.to : h >= p.from || h < p.to) return p;
  }
  return TIME_PHASES[1];
}

export const WEATHER = [
  { id: 'clear',  label: 'Cerah',   chance: 0.40, wind: 0.6, chop: 0.5, light: 1.00, rain: 0.0 },
  { id: 'cloudy', label: 'Mendung', chance: 0.32, wind: 0.9, chop: 0.8, light: 0.72, rain: 0.0 },
  { id: 'rain',   label: 'Hujan',   chance: 0.20, wind: 1.2, chop: 1.2, light: 0.52, rain: 0.6 },
  { id: 'storm',  label: 'Ribut',   chance: 0.08, wind: 2.0, chop: 2.0, light: 0.35, rain: 1.0 },
];

export const WEATHER_BY_ID = Object.fromEntries(WEATHER.map((w) => [w.id, w]));

/* --- small shared helpers ------------------------------------------------- */

function clamp01(x) { return x < 0 ? 0 : x > 1 ? 1 : x; }

function smoothstep(a, b, x) {
  const t = clamp01((x - a) / (b - a));
  return t * t * (3 - 2 * t);
}

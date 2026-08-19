/**
 * Seeded deterministic RNG (sfc32 + a string hasher).
 *
 * The whole simulation runs off explicit RNG streams rather than Math.random,
 * so a session can be replayed exactly. The headless test harness relies on
 * this to compare catch-table distributions across runs.
 */

export function hashSeed(str) {
  let h = 2166136261 >>> 0;
  const s = String(str);
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 16777619) >>> 0;
  }
  return h >>> 0;
}

export class RNG {
  constructor(seed = Date.now()) {
    const s = typeof seed === 'number' ? seed >>> 0 : hashSeed(seed);
    // Four decorrelated words from one seed, via splitmix32.
    let x = s;
    const next = () => {
      x = (x + 0x9e3779b9) >>> 0;
      let z = x;
      z = Math.imul(z ^ (z >>> 16), 0x21f0aaad) >>> 0;
      z = Math.imul(z ^ (z >>> 15), 0x735a2d97) >>> 0;
      return (z ^ (z >>> 15)) >>> 0;
    };
    this.a = next(); this.b = next(); this.c = next(); this.d = next();
    this.seed = s;
  }

  /** Uniform in [0, 1). */
  next() {
    const t = (this.a + this.b | 0) + this.d | 0;
    this.d = this.d + 1 | 0;
    this.a = this.b ^ (this.b >>> 9);
    this.b = this.c + (this.c << 3) | 0;
    this.c = (this.c << 21) | (this.c >>> 11);
    this.c = this.c + t | 0;
    return (t >>> 0) / 4294967296;
  }

  float(min = 0, max = 1) { return min + this.next() * (max - min); }

  int(min, max) { return Math.floor(this.float(min, max + 1)); }

  bool(p = 0.5) { return this.next() < p; }

  pick(arr) { return arr[Math.floor(this.next() * arr.length)]; }

  /** Box-Muller, one value per call (the spare is intentionally discarded so
   *  the stream advances by a fixed amount and stays replay-stable). */
  normal(mean = 0, sd = 1) {
    let u = 0;
    while (u === 0) u = this.next();
    const v = this.next();
    return mean + sd * Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * v);
  }

  /** Normal clipped to [min, max] by resampling, with a bailout to clamping. */
  normalClamped(mean, sd, min, max, tries = 12) {
    for (let i = 0; i < tries; i++) {
      const x = this.normal(mean, sd);
      if (x >= min && x <= max) return x;
    }
    return Math.min(max, Math.max(min, this.normal(mean, sd)));
  }

  /** Weighted pick over `[key, weight]` pairs. Returns null if all weights <= 0. */
  weighted(entries) {
    let total = 0;
    for (const [, w] of entries) if (w > 0) total += w;
    if (total <= 0) return null;
    let roll = this.next() * total;
    for (const [key, w] of entries) {
      if (w <= 0) continue;
      roll -= w;
      if (roll <= 0) return key;
    }
    return entries[entries.length - 1][0];
  }

  /** Independent sub-stream, for isolating systems from each other's draws. */
  fork(label) { return new RNG(hashSeed(`${this.seed}:${label}`)); }
}

/** Shared default stream. Systems that need reproducibility make their own. */
export const rng = new RNG(Date.now());

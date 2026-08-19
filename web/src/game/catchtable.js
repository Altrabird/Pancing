/**
 * Catch table resolution.
 *
 * Drawing a fish is a weighted pick where every modifier is multiplicative and
 * transparent. The same function that picks the fish also explains the pick, so
 * the UI can tell the player "night + rain + shrimp on the bottom is why you
 * keep hooking Baung" instead of leaving it as folklore.
 *
 * Size is drawn from a truncated normal on length, then converted to mass with
 * the species' real length-weight allometry (W = a·L^b). Weight therefore has
 * the right skew for free: a 10% longer fish is roughly 33% heavier, which is
 * why chasing length records feels the way it does.
 */

import { SPECIES_BY_ID, RARITY, LURE_MISMATCH } from '../data/species.js';
import { clamp, clamp01, smoothstep } from '../core/loop.js';

/** A fish this far outside the species mean is flagged as a trophy. */
const TROPHY_SIGMA = 1.85;

/**
 * Build the weighted entry list for a context, without drawing.
 * Exposed so the harness can assert distributions and the UI can preview odds.
 *
 * @returns {{entries: Array<[string, number]>, detail: Object, total: number}}
 */
export function buildTable(ctx) {
  const {
    spot, phase, weather, lure, lureDepthNorm = 0.5, level = 1,
    activityBonus = 1,
  } = ctx;

  const entries = [];
  const detail = {};
  let total = 0;

  for (const [id, spotWeight] of Object.entries(spot.pool)) {
    const sp = SPECIES_BY_ID[id];
    if (!sp) continue;

    // Level gate: unavailable rather than merely unlikely, so progression reads
    // as unlocking rather than as grinding.
    if (sp.minLevel > level) {
      detail[id] = { weight: 0, reason: 'locked', need: sp.minLevel };
      continue;
    }

    const mTime = sp.times[phase.id] ?? 1;
    const mWeather = sp.weather[weather.id] ?? 1;
    const mLure = sp.lures ? (sp.lures[lure.id] ?? LURE_MISMATCH) : 1;

    // Depth: a soft band, not a hard gate. Fishing 20 cm off the right layer
    // should cost you a little, not everything.
    const [lo, hi] = sp.depth;
    let mDepth;
    if (lureDepthNorm < lo) mDepth = 1 - smoothstep(0, lo + 0.22, lo - lureDepthNorm) * 0.92;
    else if (lureDepthNorm > hi) mDepth = 1 - smoothstep(0, (1 - hi) + 0.22, lureDepthNorm - hi) * 0.92;
    else mDepth = 1;
    mDepth = clamp(mDepth, 0.06, 1);

    const w = sp.weight * spotWeight * mTime * mWeather * mLure * mDepth * activityBonus;

    detail[id] = {
      weight: w, species: sp,
      mods: { spot: spotWeight, time: mTime, weather: mWeather, lure: mLure, depth: mDepth },
    };
    if (w > 0) { entries.push([id, w]); total += w; }
  }

  return { entries, detail, total };
}

/** Odds as percentages, sorted high to low. For the "what's biting" panel. */
export function odds(ctx) {
  const { entries, detail, total } = buildTable(ctx);
  const rows = entries.map(([id, w]) => ({
    id,
    species: SPECIES_BY_ID[id],
    pct: total > 0 ? (w / total) * 100 : 0,
    mods: detail[id].mods,
  }));
  rows.sort((a, b) => b.pct - a.pct);
  return rows;
}

/**
 * Draw a species for the current context.
 * @returns {object|null} species record, or null if nothing can bite here
 */
export function drawSpecies(rng, ctx) {
  const { entries } = buildTable(ctx);
  if (!entries.length) return null;
  const id = rng.weighted(entries);
  return id ? SPECIES_BY_ID[id] : null;
}

/**
 * Roll an individual fish of a species.
 *
 * @param {object} opts
 *   sizeBias   {number} lure sizeBias + gear bonuses, shifts the mean up
 *   luck       {number} 0..1 player luck stat, widens the top tail only
 */
export function rollFish(rng, species, opts = {}) {
  const { sizeBias = 0, luck = 0 } = opts;
  const L = species.length;

  // Bias shifts the mean toward the top of the range rather than scaling it, so
  // a big-fish lure cannot produce an impossible fish.
  const headroom = L.max - L.mean;
  const mean = L.mean + headroom * clamp01(sizeBias) * 0.55;
  const sd = L.sd * (1 + luck * 0.22);

  let lengthCm = rng.normalClamped(mean, sd, L.min, L.max);

  // Trophy roll: a rare second draw that pushes the fish into the top tail.
  const trophyChance = (RARITY[species.rarity]?.trophyBonus ?? 0.02) + luck * 0.03;
  let trophy = false;
  if (rng.next() < trophyChance) {
    const boosted = rng.normalClamped(L.mean + headroom * 0.72, L.sd * 0.85, L.mean, L.max);
    if (boosted > lengthCm) { lengthCm = boosted; trophy = true; }
  }

  const sigma = (lengthCm - L.mean) / L.sd;
  if (sigma >= TROPHY_SIGMA) trophy = true;

  // W = a·L^b, with a little individual condition factor (fat fish / lean fish).
  const condition = rng.float(0.90, 1.12);
  const massKg = species.allometry.a * Math.pow(lengthCm, species.allometry.b) * condition;

  return {
    species,
    speciesId: species.id,
    lengthCm: Math.round(lengthCm * 10) / 10,
    massKg: Math.round(massKg * 1000) / 1000,
    sigma: Math.round(sigma * 100) / 100,
    condition: Math.round(condition * 100) / 100,
    trophy,
    rarity: species.rarity,
  };
}

/**
 * Sale value. Trophies and heavy fish are worth disproportionately more, which
 * keeps a big common fish competitive with a small rare one.
 */
export function valueOf(fish, multiplier = 1) {
  const base = fish.species.value * Math.max(fish.massKg, 0.05);
  const sizeBonus = 1 + clamp(fish.sigma, 0, 3) * 0.22;
  const trophyBonus = fish.trophy ? 1.6 : 1;
  return Math.max(1, Math.round(base * sizeBonus * trophyBonus * multiplier));
}

export function xpOf(fish, multiplier = 1) {
  const sizeBonus = 1 + clamp(fish.sigma, 0, 3) * 0.30;
  return Math.max(1, Math.round(fish.species.xp * sizeBonus * (fish.trophy ? 1.5 : 1) * multiplier));
}

/** Human-readable size class, used on the catch card. */
export function sizeClass(fish) {
  if (fish.sigma >= 2.2) return { label: 'Gergasi', tier: 4 };
  if (fish.sigma >= 1.2) return { label: 'Besar', tier: 3 };
  if (fish.sigma >= -0.4) return { label: 'Sederhana', tier: 2 };
  if (fish.sigma >= -1.2) return { label: 'Kecil', tier: 1 };
  return { label: 'Anak', tier: 0 };
}

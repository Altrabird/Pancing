/**
 * Headless simulation harness.
 *
 * Drives the real game loop with an autopilot angler and reports what actually
 * happened. This is how the balance gets checked without clicking through a
 * thousand casts: catch-table distribution, fight outcomes, snap rate, economy
 * pace and — most importantly — whether the tension solver stays finite.
 *
 *   node tools/simulate.mjs [casts] [--seed=N] [--verbose] [--spot=id]
 */

import { RNG } from '../src/core/rng.js';
import { EventBus, EV } from '../src/core/events.js';
import { FIXED_DT } from '../src/core/loop.js';
import { PlayerState } from '../src/game/state.js';
import { World } from '../src/game/world.js';
import { FishingGame, GAME_STATE } from '../src/game/fishing.js';
import { SPOTS_BY_ID } from '../src/data/spots.js';
import { buildTable, odds } from '../src/game/catchtable.js';
import { ZONE_LIMITS } from '../src/physics/rod.js';

const args = process.argv.slice(2);
const N = Number(args.find((a) => /^\d+$/.test(a)) ?? 400);
const seed = Number((args.find((a) => a.startsWith('--seed=')) ?? '--seed=12345').split('=')[1]);
const verbose = args.includes('--verbose');
const spotArg = (args.find((a) => a.startsWith('--spot=')) ?? '').split('=')[1];
/** starter = beginner tackle; mismatch = strong reel on light line (snap bait). */
const gearArg = (args.find((a) => a.startsWith('--gear=')) ?? '--gear=balanced').split('=')[1];
/** A reckless angler locks the drag down and winches. Exercises the failure paths. */
const reckless = args.includes('--reckless');

const rng = new RNG(seed);
const bus = new EventBus();
const state = new PlayerState(bus);
// Give the autopilot a mid-game loadout so it can reach the whole table.
state.data.level = 14;
state.data.money = 50000;
for (const slot of ['rod', 'reel', 'line']) {
  state.data.owned[slot] = state.data.owned[slot].slice();
}
const LOADOUTS = {
  balanced: ['rod_graphite', 'reel_spin4000', 'line_braid30'],
  starter:  ['rod_buluh', 'reel_tangan', 'line_mono8'],
  // A reel that can out-pull the line by a mile. This should break off.
  mismatch: ['rod_carbon', 'reel_bc', 'line_mono8'],
  heavy:    ['rod_kelah', 'reel_sw', 'line_braid65'],
};
for (const id of LOADOUTS[gearArg] ?? LOADOUTS.balanced) { state.buy(id); state.equip(id); }
state.checkSpotUnlocks();
if (spotArg) { state.data.unlockedSpots.push(spotArg); state.travel(spotArg); }

const world = new World(rng.fork('world'), bus, state.spot);
const game = new FishingGame({ rng, bus, state, world });

/* --- tallies -------------------------------------------------------------- */

const tally = {
  casts: 0, interests: 0, nibbles: 0, windows: 0, hooked: 0, landed: 0,
  snap: 0, hookLost: 0, snagged: 0, missed: 0, spooked: 0, dryland: 0,
  species: {}, lostSpecies: {}, fightStates: {}, phases: {},
  fightTimes: [], peakTension: [], masses: [],
};

bus.on(EV.INTEREST, () => tally.interests++);
bus.on(EV.NIBBLE, () => tally.nibbles++);
bus.on(EV.BITE_ON, (p) => { if (p.phase === 'window') tally.windows++; });
bus.on(EV.HOOKED, () => tally.hooked++);
bus.on(EV.BITE_MISSED, () => tally.missed++);
bus.on(EV.SPOOKED, () => tally.spooked++);
bus.on(EV.LINE_SNAP, () => tally.snap++);
bus.on(EV.SNAGGED, () => tally.snagged++);
bus.on(EV.HOOK_LOST, (p) => { if (p.kind === 'hook') tally.hookLost++; });
bus.on(EV.FIGHT_STATE, (p) => { tally.fightStates[p.state] = (tally.fightStates[p.state] ?? 0) + 1; });
bus.on(EV.LANDED, (c) => {
  tally.landed++;
  tally.species[c.speciesId] = (tally.species[c.speciesId] ?? 0) + 1;
  tally.phases[c.phase] = (tally.phases[c.phase] ?? 0) + 1;
  tally.fightTimes.push(c.fightSeconds);
  tally.peakTension.push(c.peakTension);
  tally.masses.push(c.massKg);
  if (verbose) {
    console.log(`  landed ${c.species.name.padEnd(14)} ${String(c.lengthCm).padStart(6)}cm ` +
      `${String(c.massKg.toFixed(2)).padStart(7)}kg  ${c.fightSeconds}s  peak ${c.peakTension}N` +
      `${c.trophy ? '  *TROPHY*' : ''}${c.isRecord ? '  (record)' : ''}`);
  }
});
bus.on(EV.FIGHT_END, (c) => {
  if (c?.lost && c.species) {
    tally.lostSpecies[c.species.id] = (tally.lostSpecies[c.species.id] ?? 0) + 1;
  }
});

/* --- the autopilot -------------------------------------------------------- */

/**
 * A competent-but-not-perfect angler. It aims for the middle of the good
 * tension band, backs off in the danger band, and strikes with a human-ish
 * reaction delay so the hookset window is genuinely tested.
 */
const input = { reelAxis: 0, dragAxis: 0 };
let reaction = -1;
let castHoldTarget = 0;
let holdTime = 0;
let idleTime = 0;

bus.on(EV.BITE_ON, (p) => {
  if (p.phase !== 'window') return;
  // Reaction time: 180–420 ms. Fast fish windows will genuinely be missed.
  reaction = rng.float(0.18, 0.42);
});

function autopilot(dt) {
  const t = game.telemetry();

  if (reaction >= 0) {
    reaction -= dt;
    if (reaction < 0) game.strike();
  }

  switch (t.phase) {
    case GAME_STATE.READY:
      idleTime += dt;
      if (idleTime > 0.15) {
        idleTime = 0;
        castHoldTarget = rng.float(0.92, 1.06);   // occasionally overcharges
        holdTime = 0;
        game.aim(rng.float(-0.35, 0.35), rng.float(0.5, 0.78));
        game.beginCast();
        tally.casts++;
      }
      input.reelAxis = 0;
      break;

    case GAME_STATE.CHARGING:
      holdTime += dt;
      if (holdTime >= castHoldTarget * 1.15) game.releaseCast();
      break;

    case GAME_STATE.FISHING: {
      // Fish the bait: mostly static, with an occasional twitch of retrieve.
      const active = t.gear.lure.action > 0.4;
      input.reelAxis = active ? (Math.sin(game.sessionSeconds * 2.2) > 0.1 ? 0.55 : 0) : 0;
      // Give up on a dead cast rather than sitting forever.
      if (t.bite.state === 'searching' && game.cast.distance > 2 && rng.next() < dt * 0.05) {
        input.reelAxis = 1;
      }
      break;
    }

    case GAME_STATE.FIGHT: {
      const load = t.rod.loadFrac;
      const dragLoad = t.rod.dragLoad;
      if (reckless) {
        // Drag buried, handle cranked, no regard for the meter.
        input.reelAxis = 1;
        input.dragAxis = 1;
        break;
      }
      // Pump when there is room, stop dead in the danger band.
      if (load > ZONE_LIMITS.high) input.reelAxis = 0;
      else if (load > ZONE_LIMITS.good) input.reelAxis = 0.15;
      else if (load < ZONE_LIMITS.slack) input.reelAxis = 1.0;   // never leave it slack
      else input.reelAxis = 0.85;
      // Nurse the clutch: back it off near breaking strain, tighten it when the
      // fish is stripping line the tackle could comfortably hold.
      input.dragAxis = load > 0.82 ? -1
        : (t.rod.slipping && load < 0.55) ? 1
        : (dragLoad < 0.30 && load < 0.30) ? 0.4 : 0;
      break;
    }

    default:
      input.reelAxis = 0;
      input.dragAxis = 0;
      break;
  }
}

/* --- run ------------------------------------------------------------------ */

console.log(`\nPancing headless simulation — seed ${seed}, target ${N} casts, spot "${state.spot.name}"\n`);

const t0 = Date.now();
let steps = 0;
let nonFinite = 0;
let maxTension = 0;
const MAX_STEPS = N * 120 * 90;   // 90 in-game seconds per cast, hard ceiling

while (tally.casts < N && steps < MAX_STEPS) {
  world.update(FIXED_DT);
  autopilot(FIXED_DT);
  game.update(FIXED_DT, input);

  const T = game.rod.tension;
  if (!Number.isFinite(T) || !Number.isFinite(game.rod.lineOut)) nonFinite++;
  if (T > maxTension) maxTension = T;

  steps++;
}
const elapsed = (Date.now() - t0) / 1000;

/* --- report --------------------------------------------------------------- */

const pct = (a, b) => b > 0 ? `${((a / b) * 100).toFixed(1)}%` : '—';
const avg = (arr) => arr.length ? arr.reduce((s, x) => s + x, 0) / arr.length : 0;
const median = (arr) => {
  if (!arr.length) return 0;
  const s = [...arr].sort((a, b) => a - b);
  return s[Math.floor(s.length / 2)];
};

console.log('--- funnel -------------------------------------------------');
console.log(`casts            ${tally.casts}`);
console.log(`interest         ${tally.interests}   (${pct(tally.interests, tally.casts)} of casts)`);
console.log(`nibbles          ${tally.nibbles}`);
console.log(`bite windows     ${tally.windows}   (${pct(tally.windows, tally.interests)} of interests)`);
console.log(`hooked           ${tally.hooked}   (${pct(tally.hooked, tally.windows)} of windows)`);
console.log(`landed           ${tally.landed}   (${pct(tally.landed, tally.hooked)} of hookups)`);
console.log('');
console.log('--- failures -----------------------------------------------');
console.log(`line snaps       ${tally.snap}   (${pct(tally.snap, tally.hooked)} of hookups)`);
console.log(`hook pulled      ${tally.hookLost}   (${pct(tally.hookLost, tally.hooked)} of hookups)`);
console.log(`struck late/miss ${tally.missed}`);
console.log(`spooked          ${tally.spooked}`);
console.log(`structure hits   ${tally.snagged}`);
console.log('');
console.log('--- fights -------------------------------------------------');
console.log(`avg fight        ${avg(tally.fightTimes).toFixed(1)}s   median ${median(tally.fightTimes).toFixed(1)}s   max ${Math.max(0, ...tally.fightTimes).toFixed(1)}s`);
console.log(`avg peak tension ${avg(tally.peakTension).toFixed(1)}N   max observed ${maxTension.toFixed(1)}N`);
console.log(`avg mass landed  ${avg(tally.masses).toFixed(2)}kg   biggest ${Math.max(0, ...tally.masses).toFixed(2)}kg`);
console.log(`behaviour mix    ${Object.entries(tally.fightStates).sort((a, b) => b[1] - a[1]).map(([k, v]) => `${k}:${v}`).join('  ')}`);
console.log('');
console.log('--- catch composition --------------------------------------');
const rows = Object.entries(tally.species).sort((a, b) => b[1] - a[1]);
for (const [id, n] of rows) {
  const lost = tally.lostSpecies[id] ?? 0;
  console.log(`  ${id.padEnd(14)} ${String(n).padStart(4)}  ${pct(n, tally.landed).padStart(6)}` +
    (lost ? `   (lost ${lost} more)` : ''));
}
console.log('');
console.log('--- economy ------------------------------------------------');
console.log(`level ${state.data.level}   money ${state.data.money}   earned ${state.data.stats.totalEarned}`);
console.log(`records ${Object.keys(state.data.records).length}   trophies ${state.data.stats.trophies}   quests ${Object.keys(state.data.quests).length}/8`);
console.log('');
console.log('--- health -------------------------------------------------');
console.log(`sim steps        ${steps}  (${(steps * FIXED_DT / 60).toFixed(1)} in-game minutes)`);
console.log(`wall clock       ${elapsed.toFixed(2)}s   (${(steps / elapsed / 1000).toFixed(0)}k steps/sec)`);
console.log(`non-finite ticks ${nonFinite}  ${nonFinite === 0 ? 'OK' : '*** SOLVER UNSTABLE ***'}`);

// Static odds check for the current context, independent of the run.
console.log('');
console.log('--- odds right now (level 14, current lure/time/weather) ----');
for (const r of odds({
  spot: state.spot, phase: world.phase, weather: world.weather,
  lure: game.gear.lure, lureDepthNorm: 0.5, level: state.level,
}).slice(0, 8)) {
  console.log(`  ${r.id.padEnd(14)} ${r.pct.toFixed(1).padStart(5)}%   ` +
    `lure x${r.mods.lure.toFixed(2)} time x${r.mods.time.toFixed(2)} depth x${r.mods.depth.toFixed(2)}`);
}
console.log('');

const problems = [];
if (nonFinite > 0) problems.push('solver produced non-finite values');
if (tally.landed === 0) problems.push('nothing was landed');
if (tally.hooked === 0) problems.push('nothing was hooked');
if (tally.landed / Math.max(tally.hooked, 1) > 0.98) problems.push('landing rate ~100% — no tension risk');
if (tally.landed / Math.max(tally.hooked, 1) < 0.15) problems.push('landing rate under 15% — punishing');
if (problems.length) {
  console.log('PROBLEMS: ' + problems.join('; '));
  process.exitCode = 1;
} else {
  console.log('Simulation healthy.');
}

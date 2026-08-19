/**
 * Parity dump — JavaScript side.
 *
 * Runs a fixed, scripted scenario through the real modules in web/src and prints
 * one line per observation. The C# side (DumpCs.cs) runs the identical script
 * through unity/…/Sim and prints the same lines. run.sh diffs them.
 *
 * The point is not that the two builds look alike. It is that a seed means the
 * same session in both, that the tension solver returns the same newtons, and
 * that a balance number measured with tools/simulate.mjs still describes the
 * Unity build. Without this, "one game, two engines" is a slogan.
 *
 *   node shared/parity/dump_js.mjs
 */

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { RNG, hashSeed } from '../../web/src/core/rng.js';
import { RodSystem } from '../../web/src/physics/rod.js';
import { CastSystem, CAST_PHASE } from '../../web/src/physics/cast.js';
import { BiteSystem } from '../../web/src/physics/bite.js';
import { HookedFish } from '../../web/src/game/fish.js';
import { buildTable, drawSpecies, rollFish, valueOf, xpOf, sizeClass } from '../../web/src/game/catchtable.js';
import { SPECIES_BY_ID } from '../../web/src/data/species.js';
import { GEAR_BY_ID } from '../../web/src/data/gear.js';
import { SPOTS_BY_ID, TIME_PHASES, WEATHER_BY_ID, phaseForHour } from '../../web/src/data/spots.js';

const HERE = dirname(fileURLToPath(import.meta.url));
const out = [];
const say = (s) => out.push(s);

/** Fixed-width so a diff points at the exact field that drifted. */
const f = (x) => (Object.is(x, -0) ? 0 : x).toFixed(12);
/** Bare integers, for counts and ids. */
const i = (x) => String(x);

/* ------------------------------------------------------------------ 1. RNG */
// The integer stream must match bit for bit. Everything else in the game is
// downstream of it, so a drift here is the only kind that is unrecoverable.

say('# section rng-words');
{
  const r = new RNG(42);
  for (let n = 0; n < 16; n++) {
    // Reproduce the raw 32-bit word Next() divides down, without disturbing the
    // stream: the same expression the generator uses internally.
    const t = ((r.a + r.b | 0) + r.d | 0) >>> 0;
    r.next();
    say(`rngword ${n} ${t}`);
  }
}

say('# section rng-hash');
for (const s of ['pancing', 'tilapia', 'kolam', 'Ikan Keli', '', '0']) {
  say(`hash ${JSON.stringify(s)} ${hashSeed(s)}`);
}

say('# section rng-floats');
{
  const r = new RNG('pancing');
  for (let n = 0; n < 12; n++) say(`float ${n} ${f(r.next())}`);
}

say('# section rng-normal');
{
  const r = new RNG(1337);
  for (let n = 0; n < 8; n++) say(`normal ${n} ${f(r.normal(21, 6))}`);
  for (let n = 0; n < 4; n++) say(`nclamp ${n} ${f(r.normalClamped(21, 6, 11, 45))}`);
}

say('# section rng-fork');
{
  const r = new RNG(9001);
  for (const label of ['bite', 'fight', 'catch']) {
    say(`fork ${label} ${r.fork(label).seed}`);
  }
}

say('# section rng-weighted');
{
  // Order matters: the roll walks the entries in the order given. This section
  // exists because the C# side had to reproduce JavaScript object ordering.
  const r = new RNG(777);
  const entries = [['a', 3], ['b', 1], ['c', 0], ['d', 6], ['e', 2]];
  const counts = {};
  for (let n = 0; n < 400; n++) {
    const k = r.weighted(entries);
    counts[k] = (counts[k] ?? 0) + 1;
  }
  for (const [k] of entries) say(`weighted ${k} ${i(counts[k] ?? 0)}`);
}

/* ---------------------------------------------------------------- 2. data */
// The Unity build parses JSON that was generated from these very modules. If the
// parse or the generation drifts, every number downstream is wrong in a way that
// still runs, which is the worst kind.

say('# section data');
{
  const speciesJson = JSON.parse(readFileSync(join(HERE, '../data/species.json'), 'utf8'));
  const spotsJson = JSON.parse(readFileSync(join(HERE, '../data/spots.json'), 'utf8'));
  const gearJson = JSON.parse(readFileSync(join(HERE, '../data/gear.json'), 'utf8'));

  say(`counts ${i(speciesJson.species.length)} ${i(gearJson.items.length)} ${i(spotsJson.spots.length)}`);

  for (const id of ['tilapia', 'keli', 'toman', 'kelah', 'udang_galah', 'plastik']) {
    const sp = SPECIES_BY_ID[id];
    if (!sp) { say(`species ${id} MISSING`); continue; }
    say(`species ${id} ${f(sp.allometry.a)} ${f(sp.allometry.b)} ${f(sp.depth[0])} ${f(sp.depth[1])} ` +
        `${f(sp.length.mean)} ${f(sp.bite.window)} ${sp.fight.profile} ${sp.rarity} ${i(sp.minLevel)}`);
  }

  for (const id of ['rod_fiber', 'reel_spin4000', 'line_braid30', 'worm', 'popper']) {
    const g = GEAR_BY_ID[id];
    say(`gear ${id} ${g.slot} ${f(g.price)} ${i(g.level)}`);
  }

  // Pool ORDER, not just contents — the weighted draw walks it in order.
  for (const id of ['kolam', 'sungai', 'tasik']) {
    const spot = SPOTS_BY_ID[id];
    const keys = Object.keys(spot.pool);
    say(`pool ${id} ${keys.join(',')}`);
    say(`spot ${id} ${f(spot.maxDepth)} ${f(spot.waterClarity)} ${f(spot.snagDensity)} ${i(spot.structure.length)}`);
  }

  // depthAt is code, not data, in both engines. Sample it on a grid.
  for (const id of ['kolam', 'sungai', 'tasik']) {
    const spot = SPOTS_BY_ID[id];
    for (let a = 0; a <= 4; a++) {
      const row = [];
      for (let b = 0; b <= 4; b++) {
        row.push(f(spot.depthAt(a / 4, -1 + b * 0.5)));
      }
      say(`depth ${id} ${a} ${row.join(' ')}`);
    }
  }

  for (const h of [0, 4.9, 5.0, 7.99, 8.0, 12.5, 17.0, 19.4, 19.5, 23.9]) {
    say(`phase ${f(h)} ${phaseForHour(h).id}`);
  }
}

/* ------------------------------------------------------- 3. tension solver */
// Braid deliberately: 2 % stretch is the case that explodes a naive ODE
// integrator, so it is the case worth pinning.

const GEAR = {
  rod:  GEAR_BY_ID.rod_fiber,
  reel: GEAR_BY_ID.reel_spin4000,
  line: GEAR_BY_ID.line_braid30,
  lure: GEAR_BY_ID.spinner,
};

say('# section rod-curve');
{
  const rs = new RodSystem();
  rs.configure(GEAR);
  rs.lineOut = 12;
  for (let n = 0; n <= 10; n++) {
    const T = n * 15;
    say(`curve ${n} ${f(rs.lineExtension(T))} ${f(rs.rodDeflection(T))}`);
  }
  for (let n = 0; n <= 10; n++) {
    say(`solve ${n} ${f(rs.solveTension(n * 0.05))}`);
  }
}

say('# section rod-fight');
{
  const rs = new RodSystem();
  rs.configure(GEAR);
  rs.setDragFrac(0.62);
  rs.respool(14);

  const dt = 1 / 120;
  let fishDist = 14;
  // A scripted fight: the fish runs, is pumped back, runs again into structure,
  // then gives up. Deterministic — no RNG — so a mismatch here is the solver's.
  for (let tick = 0; tick < 900; tick++) {
    const t = tick * dt;
    const runA = Math.max(0, Math.sin(t * 0.9)) * 2.4;
    const runB = Math.max(0, Math.sin(t * 0.31 - 0.6)) * 3.1;
    fishDist += (runA + runB - 1.35) * dt;
    fishDist = Math.max(0.5, fishDist);

    const reelInput = t > 1.5 && Math.sin(t * 2.2) > -0.2 ? 1 : 0;
    const onStructure = t > 4.0 && t < 5.2;
    if (tick === 300) rs.setDragFrac(0.86);
    if (tick === 600) rs.setDragFrac(0.40);

    const step = rs.update(dt, { fishDist, reelInput, onStructure, extraLoad: 0.4 });

    if (tick % 30 === 0) {
      say(
        `fight ${tick} ${f(step.tension)} ${f(step.lineOut)} ${f(step.loadFrac)} ` +
        `${f(rs.lineIntegrity)} ${f(rs.hookHold)} ${f(step.bend)} ${f(step.slipRate)} ` +
        `${step.zone} ${step.slipping ? 1 : 0} ${step.snapped ? 1 : 0}`
      );
    }
  }
  const tm = rs.telemetry();
  say(`final ${f(tm.tension)} ${f(tm.lineOut)} ${f(tm.lineIntegrity)} ${f(tm.hookHold)} ${f(tm.peak)} ${tm.zone} ${tm.dragUnsafe ? 1 : 0}`);
}

/* ------------------------------------------------------------- 4. casting */

say('# section cast');
{
  const tip = { x: 0, y: 2.5, z: 0 };
  // Three release points: perfect, early, and held right through the overload
  // band — the three outcomes the charge curve is shaped to produce.
  const holds = [1.15, 0.62, 1.55];
  for (let c = 0; c < holds.length; c++) {
    const cs = new CastSystem(new RNG(500 + c));
    cs.aimYaw = 0.12;
    cs.beginCharge();

    const dt = 1 / 120;
    let held = 0;
    let auto = false;
    while (held < holds[c]) {
      if (cs.updateCharge(dt) === 'auto-release') { auto = true; break; }
      held += dt;
    }
    const rel = cs.release(tip, GEAR, 0.4);
    say(`release ${c} ${f(rel.power)} ${f(rel.quality)} ${rel.backlash ? 1 : 0} ${rel.perfect ? 1 : 0} ${auto ? 1 : 0}`);
    say(`vel ${c} ${f(cs.vel.x)} ${f(cs.vel.y)} ${f(cs.vel.z)}`);

    let ticks = 0;
    let splash = null;
    while (cs.phase === CAST_PHASE.FLYING && ticks < 2000) {
      const r = cs.updateFlight(dt, 0, 0.4);
      ticks++;
      if (r?.event === 'splash') { splash = r; break; }
      if (r?.event === 'dryland') break;
    }
    say(`flight ${c} ${i(ticks)} ${f(cs.distance)} ${f(cs.pos.x)} ${f(cs.pos.z)} ${splash ? f(splash.impact) : 'none'}`);

    let sinkTicks = 0;
    while (cs.phase === CAST_PHASE.SINKING && sinkTicks < 4000) {
      cs.updateSink(dt, GEAR.lure, 4.2);
      sinkTicks++;
    }
    say(`sink ${c} ${i(sinkTicks)} ${f(cs.sinkDepth)} ${f(cs.targetDepth)}`);

    const moved = cs.retrieve(dt * 20, 0.9, tip);
    say(`retrieve ${c} ${f(moved)} ${f(cs.distance)} ${f(cs.sinkDepth)}`);
  }
}

/* --------------------------------------------------------- 5. catch table */

say('# section catchtable');
{
  const ctx = {
    spot: SPOTS_BY_ID.tasik,
    phase: TIME_PHASES.find((p) => p.id === 'night'),
    weather: WEATHER_BY_ID.rain,
    lure: GEAR_BY_ID.shrimp,
    lureDepthNorm: 0.78,
    level: 12,
    activityBonus: 1.25,
  };
  const { entries, total } = buildTable(ctx);
  say(`table total ${f(total)} entries ${i(entries.length)}`);
  for (const [id, w] of entries) say(`entry ${id} ${f(w)}`);

  // Draw distribution: the single strongest signal that the weighted pick, the
  // pool ordering and the modifier stack all agree.
  const r = new RNG(24680);
  const counts = {};
  for (let n = 0; n < 600; n++) {
    const sp = drawSpecies(r, ctx);
    const id = sp ? sp.id : 'null';
    counts[id] = (counts[id] ?? 0) + 1;
  }
  for (const id of Object.keys(counts).sort()) say(`draw ${id} ${i(counts[id])}`);

  const rr = new RNG(13579);
  for (const id of ['tilapia', 'toman', 'kelah', 'udang_galah']) {
    for (let n = 0; n < 3; n++) {
      const fish = rollFish(rr, SPECIES_BY_ID[id], { sizeBias: 0.3, luck: 0.2 });
      say(`roll ${id} ${n} ${f(fish.lengthCm)} ${f(fish.massKg)} ${f(fish.sigma)} ` +
          `${f(fish.condition)} ${fish.trophy ? 1 : 0} ${i(valueOf(fish))} ${i(xpOf(fish))} ${sizeClass(fish).label}`);
    }
  }
}

/* ---------------------------------------------------------------- 6. bite */

say('# section bite');
{
  const bs = new BiteSystem(new RNG(31415), null);
  bs.begin();

  const spot = SPOTS_BY_ID.kolam;
  const lure = GEAR_BY_ID.worm;
  const line = GEAR_BY_ID.line_mono8;
  // A fixed candidate, so this section tests the FSM rather than the draw.
  const candidateOrder = ['tilapia', 'keli', 'puyu', 'haruan'];
  let drawn = 0;

  const dt = 1 / 120;
  let struckAt = -1;
  const events = [];
  for (let tick = 0; tick < 6000; tick++) {
    const t = tick * dt;
    // Strike 180 ms after the first hookset window opens; also strike once well
    // before anything is happening, to exercise the whiff path.
    const struck = tick === 240 || (struckAt >= 0 && tick === struckAt);

    const ev = bs.update(dt, {
      lure, line, spot,
      lureDepthNorm: 0.35,
      retrieveRate: t > 12 ? 0.25 : 0,
      noise: 0.12,
      spotActivity: 1.1,
      jerk: t > 20 && t < 20.2 ? 0.9 : 0.1,
      struck,
      drawCandidate: () => SPECIES_BY_ID[candidateOrder[drawn++ % candidateOrder.length]],
    });

    if (ev) {
      events.push(`bite ${i(tick)} ${ev.type} ${ev.species ? ev.species.id : '-'} ${ev.reason ?? '-'} ` +
                  `${ev.window != null ? f(ev.window) : '-'} ${ev.quality != null ? f(ev.quality) : '-'}`);
      if (ev.type === 'bite') struckAt = tick + Math.round(0.18 / dt);
    }
    if (events.length >= 40) break;
  }
  for (const e of events) say(e);
  const bt = bs.telemetry();
  say(`biteend ${bt.state} ${f(bt.attraction)} ${f(bt.presentation)} ${f(bt.cooldown)} ${i(bt.nibblesLeft)}`);
}

/* --------------------------------------------------------------- 7. fight */

say('# section fishai');
{
  for (const id of ['toman', 'kelah', 'tilapia', 'ranting']) {
    const sp = SPECIES_BY_ID[id];
    const rr = new RNG(hashSeed(`fight:${id}`));
    const roll = rollFish(rr, sp, { sizeBias: 0.5, luck: 0.1 });
    const fish = new HookedFish(roll, new RNG(hashSeed(`agent:${id}`)), {
      hookQuality: 0.8, startDist: 16, startDepth: 1.4, startLateral: 2.0,
    });
    say(`fishinit ${id} ${f(fish.strength)} ${f(fish.staminaMax)} ${f(fish.maxForce)} ${f(fish.stateDuration)}`);

    const dt = 1 / 120;
    const structures = [{ x: 3.0, z: 9.0, r: 1.4, kind: 'timber' }];
    let stateChanges = 0;
    let hookShock = 0;
    for (let tick = 0; tick < 2400; tick++) {
      // A scripted angler: steady pressure with a periodic pump, so the fish gets
      // to see both a tight line and a slack one.
      const t = tick * dt;
      const tension = 26 + 18 * Math.sin(t * 0.7) + 8 * Math.sin(t * 2.9);
      const r = fish.update(dt, {
        tension, loadFrac: tension / 133.4,
        structures, maxDepth: 6.5,
      });
      for (const e of r.events) {
        if (e.type === 'state') stateChanges++;
        if (e.type === 'hookShock') hookShock += e.amount;
      }
      if (tick % 400 === 0) {
        say(`fish ${id} ${tick} ${f(r.pull)} ${f(r.stamina)} ${f(r.dist)} ${f(r.depth)} ` +
            `${f(r.lateral)} ${r.state} ${r.onStructure ? 1 : 0}`);
      }
    }
    say(`fishend ${id} ${i(stateChanges)} ${f(hookShock)} ${i(fish.jumpsMade)} ${f(fish.elapsed)}`);
  }
}

process.stdout.write(out.join('\n') + '\n');

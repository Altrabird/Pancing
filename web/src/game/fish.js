/**
 * The hooked fish: an agent, not a health bar.
 *
 * The fish decides what to do based on how much stamina it has left, how hard
 * it is being pulled, and where the nearest structure is. The player never
 * fights a number — they fight a thing that has opinions.
 *
 * The stamina economy is the whole game:
 *
 *   drain = f(tension)     pulling hard tires the fish faster
 *   line/hook damage       ...but pulling hard also breaks your tackle
 *
 * So there is no single correct tension. There is a moving band, and the fish
 * keeps moving it by changing behaviour. A digger wants you impatient; a
 * thrasher wants you tight; a jumper wants you tight at exactly the wrong
 * moment.
 */

import { clamp, clamp01, damp, lerp, smoothstep } from '../core/loop.js';
import { FIGHT_PROFILES } from '../data/species.js';
import { ZONE_LIMITS } from '../physics/rod.js';

export const FIGHT_STATE = {
  RUN:     'run',      // hard sustained pull away from the angler
  DIVE:    'dive',     // straight down, sulking on the bottom
  THRASH:  'thrash',   // head-shakes; murders hook hold
  CIRCLE:  'circle',   // steady mid-effort, the fish's default cruise
  SURGE:   'surge',    // short violent burst, usually near the boat
  REST:    'rest',     // recovering stamina; your window to gain line
  JUMP:    'jump',     // airborne; a tight line here throws the hook
  BEATEN:  'beaten',   // out of gas, comes in on its side
};

const TUNE = {
  /** Peak pull in newtons for a strength-1.0 fish of average size. */
  forceScale: 78,
  /** How much of the fish's force scales with its actual mass. */
  massInfluence: 0.55,
  /** Stamina drained per second at full effort (tension == the fish's own max). */
  drainAtFullLoad: 0.165,
  /** Stamina burned simply by being hooked and panicking. Guarantees that every
   *  fight terminates even against a passive angler. */
  drainBase: 0.032,
  /** Recovery on a slack line. Deliberately well below the base drain: letting a
   *  fish rest should cost you time, not hand it the fight back. */
  recoverRate: 0.030,
  /** Effort below which the fish is coasting and starts to recover. */
  restEffort: 0.09,
  /** Below this stamina the fish stops choosing and just gets dragged. */
  beatenAt: 0.06,
  /** Head-shake hook damage per second while thrashing. */
  thrashHookRate: 0.16,
  /** Hook damage from landing a jump on a tight line. */
  jumpTightPenalty: 0.34,
  /** Distance in metres at which structure becomes reachable. */
  structureReach: 2.2,
};

export class HookedFish {
  /**
   * @param {object} spec  { species, lengthCm, massKg, trophy }
   * @param {import('../core/rng.js').RNG} rng
   */
  constructor(spec, rng, opts = {}) {
    this.rng = rng;
    this.species = spec.species;
    this.lengthCm = spec.lengthCm;
    this.massKg = spec.massKg;
    this.trophy = spec.trophy ?? false;
    this.profile = FIGHT_PROFILES[this.species.fight.profile] ?? FIGHT_PROFILES.runner;

    const f = this.species.fight;
    // Individual variation: two fish of the same species and size still differ.
    const vary = () => rng.float(0.88, 1.14);
    // Size relative to the species' own mean — a big one really does pull harder.
    const sizeRatio = this.lengthCm / this.species.length.mean;
    const sizeBoost = Math.pow(clamp(sizeRatio, 0.5, 2.4), TUNE.massInfluence);

    this.strength = f.strength * vary() * sizeBoost;
    this.staminaMax = f.stamina * vary() * lerp(1, 1.35, clamp01((sizeRatio - 1) / 1.4));
    this.stamina = 1;
    this.aggression = f.aggression;
    this.burst = f.burst;
    this.structureSeek = f.structureSeek;
    this.jumpChance = f.jumpChance;

    // A clean hookset in the corner of the jaw holds; a lip-hook does not.
    this.hookQuality = clamp(opts.hookQuality ?? 0.7, 0.05, 1);

    this.state = FIGHT_STATE.RUN;
    this.stateTime = 0;
    this.stateDuration = rng.float(1.2, 2.4);
    this.dist = opts.startDist ?? 12;
    this.depth = opts.startDepth ?? 1.0;
    this.lateral = opts.startLateral ?? 0;
    this.velAway = 0;
    this.effort = 0;
    this.pull = 0;
    this.smoothPull = 0;
    this.airborne = 0;
    this.nearStructure = null;
    this.onStructure = false;
    this.elapsed = 0;
    this.jumpsMade = 0;
    this.thrashPhase = rng.float(0, Math.PI * 2);
    this._surgeCooldown = 0;
  }

  /** Peak force this fish can produce right now, in newtons. */
  get maxForce() {
    return TUNE.forceScale * this.strength;
  }

  /**
   * Pick the next behaviour. Weighted by profile bias, current stamina, how
   * hard the player is pulling, and whether there is cover within reach.
   */
  _chooseState(ctx) {
    const p = this.profile;
    const s = this.stamina;
    const load = ctx.loadFrac;

    if (s <= TUNE.beatenAt) return FIGHT_STATE.BEATEN;

    // Exhausted fish rest; a fish being pulled hard resists rather than resting.
    if (s < 0.30 && load < ZONE_LIMITS.good && this.rng.bool(0.55)) return FIGHT_STATE.REST;

    const structureNear = this.nearStructure &&
      this.nearStructure.dist < TUNE.structureReach * (1 + this.structureSeek);

    const weights = [
      [FIGHT_STATE.RUN, p.runBias * (0.35 + s * 1.15) * (1 + this.burst * 0.6)],
      [FIGHT_STATE.DIVE, p.diveBias * (0.45 + s * 0.85) * (structureNear ? 2.4 : 1)],
      [FIGHT_STATE.THRASH, p.thrashBias * (0.25 + s * 0.9) * (1 + load * 0.9)],
      [FIGHT_STATE.CIRCLE, p.circleBias * (0.55 + (1 - s) * 0.85)],
      [FIGHT_STATE.SURGE, p.surgeRate * this.burst * (this._surgeCooldown > 0 ? 0 : 1)
        * (this.dist < 5 ? 1.9 : 0.7) * (0.3 + s)],
      [FIGHT_STATE.REST, p.restRate * (1 - s) * 1.4],
    ];

    // Jumping is opportunistic: a jumper near the surface with gas left.
    if (this.jumpChance > 0 && s > 0.25 && this.depth < 1.6 && this._surgeCooldown <= 0) {
      weights.push([FIGHT_STATE.JUMP, this.jumpChance * 2.2 * (load > ZONE_LIMITS.good ? 1.6 : 1)]);
    }

    return this.rng.weighted(weights) ?? FIGHT_STATE.CIRCLE;
  }

  _enterState(state) {
    this.state = state;
    this.stateTime = 0;
    const r = this.rng;
    switch (state) {
      case FIGHT_STATE.RUN:    this.stateDuration = r.float(1.4, 3.4); break;
      case FIGHT_STATE.DIVE:   this.stateDuration = r.float(1.8, 4.0); break;
      case FIGHT_STATE.THRASH: this.stateDuration = r.float(0.7, 1.8); break;
      case FIGHT_STATE.CIRCLE: this.stateDuration = r.float(2.0, 4.5); break;
      case FIGHT_STATE.SURGE:  this.stateDuration = r.float(0.35, 0.9); this._surgeCooldown = r.float(2.5, 5.0); break;
      case FIGHT_STATE.REST:   this.stateDuration = r.float(1.0, 2.6); break;
      case FIGHT_STATE.JUMP:   this.stateDuration = 0.85; this.airborne = 0; this._surgeCooldown = r.float(3, 6); this.jumpsMade++; break;
      case FIGHT_STATE.BEATEN: this.stateDuration = 999; break;
    }
  }

  /**
   * Advance one tick.
   *
   * @param {number} dt
   * @param {object} ctx
   *   tension   {number}  newtons currently on the line, from RodSystem
   *   loadFrac  {number}  tension / breaking strain, from RodSystem
   *   structures {Array}  { x, z, r } snag points in fish-local metres
   *   maxDepth  {number}  bed depth at the fish's position
   * @returns {object} report consumed by the fishing loop
   */
  update(dt, ctx) {
    this.elapsed += dt;
    this.stateTime += dt;
    this._surgeCooldown = Math.max(0, this._surgeCooldown - dt);

    const events = [];

    // --- stamina economy ---------------------------------------------------
    // Effort is tension measured against THIS FISH'S strength, not against the
    // line's breaking strain. That distinction is the whole difficulty curve:
    // 30 N is a crushing workload for a Tilapia and a gentle stretch for a
    // Toman, so heavy tackle beats small fish quickly and still cannot bully a
    // big one. Scaling by the line instead would have made better line tire
    // fish *slower*, which is exactly backwards.
    const effort = clamp01(ctx.tension / Math.max(this.maxForce, 1));
    const load = clamp01(ctx.loadFrac);
    const drain = TUNE.drainBase + TUNE.drainAtFullLoad * Math.pow(effort, 1.35);
    const resting = this.state === FIGHT_STATE.REST || this.state === FIGHT_STATE.BEATEN;
    const recovery = effort < TUNE.restEffort
      ? TUNE.recoverRate * (resting ? 1.8 : 1.0)
      : 0;
    this.effort = effort;
    this.stamina = clamp01(this.stamina - (drain / this.staminaMax) * dt + recovery * dt);

    // --- behaviour transitions -------------------------------------------
    if (this.stateTime >= this.stateDuration) {
      const next = this._chooseState(ctx);
      if (next !== this.state) {
        this._enterState(next);
        events.push({ type: 'state', state: this.state });
        if (this.state === FIGHT_STATE.JUMP) events.push({ type: 'jump' });
      } else {
        this.stateTime = 0;
        this.stateDuration *= this.rng.float(0.7, 1.3);
      }
    }

    // --- force output ------------------------------------------------------
    let pull = 0;
    let vDepth = 0;
    let vLateral = 0;
    const gas = smoothstep(0, 0.35, this.stamina);   // weak fish pull weakly
    const t = this.stateTime;

    switch (this.state) {
      case FIGHT_STATE.RUN: {
        // Accelerate into the run, then fade — fish do not pull flat.
        const shape = Math.sin(clamp01(t / this.stateDuration) * Math.PI) ** 0.7;
        pull = this.maxForce * (0.62 + this.aggression * 0.38) * shape * gas;
        vLateral = (this.lateral >= 0 ? 1 : -1) * 0.9 * gas;
        break;
      }
      case FIGHT_STATE.DIVE: {
        pull = this.maxForce * 0.55 * gas * (0.8 + 0.2 * Math.sin(t * 2.1));
        vDepth = 1.15 * gas;
        break;
      }
      case FIGHT_STATE.THRASH: {
        // Rapid oscillation: the tension needle should visibly hammer.
        this.thrashPhase += dt * 17;
        const shake = 0.5 + 0.5 * Math.sin(this.thrashPhase);
        pull = this.maxForce * (0.35 + 0.65 * shake) * gas;
        vDepth = Math.sin(this.thrashPhase * 0.5) * 0.3;
        break;
      }
      case FIGHT_STATE.CIRCLE: {
        pull = this.maxForce * 0.38 * gas * (0.85 + 0.15 * Math.sin(t * 1.3));
        vLateral = Math.sin(t * 0.9) * 1.1 * gas;
        break;
      }
      case FIGHT_STATE.SURGE: {
        const shape = Math.exp(-Math.pow((t - 0.18) / 0.22, 2));
        pull = this.maxForce * (1.05 + this.burst * 0.55) * shape * gas;
        break;
      }
      case FIGHT_STATE.REST: {
        pull = this.maxForce * 0.12 * gas;
        break;
      }
      case FIGHT_STATE.JUMP: {
        // Out of the water: no drag on the fish, so the line goes light for a
        // beat, then the fish lands. A tight line on landing tears the hook.
        this.airborne = Math.sin(clamp01(t / this.stateDuration) * Math.PI);
        pull = this.maxForce * 0.25 * (1 - this.airborne) * gas;
        vDepth = -this.airborne * 2.0;
        if (t >= this.stateDuration - dt && load > ZONE_LIMITS.good) {
          events.push({ type: 'hookShock', amount: TUNE.jumpTightPenalty * (0.6 + load * 0.7) });
          events.push({ type: 'splash' });
        }
        break;
      }
      case FIGHT_STATE.BEATEN: {
        pull = this.maxForce * 0.09;
        vDepth = -0.35;
        break;
      }
    }

    this.pull = pull;
    this.smoothPull = damp(this.smoothPull, pull, 12, dt);

    // --- movement ------------------------------------------------------------
    // Pure force balance against water drag. The fish swims away only while it
    // out-pulls the line, and is dragged in when the line out-pulls it. Nothing
    // here knows about the reel or the clutch: reeling shortens the line, which
    // raises tension, which flips the sign of the net force. The tug of war is
    // emergent, which is why a locked drag and a loose drag feel so different
    // without either being special-cased.
    const T = ctx.tension ?? 0;
    const net = pull - T;
    // Water drag on a body of this mass, N per m/s. Big fish are hard to stop
    // and also hard to accelerate.
    const waterDrag = 34 + this.massKg * 14 + this.lengthCm * 0.22;
    const vel = clamp(net / waterDrag, -2.6, 3.2);
    this.dist = Math.max(0.6, this.dist + vel * dt);
    this.velAway = vel;

    const freedom = 1 - clamp01(load * 0.8);

    this.depth = clamp(this.depth + vDepth * dt, 0, ctx.maxDepth ?? 6);
    this.lateral += vLateral * freedom * dt;
    if (Math.abs(this.lateral) > 6) this.lateral *= 0.92;

    // --- structure ---------------------------------------------------------
    this.nearStructure = null;
    if (ctx.structures?.length) {
      let best = Infinity;
      for (const s of ctx.structures) {
        const d = Math.hypot(s.x - this.lateral, s.z - this.dist) - s.r;
        if (d < best) { best = d; this.nearStructure = { ...s, dist: d }; }
      }
      const wasOn = this.onStructure;
      this.onStructure = this.nearStructure && this.nearStructure.dist <= 0;
      if (this.onStructure && !wasOn) events.push({ type: 'structureHit', snag: this.nearStructure });
    }

    // Diving fish actively steer toward cover.
    if (this.nearStructure && (this.state === FIGHT_STATE.DIVE || this.state === FIGHT_STATE.RUN)) {
      const pullToward = this.structureSeek * freedom * 0.8 * dt;
      this.lateral += Math.sign(this.nearStructure.x - this.lateral) * pullToward;
    }

    // --- hook wear from behaviour -----------------------------------------
    if (this.state === FIGHT_STATE.THRASH) {
      const wear = TUNE.thrashHookRate * this.profile.hookWear
                 * (0.4 + load * 1.2) * (1.35 - this.hookQuality) * dt;
      events.push({ type: 'hookShock', amount: wear });
    }

    return {
      pull: this.pull,
      state: this.state,
      stamina: this.stamina,
      velAway: this.velAway,
      dist: this.dist,
      depth: this.depth,
      lateral: this.lateral,
      airborne: this.airborne,
      onStructure: this.onStructure,
      beaten: this.state === FIGHT_STATE.BEATEN,
      events,
    };
  }

  telemetry() {
    return {
      species: this.species,
      state: this.state,
      stamina: this.stamina,
      lengthCm: this.lengthCm,
      massKg: this.massKg,
      dist: this.dist,
      depth: this.depth,
      // The renderer positions the fish from all three axes — omitting lateral
      // here silently produced NaN positions downstream.
      lateral: this.lateral,
      velAway: this.velAway,
      effort: this.effort,
      pull: this.smoothPull,
      maxForce: this.maxForce,
      airborne: this.airborne,
      onStructure: this.onStructure,
      trophy: this.trophy,
      elapsed: this.elapsed,
    };
  }
}

export { TUNE as FIGHT_TUNE };

/**
 * Rod, line and drag physics.
 *
 * THE MODEL
 * ---------
 * Rod tip and line are two springs in series between the reel and the fish:
 *
 *      reel ──[ drag clutch ]── rod blank ──tip──[ line ]── fish
 *
 * The line is close to linear: it reaches its rated breaking force exactly when
 * its strain reaches the material's rated stretch. That gives a spring whose
 * constant falls off with length, matching k = EA/L:
 *
 *      T_line(e) = testN * e / (stretch * lineOut)
 *
 * The rod is deliberately NOT linear. A real blank has a progressive taper: soft
 * at the tip, stiff at the butt, so the harder you load it the less extra give
 * you get. Modelled as a saturating exponential whose asymptote is the rod's
 * maximum deflection:
 *
 *      e_rod(T) = maxDeflect * (1 - exp(-T / power))
 *
 * That single curve is what makes a rod *feel* like a rod. It is the shock
 * absorber: a sudden lunge from the fish is soaked up by tip deflection before
 * it ever reaches the line, which is why a soft rod protects light line and a
 * broomstick snaps it.
 *
 * SOLVING IT
 * ----------
 * Both springs carry the same tension T and their extensions must sum to the
 * overshoot between where the fish is and how much line is out:
 *
 *      e_line(T) + e_rod(T) = fishDist - lineOut
 *
 * The left side is strictly increasing in T, so instead of integrating a stiff
 * ODE — which explodes with near-zero-stretch braid — we bisect for T directly.
 * That is unconditionally stable at any timestep and costs ~20 float ops.
 *
 * THE DRAG
 * --------
 * Above the clutch setting the spool slips and pays out line, which lengthens
 * lineOut, which drops the strain, which drops T back toward the setting. The
 * feedback loop is the drag. Nothing else needs to enforce the ceiling.
 */

import { clamp, clamp01, damp } from '../core/loop.js';

export const G = 9.81;

/** Tension bands, normalised against the line's breaking force. */
export const ZONE = {
  SLACK:  'slack',
  GOOD:   'good',
  HIGH:   'high',
  DANGER: 'danger',
};

export const ZONE_LIMITS = { slack: 0.08, good: 0.55, high: 0.85 };

export function zoneFor(loadFrac) {
  if (loadFrac < ZONE_LIMITS.slack) return ZONE.SLACK;
  if (loadFrac < ZONE_LIMITS.good) return ZONE.GOOD;
  if (loadFrac < ZONE_LIMITS.high) return ZONE.HIGH;
  return ZONE.DANGER;
}

/* --- tuning constants ------------------------------------------------------ */

const TUNE = {
  /** Rod tip travel as a fraction of blank length at full bend. */
  maxDeflectRatio: 0.34,
  /** Bisection bracket, as a multiple of breaking force. */
  solveCeil: 3.0,
  solveIters: 22,
  /** How fast the spool gives line per newton of overload, m/s/N. */
  slipGain: 0.055,
  /** A rough clutch grabs and lets go; a smooth one bleeds evenly. */
  slipJudder: 0.55,
  /** Line wear starts here (fraction of breaking force). */
  wearFrom: 0.62,
  wearRate: 0.55,
  /** Abrasion multiplier while the line is dragging across structure. */
  snagWear: 3.4,
  /** Hook works loose on a slack line. */
  hookSlackRate: 0.20,
  /** Hook tears out under sustained overload. */
  hookTearFrom: 0.78,
  hookTearRate: 0.42,
  /** Shape and depth of the retrieve penalty under load (never reaches zero;
   *  a slipping clutch is what actually stops you gaining line). */
  reelStallExp: 2.0,
  reelLoadPenalty: 0.6,
  /** Minimum line out; you cannot reel the fish into the rod tip. */
  minLineOut: 0.45,
  /** Landing distance. */
  landDist: 1.1,
};

export class RodSystem {
  constructor() {
    this.reset();
  }

  /**
   * @param {object} gear  { rod, reel, line } records from data/gear.js
   */
  configure(gear) {
    const { rod, reel, line } = gear;
    this.rod = rod;
    this.reel = reel;
    this.line = line;

    this.power = rod.power;                              // N for full bend
    this.maxDeflect = rod.length * TUNE.maxDeflectRatio;  // m of tip travel
    this.testN = line.test * G;                          // breaking force, N
    this.stretch = Math.max(line.stretch, 0.005);
    this.retrieveBase = reel.retrieve;
    this.dragMax = reel.drag;
    this.dragSmooth = reel.dragSmooth;

    // The clutch is NOT clamped to the line's strength. A reel that can apply
    // more drag than the line can take is exactly how anglers break off, and
    // taking that away would remove the central risk decision: winding the drag
    // up beats the fish faster and gets you closer to a snap. The UI warns via
    // `dragUnsafe` instead of the model quietly protecting the player.
    this.dragCeil = this.dragMax;
    this.setDragFrac(this.dragFrac ?? 0.55);
  }

  reset() {
    this.lineOut = 0;
    this.tension = 0;
    this.smoothTension = 0;
    this.bend = 0;
    this.tipGive = 0;
    this.slipRate = 0;
    this.lineIntegrity = 1;
    this.hookHold = 1;
    this.dragFrac = this.dragFrac ?? 0.55;
    this.drag = 0;
    this.zone = ZONE.SLACK;
    this.loadFrac = 0;
    this.broke = false;
    this.reelGain = 0;
    this.slipping = false;
    this._judder = 0;
    this.peakTension = 0;
  }

  /** Fresh line and a fresh hook, e.g. after a break-off or a landed fish. */
  respool(lineOut = 0) {
    this.lineOut = lineOut;
    this.tension = 0;
    this.smoothTension = 0;
    this.lineIntegrity = 1;
    this.hookHold = 1;
    this.broke = false;
    this.peakTension = 0;
    this.slipRate = 0;
    this.bend = 0;
  }

  setDragFrac(f) {
    this.dragFrac = clamp01(f);
    this.drag = this.dragCeil ? 0.06 * this.dragCeil + this.dragFrac * 0.94 * this.dragCeil : 0;
  }

  adjustDrag(delta) { this.setDragFrac(this.dragFrac + delta); }

  /* --- the solver --------------------------------------------------------- */

  /** Line extension at tension T (metres). */
  lineExtension(T) {
    return (T * this.stretch * Math.max(this.lineOut, TUNE.minLineOut)) / this.testN;
  }

  /** Rod tip deflection at tension T (metres). Saturating: progressive taper. */
  rodDeflection(T) {
    return this.maxDeflect * (1 - Math.exp(-T / this.power));
  }

  /**
   * Bisect for the tension that makes the two series springs absorb exactly
   * `overshoot` metres. Monotonic, so bisection always converges.
   */
  solveTension(overshoot) {
    if (overshoot <= 0) return 0;
    let lo = 0;
    let hi = this.testN * TUNE.solveCeil;
    // If even the ceiling cannot absorb the overshoot the line is past breaking
    // anyway; returning the ceiling lets the damage pass handle it.
    for (let i = 0; i < TUNE.solveIters; i++) {
      const mid = 0.5 * (lo + hi);
      const e = this.lineExtension(mid) + this.rodDeflection(mid);
      if (e < overshoot) lo = mid; else hi = mid;
    }
    return 0.5 * (lo + hi);
  }

  /**
   * One simulation step.
   *
   * @param {number} dt
   * @param {object} ctx
   *   fishDist   {number} metres from rod tip to hook
   *   reelInput  {number} 0..1 requested retrieve
   *   onStructure{boolean} line is currently rubbing on a snag
   *   extraLoad  {number} additional steady pull, e.g. current or dead weight
   * @returns {object} a per-step report the game layer reacts to
   */
  update(dt, ctx) {
    const {
      fishDist = 0,
      reelInput = 0,
      onStructure = false,
      extraLoad = 0,
      allowSlip = true,
    } = ctx;

    if (this.lineOut <= 0) this.lineOut = Math.max(fishDist, TUNE.minLineOut);

    // 1. Reel in. Two separate things are going on and conflating them is wrong:
    //    a loaded reel winds *slower* (the gearbox is fighting the fish), but a
    //    reel whose clutch is actually slipping wins *nothing* — line leaves the
    //    spool as fast as the handle puts it back. So the handle stays useful
    //    right up to the slip point, and goes dead the instant the drag gives.
    const load = this.drag > 0 ? clamp01(this.tension / this.drag) : 0;
    const efficiency = this.slipping
      ? 0
      : clamp(1 - TUNE.reelLoadPenalty * Math.pow(load, TUNE.reelStallExp), 0.22, 1);
    this.reelGain = reelInput * this.retrieveBase * efficiency;
    if (this.reelGain > 0) {
      this.lineOut = Math.max(TUNE.minLineOut, this.lineOut - this.reelGain * dt);
    }

    // 2. Solve the series springs for the tension the geometry demands.
    const overshoot = fishDist - this.lineOut;
    let T = this.solveTension(overshoot) + extraLoad;

    // 3. Drag clutch. Anything above the setting slips line off the spool; the
    //    resulting longer lineOut is what actually relieves the tension, so the
    //    ceiling is enforced by the feedback loop rather than by a clamp.
    this.slipping = false;
    this.slipRate = 0;
    if (allowSlip && T > this.drag && this.drag > 0) {
      const excess = T - this.drag;
      // A rough clutch stutters; jitter is deterministic in tension so it reads
      // as texture in the rumble/needle rather than as noise.
      this._judder += dt * (6 + excess * 0.4);
      const judder = 1 + (1 - this.dragSmooth) * TUNE.slipJudder * Math.sin(this._judder * 9.0);
      this.slipRate = excess * TUNE.slipGain * judder;
      this.lineOut += this.slipRate * dt;
      this.slipping = true;
      // Re-solve after paying out: the tension the player feels this frame is
      // the post-slip one, which is what a real drag delivers.
      T = this.solveTension(fishDist - this.lineOut) + extraLoad;
    }

    this.tension = T;
    this.peakTension = Math.max(this.peakTension, T);
    this.smoothTension = damp(this.smoothTension, T, 14, dt);
    this.loadFrac = this.testN > 0 ? T / this.testN : 0;
    this.zone = zoneFor(this.loadFrac);

    // 4. Rod bend, for the renderer and for the feel of the tension meter.
    this.tipGive = this.rodDeflection(T);
    this.bend = this.maxDeflect > 0 ? this.tipGive / this.maxDeflect : 0;

    // 5. Damage. Line wear is superlinear past the wear threshold, so sitting
    //    at 0.9 of breaking strain kills you much faster than sitting at 0.7.
    let snapped = false;
    if (this.loadFrac > TUNE.wearFrom) {
      const over = (this.loadFrac - TUNE.wearFrom) / (1 - TUNE.wearFrom);
      const abrasion = onStructure ? TUNE.snagWear * (1 - this.line.abrasion * 0.6) : 1;
      this.lineIntegrity -= over * over * TUNE.wearRate * abrasion * dt;
    } else if (onStructure) {
      this.lineIntegrity -= 0.18 * (1 - this.line.abrasion * 0.6) * dt;
    }
    this.lineIntegrity = clamp01(this.lineIntegrity);

    if (this.loadFrac >= 1 || this.lineIntegrity <= 0) {
      snapped = true;
      this.broke = true;
    }

    // 6. Hook hold. Two ways to lose a fish that is still attached: let the line
    //    go slack so the hook backs out, or bury the rod and tear the hole open.
    let hookWear = 0;
    if (this.loadFrac < ZONE_LIMITS.slack) {
      hookWear += TUNE.hookSlackRate * (1 - this.loadFrac / ZONE_LIMITS.slack);
    }
    if (this.loadFrac > TUNE.hookTearFrom) {
      hookWear += TUNE.hookTearRate * (this.loadFrac - TUNE.hookTearFrom) / (1 - TUNE.hookTearFrom);
    }
    if (hookWear > 0) this.hookHold = clamp01(this.hookHold - hookWear * dt);

    return {
      tension: this.tension,
      loadFrac: this.loadFrac,
      zone: this.zone,
      bend: this.bend,
      lineOut: this.lineOut,
      slipping: this.slipping,
      slipRate: this.slipRate,
      snapped,
      hookLost: this.hookHold <= 0,
      landed: this.lineOut <= TUNE.landDist && fishDist <= TUNE.landDist * 1.2,
      overloadedRod: this.tension > this.power * 2.4,
    };
  }

  /** Extra hook damage from a discrete event (a head-shake, a jump, a snag hit). */
  shockHook(amount) { this.hookHold = clamp01(this.hookHold - amount); }

  /** Damage the line directly, e.g. a rock strike during a run. */
  abrade(amount) { this.lineIntegrity = clamp01(this.lineIntegrity - amount); }

  /** Snapshot for the HUD, already normalised so the UI does no maths. */
  telemetry() {
    return {
      tension: this.tension,
      smooth: this.smoothTension,
      loadFrac: this.loadFrac,
      dragFrac: this.dragFrac,
      dragN: this.drag,
      dragLoad: this.drag > 0 ? clamp01(this.tension / this.drag) : 0,
      /** Clutch is set harder than the line can take: a surge will break you. */
      dragUnsafe: this.drag > this.testN * 0.8,
      dragVsLine: this.testN > 0 ? this.drag / this.testN : 0,
      zone: this.zone,
      bend: this.bend,
      lineOut: this.lineOut,
      lineIntegrity: this.lineIntegrity,
      hookHold: this.hookHold,
      slipping: this.slipping,
      slipRate: this.slipRate,
      testN: this.testN,
      peak: this.peakTension,
    };
  }
}

/**
 * Catenary sag for the visible line between two points.
 *
 * A taut line is a straight line; a slack one hangs. Rather than simulating a
 * rope we solve the sag depth from the tension directly — sag is what tension
 * *looks like*, so the player reads the physics off the screen before they read
 * the meter. Returns a sampled polyline.
 */
export function catenaryPoints(from, to, tension, weight = 1.0, segments = 18, out = []) {
  const dx = to.x - from.x, dy = to.y - from.y, dz = to.z - from.z;
  const span = Math.hypot(dx, dz) || 0.0001;
  // Sag falls off as 1/T, capped so a dead-slack line does not fall to infinity.
  const sag = Math.min(span * 0.42, (weight * span * span) / (8 * Math.max(tension, 0.55)));

  out.length = 0;
  for (let i = 0; i <= segments; i++) {
    const t = i / segments;
    const droop = 4 * sag * t * (1 - t);   // parabolic approximation of cosh
    out.push({
      x: from.x + dx * t,
      y: from.y + dy * t - droop,
      z: from.z + dz * t,
    });
  }
  return out;
}

/**
 * Rod blank shape under load. Returns spine points from butt to tip, bending in
 * the plane defined by the aim direction. `bend` is 0..1 from the solver.
 */
export function rodSpine(length, bend, segments = 10, out = []) {
  out.length = 0;
  // Deflection concentrated toward the tip — the classic fast-action curve.
  const k = bend * 1.35;
  for (let i = 0; i <= segments; i++) {
    const t = i / segments;
    const droop = k * Math.pow(t, 2.35);
    const along = length * t * (1 - 0.10 * droop * droop);
    out.push({ along, drop: droop * length * 0.30 });
  }
  return out;
}

export { TUNE as ROD_TUNE };

/**
 * Casting: charge, release, ballistic flight, splashdown, sink.
 *
 * The charge is a sawtooth that runs past 1.0 into an overload band. Releasing
 * inside the sweet spot gives full distance and tight accuracy; holding past it
 * gives you a backlash — more distance, much worse accuracy, and a chance of a
 * bird's nest that costs you the cast. That single curve is the whole risk
 * decision at the front of every fishing loop.
 */

import { clamp, clamp01, lerp } from '../core/loop.js';

export const CAST_PHASE = {
  IDLE: 'idle',
  CHARGING: 'charging',
  FLYING: 'flying',
  SINKING: 'sinking',
  SETTLED: 'settled',
};

const AIR_DRAG = 0.11;
const GRAVITY = 9.81;
/** Launch speed at full charge with a reference rod, m/s. */
const LAUNCH_SPEED = 22.5;

/** Charge ramps to 1.0 over this many seconds, then overloads. */
export const CHARGE_TIME = 1.15;
export const OVERLOAD_TIME = 0.45;
/** Releasing within this much of 1.0 counts as a perfect cast. */
export const PERFECT_BAND = 0.09;

export class CastSystem {
  constructor(rng) {
    this.rng = rng;
    this.phase = CAST_PHASE.IDLE;
    this.charge = 0;
    this.overload = 0;
    this.pos = { x: 0, y: 0, z: 0 };
    this.vel = { x: 0, y: 0, z: 0 };
    this.aimYaw = 0;
    this.aimPitch = 0.62;      // radians above horizontal
    this.sinkDepth = 0;
    this.targetDepth = 0;
    this.quality = 0;          // 0..1, how clean the release was
    this.backlash = false;
    this.distance = 0;
  }

  beginCharge() {
    if (this.phase !== CAST_PHASE.IDLE) return false;
    this.phase = CAST_PHASE.CHARGING;
    this.charge = 0;
    this.overload = 0;
    this.backlash = false;
    return true;
  }

  updateCharge(dt) {
    if (this.phase !== CAST_PHASE.CHARGING) return;
    if (this.charge < 1) {
      this.charge = Math.min(1, this.charge + dt / CHARGE_TIME);
    } else {
      this.overload = Math.min(1, this.overload + dt / OVERLOAD_TIME);
      // Held all the way through the overload band: the cast goes off by itself
      // and it goes off badly.
      if (this.overload >= 1) return 'auto-release';
    }
    return null;
  }

  /**
   * Release the cast.
   * @param {object} tip   rod tip world position
   * @param {object} gear  { rod, lure }
   * @param {number} wind  lateral wind, m/s
   */
  release(tip, gear, wind = 0) {
    if (this.phase !== CAST_PHASE.CHARGING) return null;

    const over = this.overload;
    const raw = this.charge + over * 0.28;                 // overload adds reach
    const nearPerfect = 1 - clamp01(Math.abs(this.charge - 1) / PERFECT_BAND);
    this.quality = over > 0 ? clamp01(nearPerfect * (1 - over * 0.9)) : nearPerfect;

    // Backlash chance climbs steeply through the overload band.
    this.backlash = over > 0 && this.rng.next() < over * over * 0.55;

    const rodPower = gear.rod.castPower;
    // A heavy sinking lure casts further than a bag of fluff.
    const lureMass = 0.45 + gear.lure.sink * 0.55;
    // Tuned so the starter bamboo rod reaches ~14 m and the top rod ~34 m,
    // which is the full width of the fishable water. Range goes as speed², so
    // this constant is sensitive — measure, don't guess.
    const speed = LAUNCH_SPEED * raw * rodPower * lerp(0.85, 1.12, lureMass);

    // Accuracy: spread in radians, worsened by wind, overload and a soft rod.
    const spread = (0.030 + (1 - this.quality) * 0.085 + over * 0.10)
                 * lerp(1.25, 0.85, gear.rod.castPower);
    const yaw = this.aimYaw + this.rng.normal(0, spread);
    const pitch = clamp(this.aimPitch + this.rng.normal(0, spread * 0.55), 0.12, 1.35);

    const horiz = Math.cos(pitch) * speed;
    this.pos = { x: tip.x, y: tip.y, z: tip.z };
    this.vel = {
      x: Math.sin(yaw) * horiz + wind * 0.35,
      y: Math.sin(pitch) * speed,
      z: Math.cos(yaw) * horiz,
    };

    if (this.backlash) {
      // Bird's nest: the spool locks mid-flight and the lure drops short.
      this.vel.x *= 0.34; this.vel.y *= 0.42; this.vel.z *= 0.34;
    }

    this.phase = CAST_PHASE.FLYING;
    this.charge = 0;
    this.overload = 0;
    return {
      power: raw,
      quality: this.quality,
      backlash: this.backlash,
      perfect: this.quality > 0.86 && !this.backlash,
    };
  }

  /**
   * Integrate flight. Returns 'splash' on the frame the lure hits the water.
   * @param {number} waterY  water surface height
   */
  updateFlight(dt, waterY, wind = 0) {
    if (this.phase !== CAST_PHASE.FLYING) return null;

    const v = this.vel;
    const speed = Math.hypot(v.x, v.y, v.z);
    const drag = AIR_DRAG * speed;
    v.x += (-drag * v.x + wind * 0.8) * dt;
    v.y += (-GRAVITY - drag * v.y) * dt;
    v.z += -drag * v.z * dt;

    this.pos.x += v.x * dt;
    this.pos.y += v.y * dt;
    this.pos.z += v.z * dt;

    if (this.pos.y <= waterY && v.y < 0) {
      this.pos.y = waterY;
      this.phase = CAST_PHASE.SINKING;
      this.sinkDepth = 0;
      this.distance = Math.hypot(this.pos.x, this.pos.z);
      return { event: 'splash', impact: speed, pos: { ...this.pos } };
    }
    // Landed on the bank.
    if (this.pos.y <= 0 && v.y < 0) {
      this.pos.y = 0;
      this.phase = CAST_PHASE.SETTLED;
      return { event: 'dryland', pos: { ...this.pos } };
    }
    return null;
  }

  /**
   * Sink the lure toward its working depth. Floating lures (popper, frog) stop
   * at the surface; bottom baits keep going until they find the bed.
   */
  updateSink(dt, lure, bedDepth) {
    if (this.phase !== CAST_PHASE.SINKING) return null;
    this.targetDepth = bedDepth * clamp01(lure.sink);
    const rate = 0.35 + lure.sink * 1.25;
    this.sinkDepth = Math.min(this.targetDepth, this.sinkDepth + rate * dt);
    if (this.sinkDepth >= this.targetDepth - 1e-3) {
      this.phase = CAST_PHASE.SETTLED;
      return { event: 'settled', depth: this.sinkDepth };
    }
    return null;
  }

  /** Retrieve pulls the lure back toward the angler and lifts it in the water. */
  retrieve(dt, rate, tipPos) {
    const dx = tipPos.x - this.pos.x;
    const dz = tipPos.z - this.pos.z;
    const d = Math.hypot(dx, dz);
    if (d < 1e-4) return 0;
    const move = Math.min(d, rate * dt);
    this.pos.x += (dx / d) * move;
    this.pos.z += (dz / d) * move;
    // Moving line lifts the lure; a fast retrieve fishes shallower.
    this.sinkDepth = Math.max(0, this.sinkDepth - rate * dt * 0.42);
    this.distance = Math.hypot(this.pos.x, this.pos.z);
    return move;
  }

  reset() {
    this.phase = CAST_PHASE.IDLE;
    this.charge = 0;
    this.overload = 0;
    this.sinkDepth = 0;
    this.backlash = false;
    this.vel = { x: 0, y: 0, z: 0 };
  }

  /** 0..1 meter fill for the HUD, plus whether we are in the danger band. */
  chargeMeter() {
    return {
      value: this.charge,
      overload: this.overload,
      inSweetSpot: this.charge >= 1 - PERFECT_BAND && this.overload === 0,
      charging: this.phase === CAST_PHASE.CHARGING,
    };
  }
}

/**
 * The fishing loop.
 *
 * This is the state machine that owns a single cast, from winding up to either
 * a fish in the net or a bare hook. It is the only place that knows about all
 * the subsystems at once, and it is deliberately headless — it never touches
 * three.js, the DOM or the clock. The renderer subscribes to its events and the
 * test harness drives it directly.
 *
 *   READY ─cast─> CHARGING ─release─> FLYING ─splash─> SINKING ─> FISHING
 *     ^                                                              │
 *     │                                                      hookset ▼
 *     └──── RESOLVE <── FIGHT <─────────────────────────────────  HOOKED
 */

import { EV } from '../core/events.js';
import { clamp, clamp01, damp } from '../core/loop.js';
import { CastSystem, CAST_PHASE } from '../physics/cast.js';
import { BiteSystem, BITE_STATE } from '../physics/bite.js';
import { RodSystem, ZONE } from '../physics/rod.js';
import { HookedFish, FIGHT_STATE } from './fish.js';
import { drawSpecies, rollFish, valueOf, xpOf, sizeClass } from './catchtable.js';

export const GAME_STATE = {
  READY:    'ready',
  CHARGING: 'charging',
  FLYING:   'flying',
  SINKING:  'sinking',
  FISHING:  'fishing',
  FIGHT:    'fight',
  RESOLVE:  'resolve',
};

/** How long the catch card stays up before the rod resets. */
const RESOLVE_HOLD = 2.6;

export class FishingGame {
  /**
   * @param {object} deps { rng, bus, state, world }
   */
  constructor({ rng, bus, state, world }) {
    this.rng = rng;
    this.bus = bus;
    this.state = state;
    this.world = world;

    this.cast = new CastSystem(rng.fork('cast'));
    this.bite = new BiteSystem(rng.fork('bite'), bus);
    this.rod = new RodSystem();
    this.fish = null;

    this.phase = GAME_STATE.READY;
    this.resolveTimer = 0;
    this.lastCatch = null;
    this.lureDepth = 0;
    this.lureDepthNorm = 0;
    this.bedDepth = 1;
    this.retrieveRate = 0;
    this.jerk = 0;
    this._prevReel = 0;
    this.tipPos = { x: 0, y: 1.9, z: 0 };
    this.hookedElapsed = 0;
    this.sessionSeconds = 0;

    this.refreshGear();
    this.bus.on(EV.GEAR_EQUIP, () => this.refreshGear());
  }

  refreshGear() {
    this.gear = this.state.gear();
    this.rod.configure(this.gear);
    this.tipPos.y = 1.05 + this.gear.rod.length * 0.55;
  }

  /* --- lure position helpers --------------------------------------------- */

  /** Normalised (u, v) of the lure across the castable water, for depthAt(). */
  lureUV() {
    const spot = this.state.spot;
    const maxCast = 34;
    const u = clamp01(this.cast.distance / maxCast);
    const v = clamp(this.cast.pos.x / 18, -1, 1);
    return { u, v, spot };
  }

  bedDepthAt() {
    const { u, v, spot } = this.lureUV();
    return Math.max(0.35, spot.depthAt(u, v) * spot.maxDepth);
  }

  /** Structures near the lure, expressed in metres relative to the rod tip. */
  structuresNear() {
    const spot = this.state.spot;
    const maxCast = 34;
    return spot.structure.map((s) => ({
      x: s.v * 18, z: s.u * maxCast, r: s.r * 12, kind: s.kind,
    }));
  }

  /* --- input entry points -------------------------------------------------- */

  beginCast() {
    if (this.phase !== GAME_STATE.READY) return false;
    if (!this.cast.beginCharge()) return false;
    this.phase = GAME_STATE.CHARGING;
    return true;
  }

  releaseCast() {
    if (this.phase !== GAME_STATE.CHARGING) return false;
    this.state.consumeBait();
    this.refreshGear();
    const result = this.cast.release(this.tipPos, this.gear, this.world.wind);
    if (!result) return false;
    this.phase = GAME_STATE.FLYING;
    this.state.data.stats.casts++;
    this.rod.respool(2.0);
    this.bite.reset();
    this.bus.emit(EV.CAST_START, result);
    if (result.backlash) {
      this.bus.emit(EV.TOAST, { text: 'Tali kusut! Lontaran tersekat.', kind: 'warn' });
    }
    return true;
  }

  /** The hookset. Timestamped by the input layer, so latency is honest. */
  strike() {
    this._strikeQueued = true;
  }

  aim(yaw, pitch) {
    this.cast.aimYaw = yaw;
    if (pitch != null) this.cast.aimPitch = pitch;
  }

  /** Give up on the current cast and wind everything back in. */
  reelInHard() {
    if (this.phase === GAME_STATE.FISHING || this.phase === GAME_STATE.SINKING) {
      this._finishCast('reeled-in');
    }
  }

  /* --- the tick ------------------------------------------------------------ */

  update(dt, input) {
    this.sessionSeconds += dt;
    this.state.data.stats.playSeconds = Math.round(this.sessionSeconds);

    const struck = this._strikeQueued === true;
    this._strikeQueued = false;

    // Drag adjustment is always live; a fight is often won on the clutch.
    if (input?.dragAxis) this.rod.adjustDrag(input.dragAxis * 0.45 * dt);

    // Rod-tip jerk: how violently the angler is changing retrieve. Cautious
    // fish notice.
    const reel = input?.reelAxis ?? 0;
    this.jerk = damp(this.jerk, Math.abs(reel - this._prevReel) / Math.max(dt, 1e-4) * 0.02, 8, dt);
    this._prevReel = reel;

    switch (this.phase) {
      case GAME_STATE.CHARGING: this._tickCharging(dt); break;
      case GAME_STATE.FLYING:   this._tickFlying(dt); break;
      case GAME_STATE.SINKING:  this._tickSinking(dt); break;
      case GAME_STATE.FISHING:  this._tickFishing(dt, reel, struck); break;
      case GAME_STATE.FIGHT:    this._tickFight(dt, reel, struck); break;
      case GAME_STATE.RESOLVE:  this._tickResolve(dt); break;
      default: break;
    }
  }

  _tickCharging(dt) {
    const r = this.cast.updateCharge(dt);
    if (r === 'auto-release') this.releaseCast();
  }

  _tickFlying(dt) {
    const r = this.cast.updateFlight(dt, 0, this.world.wind);
    if (!r) return;
    if (r.event === 'splash') {
      this.bedDepth = this.bedDepthAt();
      this.phase = GAME_STATE.SINKING;
      this.rod.respool(Math.max(2.0, this.cast.distance));
      this.bus.emit(EV.CAST_LAND, { pos: r.pos, impact: r.impact, distance: this.cast.distance });
      this.bus.emit(EV.SPLASH, { pos: r.pos, strength: clamp01(r.impact / 18) });
      this.bus.emit(EV.RIPPLE, { pos: r.pos, strength: clamp01(r.impact / 14) });
    } else if (r.event === 'dryland') {
      this.bus.emit(EV.TOAST, { text: 'Tersangkut di darat.', kind: 'warn' });
      this._finishCast('dryland');
    }
  }

  _tickSinking(dt) {
    const r = this.cast.updateSink(dt, this.gear.lure, this.bedDepth);
    this.lureDepth = this.cast.sinkDepth;
    this.lureDepthNorm = clamp01(this.lureDepth / Math.max(this.bedDepth, 0.1));
    if (r?.event === 'settled') {
      this.phase = GAME_STATE.FISHING;
      this.bite.begin();
      this.bus.emit(EV.LURE_SETTLED, { depth: this.lureDepth, bed: this.bedDepth });
    }
  }

  _tickFishing(dt, reel, struck) {
    // Retrieving moves the lure home and lifts it in the column.
    this.retrieveRate = reel * this.gear.reel.retrieve;
    if (this.retrieveRate > 0) {
      this.cast.retrieve(dt, this.retrieveRate, this.tipPos);
      this.rod.lineOut = Math.max(0.6, this.cast.distance);
      this.bedDepth = this.bedDepthAt();
      this.lureDepth = this.cast.sinkDepth;
      this.lureDepthNorm = clamp01(this.lureDepth / Math.max(this.bedDepth, 0.1));
      this.bus.emit(EV.RIPPLE, { pos: this.cast.pos, strength: 0.12 * reel });
    } else {
      // A settled bait keeps sinking slowly toward its working depth.
      this.cast.phase = CAST_PHASE.SETTLED;
    }

    // Reeled all the way back with nothing on: the cast is over.
    if (this.cast.distance <= 1.4) { this._finishCast('retrieved'); return; }

    // Keep the tension model live even with nothing on the end, so the meter
    // and the rod bend show the weight of the lure and the drag of the water.
    this.rod.lineOut = Math.max(0.6, this.cast.distance);
    this.rod.update(dt, {
      fishDist: this.rod.lineOut,
      reelInput: 0,
      extraLoad: 0.9 + reel * 4.5 * (0.4 + this.gear.lure.sink),
      allowSlip: false,
    });

    const spot = this.state.spot;
    const ev = this.bite.update(dt, {
      lure: this.gear.lure,
      line: this.gear.line,
      spot,
      lureDepthNorm: this.lureDepthNorm,
      retrieveRate: reel,
      noise: this.world.surfaceNoise() + this.gear.lure.noise * reel,
      spotActivity: this.world.activity(),
      jerk: this.jerk,
      struck,
      drawCandidate: () => this._drawCandidate(),
    });

    if (ev) this._handleBiteEvent(ev);
  }

  _drawCandidate() {
    return drawSpecies(this.rng, {
      spot: this.state.spot,
      phase: this.world.phase,
      weather: this.world.weather,
      lure: this.gear.lure,
      lureDepthNorm: this.lureDepthNorm,
      level: this.state.level,
      activityBonus: this.world.activity(),
    });
  }

  _handleBiteEvent(ev) {
    switch (ev.type) {
      case 'interest':
        this.bus.emit(EV.INTEREST, { species: ev.species });
        break;
      case 'nibble':
        this.state.data.stats.bites++;
        this.bus.emit(EV.NIBBLE, { species: ev.species, remaining: ev.remaining });
        this.bus.emit(EV.RIPPLE, { pos: this.cast.pos, strength: 0.22 });
        break;
      case 'committing':
        this.bus.emit(EV.BITE_ON, { species: ev.species, phase: 'committing' });
        break;
      case 'bite':
        this.bus.emit(EV.BITE_ON, { species: ev.species, window: ev.window, phase: 'window' });
        this.bus.emit(EV.RIPPLE, { pos: this.cast.pos, strength: 0.45 });
        break;
      case 'hooked':
        this._beginFight(ev.species, ev.quality);
        break;
      case 'missed':
        this.state.registerLoss('missed');
        this.bus.emit(EV.BITE_MISSED, { species: ev.species, reason: ev.reason });
        this.bus.emit(EV.TOAST, { text: 'Terlepas — sambaran lambat.', kind: 'miss' });
        break;
      case 'spooked':
        this.state.registerLoss('spooked');
        this.bus.emit(EV.SPOOKED, { species: ev.species, reason: ev.reason });
        if (ev.reason === 'struck-early') {
          this.bus.emit(EV.TOAST, { text: 'Terlalu awal! Ikan lari.', kind: 'miss' });
        }
        break;
      case 'whiff':
        this.bus.emit(EV.HOOKSET_EARLY, {});
        break;
      default: break;
    }
  }

  _beginFight(species, quality) {
    const fish = rollFish(this.rng, species, {
      sizeBias: this.gear.lure.sizeBias, luck: this.state.luck(),
    });
    this.pending = fish;
    this.fish = new HookedFish(fish, this.rng.fork(`fight:${Date.now()}`), {
      hookQuality: quality,
      startDist: Math.max(3, this.cast.distance),
      startDepth: this.lureDepth,
      // The fish is where the lure is, not on the centreline. Without this the
      // fish never starts near the cover it is supposed to run for.
      startLateral: this.cast.pos.x,
    });
    this.rod.respool(Math.max(3, this.cast.distance));
    this.rod.hookHold = clamp(0.35 + quality * 0.65, 0.2, 1);
    this.phase = GAME_STATE.FIGHT;
    this.hookedElapsed = 0;
    this.state.data.stats.hooked++;
    this.bus.emit(EV.HOOKED, { fish, quality });
    this.bus.emit(EV.FIGHT_START, { fish, quality, telemetry: this.fish.telemetry() });
    this.bus.emit(EV.SPLASH, { pos: this.cast.pos, strength: 0.6 });
  }

  _tickFight(dt, reel, struck) {
    this.hookedElapsed += dt;
    const fish = this.fish;

    // 1. Fish decides and pulls.
    const fr = fish.update(dt, {
      tension: this.rod.tension,
      loadFrac: this.rod.loadFrac,
      structures: this.structuresNear(),
      maxDepth: this.bedDepth,
    });

    for (const e of fr.events) {
      if (e.type === 'hookShock') this.rod.shockHook(e.amount);
      else if (e.type === 'jump') {
        this.bus.emit(EV.FISH_JUMP, { fish: fish.telemetry() });
        this.bus.emit(EV.SPLASH, { pos: this.cast.pos, strength: 0.8 });
      } else if (e.type === 'splash') {
        this.bus.emit(EV.SPLASH, { pos: this.cast.pos, strength: 0.7 });
        this.bus.emit(EV.RIPPLE, { pos: this.cast.pos, strength: 0.6 });
      } else if (e.type === 'state') {
        this.bus.emit(EV.FIGHT_STATE, { state: e.state, fish: fish.telemetry() });
      } else if (e.type === 'structureHit') {
        this.rod.abrade(0.06);
        this.bus.emit(EV.SNAGGED, { snag: e.snag });
      }
    }

    // 2. The fish has moved; the rod now solves the tension that the new
    //    geometry implies. Nothing is added on top — the pull already showed up
    //    as distance, and distance is what stretches line.
    const report = this.rod.update(dt, {
      fishDist: fish.dist,
      reelInput: reel,
      onStructure: fr.onStructure,
    });

    // 3. Outcomes.
    if (report.snapped) { this._loseFish('snap'); return; }
    if (report.hookLost) { this._loseFish('hook'); return; }
    if (fr.onStructure && this.rod.lineIntegrity < 0.12) { this._loseFish('snag'); return; }

    // Landing: the fish is at the rod tip and can be lifted — either because it
    // has nothing left, or because the tackle simply out-guns it. Without that
    // second clause a 200 g Tilapia on a 30 lb braid would still need a full
    // exhaustion fight, which is nonsense.
    const outgunned = fish.maxForce < this.rod.drag * 0.8;
    if (fish.dist <= 1.7 && (fish.stamina < 0.30 || outgunned || fish.state === FIGHT_STATE.BEATEN)) {
      this._landFish();
      return;
    }

    // A hookset during a fight is a "pump" — it costs hook hold for nothing.
    if (struck) this.rod.shockHook(0.05);

    if (report.overloadedRod) this.bus.emit(EV.ROD_OVERLOAD, { tension: report.tension });
    this.bus.emit(EV.RIPPLE, { pos: this.cast.pos, strength: 0.05 + fr.pull / fish.maxForce * 0.1 });
  }

  _landFish() {
    const fish = this.pending;
    const phaseId = this.world.phase.id;
    const value = valueOf(fish);
    const xp = xpOf(fish);
    const reward = this.state.recordCatch(fish, { value, xp, phase: phaseId });

    this.lastCatch = {
      ...fish, value, xp, ...reward,
      sizeClass: sizeClass(fish),
      fightSeconds: Math.round(this.hookedElapsed * 10) / 10,
      peakTension: Math.round(this.rod.peakTension * 10) / 10,
      spot: this.state.data.spot,
      phase: phaseId,
    };

    this.bus.emit(EV.LANDED, this.lastCatch);
    this.bus.emit(EV.SPLASH, { pos: this.cast.pos, strength: 0.9 });
    this._enterResolve();
  }

  _loseFish(kind) {
    const reasons = {
      snap: 'Tali putus!',
      hook: 'Mata kail terlucut!',
      snag: 'Tersangkut — ikan bawa ke reba.',
    };
    this.state.registerLoss(kind === 'snap' ? 'snap' : 'lost');
    if (kind === 'snap') this.bus.emit(EV.LINE_SNAP, { tension: this.rod.tension });
    else this.bus.emit(EV.HOOK_LOST, { kind });
    this.bus.emit(EV.TOAST, { text: reasons[kind] ?? 'Ikan terlepas.', kind: 'fail' });
    this.lastCatch = { lost: true, kind, species: this.fish?.species ?? null };
    this._enterResolve();
  }

  _enterResolve() {
    this.phase = GAME_STATE.RESOLVE;
    this.resolveTimer = RESOLVE_HOLD;
    this.fish = null;
    this.pending = null;
    this.bus.emit(EV.FIGHT_END, this.lastCatch);
  }

  _tickResolve(dt) {
    this.resolveTimer -= dt;
    if (this.resolveTimer <= 0) this._finishCast('resolved');
  }

  _finishCast(reason) {
    this.phase = GAME_STATE.READY;
    this.cast.reset();
    this.bite.reset();
    this.rod.respool(0);
    this.fish = null;
    this.retrieveRate = 0;
    this.lureDepth = 0;
    this.bus.emit(EV.REEL_IN, { reason });
  }

  /* --- telemetry ----------------------------------------------------------- */

  telemetry() {
    return {
      phase: this.phase,
      cast: this.cast.chargeMeter(),
      castDistance: this.cast.distance,
      lureDepth: this.lureDepth,
      lureDepthNorm: this.lureDepthNorm,
      bedDepth: this.bedDepth,
      rod: this.rod.telemetry(),
      bite: this.bite.telemetry(),
      fish: this.fish?.telemetry() ?? null,
      lastCatch: this.lastCatch,
      gear: this.gear,
      fightSeconds: this.hookedElapsed,
    };
  }
}

export { ZONE, BITE_STATE, FIGHT_STATE, CAST_PHASE };

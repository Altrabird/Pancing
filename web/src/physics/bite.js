/**
 * Bite detection.
 *
 * A bite is not a coin flip on a timer. It is an attraction budget that fills
 * or empties every tick based on whether what the player is doing matches what
 * the fish that is actually down there wants:
 *
 *   SEARCHING  no candidate. Attraction accumulates from presentation quality.
 *   INTEREST   a specific fish has been drawn from the catch table and is
 *              inspecting. Bad presentation now loses it.
 *   NIBBLING   discrete taps. The bobber twitches. Striking here is too early.
 *   COMMITTED  the hookset window. Species-dependent, 320 ms for a Toman up to
 *              1.4 s for a prawn. Strike quality is how centred you were.
 *   SPOOKED    cooldown; that fish is gone and the swim is quiet for a while.
 *
 * Presentation is scored from four independent factors, all multiplicative so a
 * single bad choice can kill a bite outright:
 *   - lure match     from the species lure table
 *   - depth match    how close the lure sits to the species' preferred band
 *   - action match   whether the retrieve suits the species' aggression
 *   - stealth        line visibility against water clarity, plus noise
 */

import { clamp, clamp01, damp, smoothstep } from '../core/loop.js';
import { LURE_MISMATCH } from '../data/species.js';

export const BITE_STATE = {
  IDLE: 'idle',
  SEARCHING: 'searching',
  INTEREST: 'interest',
  NIBBLING: 'nibbling',
  COMMITTED: 'committed',
  HOOKED: 'hooked',
  SPOOKED: 'spooked',
};

const TUNE = {
  /* Pacing. Measured against a good presentation these give roughly 4 s to
   * interest and 5–6 s more to the first nibble — about a 10 s bite cycle,
   * which leaves real waiting in the game without it becoming a screensaver.
   * A poor presentation scales both directly, so a wrong lure can stretch the
   * same cycle past a minute. */
  /** Attraction needed before a candidate fish is drawn. */
  interestThreshold: 1.6,
  /** Attraction needed to move from inspecting to actually mouthing the bait. */
  commitThreshold: 3.2,
  /** Base fill rate; species `bite.speed` scales it. */
  fillRate: 0.62,
  /** Decay when presentation is poor. */
  decayRate: 0.55,
  /** How long a spooked swim stays quiet, seconds. */
  spookCooldown: [3.5, 7.0],
  /** Gap between individual nibble taps. */
  nibbleGap: [0.35, 1.10],
  /** A tap lasts this long visually. */
  tapDuration: 0.16,
  /** Striking during a tap does not just miss — it spooks the fish. */
  earlyStrikeSpook: 0.75,
};

export class BiteSystem {
  /**
   * @param {import('../core/rng.js').RNG} rng
   * @param {import('../core/events.js').EventBus} bus
   */
  constructor(rng, bus) {
    this.rng = rng;
    this.bus = bus;
    this.reset();
  }

  reset() {
    this.state = BITE_STATE.IDLE;
    this.attraction = 0;
    this.candidate = null;      // species record being courted
    this.timer = 0;
    this.nibblesLeft = 0;
    this.nextTap = 0;
    this.tapTimer = 0;
    this.tapping = false;
    this.windowLeft = 0;
    this.windowTotal = 0;
    this.cooldown = 0;
    this.presentation = 0;
    this.lastScore = null;
    this.strikeResult = null;
    this.timeSinceCast = 0;
  }

  /** Called when the lure settles; the swim starts paying attention. */
  begin() {
    if (this.state === BITE_STATE.SPOOKED && this.cooldown > 0) return;
    this.state = BITE_STATE.SEARCHING;
    this.attraction = 0;
    this.candidate = null;
    this.timeSinceCast = 0;
  }

  /**
   * Score how good the current presentation is, 0..~2.
   * Exposed separately so the HUD can show the player *why* nothing is biting.
   */
  scorePresentation(ctx) {
    const { species, lure, line, spot, lureDepthNorm, retrieveRate, noise } = ctx;

    // 1. Does this fish want this lure at all?
    const lureMatch = species
      ? (species.lures ? (species.lures[lure.id] ?? LURE_MISMATCH) : 1.0)
      : 1.0;

    // 2. Is the bait in the right part of the water column?
    let depthMatch = 1;
    if (species) {
      const [lo, hi] = species.depth;
      if (lureDepthNorm < lo) depthMatch = 1 - smoothstep(0, lo + 0.18, lo - lureDepthNorm);
      else if (lureDepthNorm > hi) depthMatch = 1 - smoothstep(0, (1 - hi) + 0.18, lureDepthNorm - hi);
      depthMatch = clamp(depthMatch, 0.08, 1);
    }

    // 3. Does the retrieve suit the fish? Predators want movement; bottom
    //    feeders want the bait to sit still. `lure.action` is how much movement
    //    the lure generates on its own.
    const motion = clamp01(retrieveRate * 0.9 + lure.action * 0.8);
    const wantsMotion = species ? clamp01(species.fight.aggression * 0.75 + 0.12) : 0.5;
    const actionMatch = 1 - Math.abs(motion - wantsMotion) * 0.85;

    // 4. Stealth. Visible line in clear water on a cautious fish is the classic
    //    reason a good spot goes dead.
    const caution = species ? species.bite.caution : 0.35;
    const seen = line.visibility * spot.waterClarity;
    const stealth = clamp(1 - seen * caution * 1.35 - noise * caution * 0.8, 0.10, 1);

    const total = lureMatch * depthMatch * clamp(actionMatch, 0.12, 1) * stealth;
    this.lastScore = { lureMatch, depthMatch, actionMatch, stealth, total };
    return this.lastScore;
  }

  /**
   * @param {number} dt
   * @param {object} ctx  see scorePresentation, plus:
   *    drawCandidate  {()=>species|null}  pulls from the catch table on demand
   *    struck         {boolean}           player hit the strike this tick
   *    lureMoving     {boolean}
   * @returns {object|null} an event descriptor when something happens
   */
  update(dt, ctx) {
    this.timeSinceCast += dt;

    if (this.cooldown > 0) {
      this.cooldown -= dt;
      if (this.cooldown <= 0 && this.state === BITE_STATE.SPOOKED) {
        this.state = BITE_STATE.SEARCHING;
        this.attraction = 0;
      }
    }

    if (this.state === BITE_STATE.IDLE || this.state === BITE_STATE.HOOKED) return null;

    // Striking with nothing there costs you: it yanks the lure and puts the
    // swim on edge. Cheap to do, so it needs a cost or strike-spam wins.
    if (ctx.struck && (this.state === BITE_STATE.SEARCHING || this.state === BITE_STATE.INTEREST)) {
      const had = this.state === BITE_STATE.INTEREST;
      this.attraction = Math.max(0, this.attraction - (had ? 1.6 : 0.6));
      if (had) return this._spook('premature');
      return { type: 'whiff' };
    }

    const score = this.scorePresentation({ ...ctx, species: this.candidate });
    this.presentation = damp(this.presentation, score.total, 6, dt);

    switch (this.state) {
      case BITE_STATE.SEARCHING:  return this._searching(dt, ctx, score);
      case BITE_STATE.INTEREST:   return this._interest(dt, ctx, score);
      case BITE_STATE.NIBBLING:   return this._nibbling(dt, ctx);
      case BITE_STATE.COMMITTED:  return this._committed(dt, ctx);
      default: return null;
    }
  }

  _searching(dt, ctx, score) {
    // Nothing specific is down there yet, so score against a neutral fish and
    // let the spot's own richness set the pace.
    const rate = TUNE.fillRate * score.total * (0.7 + ctx.spotActivity * 0.6);
    this.attraction += rate * dt;

    if (this.attraction >= TUNE.interestThreshold) {
      const species = ctx.drawCandidate();
      if (!species) { this.attraction = 0; return null; }
      this.candidate = species;
      this.attraction = TUNE.interestThreshold;
      this.state = BITE_STATE.INTEREST;
      this.timer = 0;
      return { type: 'interest', species };
    }
    return null;
  }

  _interest(dt, ctx, score) {
    this.timer += dt;
    const sp = this.candidate;
    const speed = sp.bite.speed;

    // Now the score is against a real fish, and a mismatched lure actively
    // repels rather than merely failing to attract.
    if (score.total < 0.35) {
      this.attraction -= TUNE.decayRate * (0.35 - score.total) * 4 * dt;
      if (this.attraction <= 0.15) return this._spook('lost-interest');
    } else {
      this.attraction += TUNE.fillRate * score.total * speed * dt;
    }

    // A sudden jerk of the rod while a cautious fish is inspecting scares it.
    if (ctx.jerk > 0.6 && this.rng.next() < sp.bite.caution * ctx.jerk * dt * 3) {
      return this._spook('startled');
    }

    if (this.attraction >= TUNE.commitThreshold) {
      const [lo, hi] = sp.bite.nibbles;
      // Cautious fish test the bait more before committing.
      const extra = this.rng.next() < sp.bite.caution ? 1 : 0;
      this.nibblesLeft = this.rng.int(lo, hi) + extra;
      this.state = BITE_STATE.NIBBLING;
      this.nextTap = this.rng.float(...TUNE.nibbleGap) * 0.5;
      this.tapTimer = 0;
      this.tapping = false;
      return { type: 'committing', species: sp };
    }
    return null;
  }

  _nibbling(dt, ctx) {
    const sp = this.candidate;

    if (ctx.struck) {
      // Struck on a tap. Classic beginner mistake — the fish has the bait in
      // its lips, not its throat.
      if (this.rng.next() < TUNE.earlyStrikeSpook) return this._spook('struck-early');
      return this._spook('struck-early');
    }

    if (this.tapping) {
      this.tapTimer -= dt;
      if (this.tapTimer <= 0) {
        this.tapping = false;
        this.nextTap = this.rng.float(...TUNE.nibbleGap);
        this.nibblesLeft--;
        if (this.nibblesLeft <= 0) {
          this.state = BITE_STATE.COMMITTED;
          this.windowTotal = sp.bite.window;
          this.windowLeft = this.windowTotal;
          return { type: 'bite', species: sp, window: this.windowTotal };
        }
      }
      return null;
    }

    this.nextTap -= dt;
    if (this.nextTap <= 0) {
      this.tapping = true;
      this.tapTimer = TUNE.tapDuration;
      return { type: 'nibble', species: sp, remaining: this.nibblesLeft };
    }
    return null;
  }

  _committed(dt, ctx) {
    const sp = this.candidate;
    this.windowLeft -= dt;

    if (ctx.struck) {
      // Quality peaks slightly after the window opens — the fish needs a beat to
      // turn with the bait — and falls off toward the end.
      const elapsed = this.windowTotal - this.windowLeft;
      const ideal = this.windowTotal * 0.42;
      const off = Math.abs(elapsed - ideal) / (this.windowTotal * 0.62);
      const quality = clamp01(1 - off * off);
      this.state = BITE_STATE.HOOKED;
      this.strikeResult = { quality, elapsed, ideal };
      return { type: 'hooked', species: sp, quality, timing: elapsed };
    }

    if (this.windowLeft <= 0) {
      // Too slow. The fish spits it and is now suspicious of this bait.
      return this._spook('too-slow', true);
    }
    return null;
  }

  _spook(reason, missed = false) {
    const sp = this.candidate;
    const [lo, hi] = TUNE.spookCooldown;
    this.state = BITE_STATE.SPOOKED;
    this.cooldown = this.rng.float(lo, hi);
    this.attraction = 0;
    this.candidate = null;
    this.tapping = false;
    this.nibblesLeft = 0;
    return { type: missed ? 'missed' : 'spooked', reason, species: sp };
  }

  /** Snapshot for the HUD. */
  telemetry() {
    return {
      state: this.state,
      attraction: this.attraction,
      attractionPct: clamp01(this.attraction / TUNE.commitThreshold),
      presentation: this.presentation,
      score: this.lastScore,
      candidate: this.candidate,
      tapping: this.tapping,
      windowLeft: this.windowLeft,
      windowPct: this.windowTotal > 0 ? clamp01(this.windowLeft / this.windowTotal) : 0,
      cooldown: this.cooldown,
      nibblesLeft: this.nibblesLeft,
    };
  }
}

export { TUNE as BITE_TUNE };

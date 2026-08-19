/**
 * Fixed-timestep game loop with render interpolation.
 *
 * The tension solver is a stiff spring system — it is only stable at a fixed
 * dt. So simulation runs at a locked 120 Hz accumulator while rendering runs
 * free at display rate and interpolates. This is also why the headless harness
 * can step the exact same simulation without a browser: it calls `step()`
 * directly and never touches the render half.
 */

export const FIXED_DT = 1 / 120;
const MAX_FRAME = 0.25;      // never simulate more than 250 ms in one frame
const MAX_STEPS = 40;        // hard cap so a stalled tab cannot spiral

export class GameLoop {
  /**
   * @param {(dt:number, t:number)=>void} step   fixed-rate simulation tick
   * @param {(alpha:number, dt:number)=>void} render  frame draw; alpha is the
   *        0..1 blend between the previous and current simulation state
   */
  constructor(step, render) {
    this.step = step;
    this.render = render;
    this.accumulator = 0;
    this.simTime = 0;
    this.frameId = 0;
    this.running = false;
    this.lastNow = 0;
    this.timeScale = 1;
    this.stats = { fps: 0, stepsLastFrame: 0, frameMs: 0, simMs: 0 };
    this._fpsAccum = 0;
    this._fpsFrames = 0;
    this._tick = this._tick.bind(this);
  }

  start() {
    if (this.running) return;
    this.running = true;
    this.lastNow = performance.now();
    this.frameId = requestAnimationFrame(this._tick);
  }

  stop() {
    this.running = false;
    if (this.frameId) cancelAnimationFrame(this.frameId);
    this.frameId = 0;
  }

  _tick(now) {
    if (!this.running) return;
    this.frameId = requestAnimationFrame(this._tick);

    const frameStart = now;
    let frame = (now - this.lastNow) / 1000;
    this.lastNow = now;
    if (!Number.isFinite(frame) || frame < 0) frame = 0;
    if (frame > MAX_FRAME) frame = MAX_FRAME;   // tab was backgrounded; drop the debt

    this.accumulator += frame * this.timeScale;

    const simStart = performance.now();
    let steps = 0;
    while (this.accumulator >= FIXED_DT && steps < MAX_STEPS) {
      this.step(FIXED_DT, this.simTime);
      this.simTime += FIXED_DT;
      this.accumulator -= FIXED_DT;
      steps++;
    }
    if (steps >= MAX_STEPS) this.accumulator = 0;  // give up on the backlog
    this.stats.simMs = performance.now() - simStart;
    this.stats.stepsLastFrame = steps;

    const alpha = this.accumulator / FIXED_DT;
    this.render(alpha, frame);

    this.stats.frameMs = performance.now() - frameStart;
    this._fpsAccum += frame;
    this._fpsFrames++;
    if (this._fpsAccum >= 0.5) {
      this.stats.fps = this._fpsFrames / this._fpsAccum;
      this._fpsAccum = 0;
      this._fpsFrames = 0;
    }
  }

  /** Advance the simulation without rendering. Used by the headless harness. */
  runHeadless(seconds) {
    const steps = Math.round(seconds / FIXED_DT);
    for (let i = 0; i < steps; i++) {
      this.step(FIXED_DT, this.simTime);
      this.simTime += FIXED_DT;
    }
    return steps;
  }
}

/* --- interpolation helpers used by renderers ------------------------------ */

export function lerp(a, b, t) { return a + (b - a) * t; }

export function clamp(x, lo, hi) { return x < lo ? lo : x > hi ? hi : x; }

export function clamp01(x) { return x < 0 ? 0 : x > 1 ? 1 : x; }

export function smoothstep(a, b, x) {
  const t = clamp01((x - a) / (b - a));
  return t * t * (3 - 2 * t);
}

/** Frame-rate independent exponential approach. `rate` is per second. */
export function damp(current, target, rate, dt) {
  return target + (current - target) * Math.exp(-rate * dt);
}

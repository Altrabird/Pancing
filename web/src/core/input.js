/**
 * Unified input.
 *
 * The game needs three things: a hold-to-charge action (cast), a fast discrete
 * action (hookset), and a continuous analogue axis (reel speed). Mouse, touch
 * and keyboard all funnel into the same three, so the fight logic never asks
 * what device it is on.
 *
 * Hookset latency matters — the bite window can be 320 ms — so the strike is
 * timestamped at the DOM event, not sampled at the next simulation tick.
 */

export class Input {
  constructor(target = window) {
    this.target = target;
    this.keys = new Set();

    /** Rising-edge action flags, consumed by the game and cleared each tick. */
    this.pressed = { strike: false, cast: false, release: false };

    this.pointer = { x: 0, y: 0, nx: 0, ny: 0, down: false, dragX: 0, dragY: 0 };
    this.reelAxis = 0;          // 0..1 desired retrieve rate
    this.dragAxis = 0;          // -1..1 drag adjustment this tick
    this.holdTime = 0;          // seconds the cast button has been held
    this.holding = false;
    this.strikeStamp = 0;       // performance.now() of the last strike
    this.enabled = true;

    this._bind();
  }

  _bind() {
    const t = this.target;
    const el = t.document ? t.document : t;

    this._onKeyDown = (e) => {
      if (!this.enabled) return;
      if (e.repeat) { return; }
      const k = e.key.toLowerCase();
      this.keys.add(k);
      if (k === ' ' || k === 'spacebar') {
        e.preventDefault();
        this._beginHold();
      }
      if (k === 'enter' || k === 'e') this.strike();
    };

    this._onKeyUp = (e) => {
      if (!this.enabled) return;
      const k = e.key.toLowerCase();
      this.keys.delete(k);
      if (k === ' ' || k === 'spacebar') this._endHold();
    };

    this._onPointerDown = (e) => {
      if (!this.enabled) return;
      if (e.target && e.target.closest && e.target.closest('[data-ui]')) return;
      this.pointer.down = true;
      this._setPointer(e);
      this.pointer.dragX = 0;
      this.pointer.dragY = 0;
      this._lastX = e.clientX;
      this._lastY = e.clientY;
      this._beginHold();
      if (e.pointerId != null && el.setPointerCapture) {
        try { el.setPointerCapture(e.pointerId); } catch { /* not capturable */ }
      }
    };

    this._onPointerMove = (e) => {
      if (!this.enabled) return;
      this._setPointer(e);
      if (this.pointer.down) {
        this.pointer.dragX += e.clientX - (this._lastX ?? e.clientX);
        this.pointer.dragY += e.clientY - (this._lastY ?? e.clientY);
      }
      this._lastX = e.clientX;
      this._lastY = e.clientY;
    };

    this._onPointerUp = () => {
      if (!this.enabled) return;
      this.pointer.down = false;
      this._endHold();
    };

    this._onContext = (e) => { if (this.enabled) e.preventDefault(); };
    this._onBlur = () => { this.keys.clear(); this.holding = false; this.pointer.down = false; };

    t.addEventListener('keydown', this._onKeyDown);
    t.addEventListener('keyup', this._onKeyUp);
    t.addEventListener('blur', this._onBlur);
    el.addEventListener('pointerdown', this._onPointerDown);
    el.addEventListener('pointermove', this._onPointerMove);
    el.addEventListener('pointerup', this._onPointerUp);
    el.addEventListener('pointercancel', this._onPointerUp);
    el.addEventListener('contextmenu', this._onContext);
  }

  _setPointer(e) {
    this.pointer.x = e.clientX;
    this.pointer.y = e.clientY;
    const w = this.target.innerWidth || 1;
    const h = this.target.innerHeight || 1;
    this.pointer.nx = (e.clientX / w) * 2 - 1;
    this.pointer.ny = -((e.clientY / h) * 2 - 1);
  }

  _beginHold() {
    if (this.holding) return;
    this.holding = true;
    this.holdTime = 0;
    this.pressed.cast = true;
  }

  _endHold() {
    if (!this.holding) return;
    this.holding = false;
    this.pressed.release = true;
  }

  /**
   * Register a strike. Called from the DOM event directly so the timestamp is
   * the real one, and separately exposed so on-screen buttons can call it.
   */
  strike() {
    this.pressed.strike = true;
    this.strikeStamp = performance.now();
  }

  /** Sampled once per simulation tick, before systems read the input. */
  update(dt) {
    if (this.holding) this.holdTime += dt;

    // Reel: hold right mouse / W / ArrowUp, or drag upward on touch.
    let axis = 0;
    if (this.keys.has('w') || this.keys.has('arrowup')) axis = 1;
    if (this.keys.has('shift') && axis > 0) axis = 1;         // burn drag, fast reel
    if (this.pointer.down && !this.holding) axis = Math.max(axis, 0.85);
    this.reelAxis = axis;

    // Drag clutch adjustment: A/D or ArrowLeft/Right, applied per second.
    let d = 0;
    if (this.keys.has('a') || this.keys.has('arrowleft')) d -= 1;
    if (this.keys.has('d') || this.keys.has('arrowright')) d += 1;
    this.dragAxis = d;
  }

  /** Clear rising-edge flags. Called at the very end of a simulation tick. */
  endFrame() {
    this.pressed.strike = false;
    this.pressed.cast = false;
    this.pressed.release = false;
  }

  dispose() {
    const t = this.target;
    const el = t.document ? t.document : t;
    t.removeEventListener('keydown', this._onKeyDown);
    t.removeEventListener('keyup', this._onKeyUp);
    t.removeEventListener('blur', this._onBlur);
    el.removeEventListener('pointerdown', this._onPointerDown);
    el.removeEventListener('pointermove', this._onPointerMove);
    el.removeEventListener('pointerup', this._onPointerUp);
    el.removeEventListener('pointercancel', this._onPointerUp);
    el.removeEventListener('contextmenu', this._onContext);
  }
}

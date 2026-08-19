/**
 * Minimal synchronous event bus.
 *
 * Everything that crosses a system boundary goes through here: the physics
 * layer never imports the UI, the UI never imports the fight AI. That keeps
 * the simulation headless-testable — the test harness subscribes to the same
 * events the HUD does.
 */

export class EventBus {
  constructor() {
    this.handlers = new Map();
    this.anyHandlers = new Set();
  }

  on(type, fn) {
    if (!this.handlers.has(type)) this.handlers.set(type, new Set());
    this.handlers.get(type).add(fn);
    return () => this.off(type, fn);
  }

  once(type, fn) {
    const off = this.on(type, (payload) => { off(); fn(payload); });
    return off;
  }

  off(type, fn) {
    this.handlers.get(type)?.delete(fn);
  }

  /** Subscribe to every event; used by the debug overlay and the test harness. */
  onAny(fn) {
    this.anyHandlers.add(fn);
    return () => this.anyHandlers.delete(fn);
  }

  emit(type, payload) {
    const set = this.handlers.get(type);
    if (set) {
      // Copy first: handlers commonly unsubscribe themselves mid-dispatch.
      for (const fn of [...set]) {
        try { fn(payload, type); }
        catch (err) { console.error(`[events] handler for "${type}" threw`, err); }
      }
    }
    for (const fn of [...this.anyHandlers]) {
      try { fn(payload, type); }
      catch (err) { console.error('[events] wildcard handler threw', err); }
    }
  }

  clear() { this.handlers.clear(); this.anyHandlers.clear(); }
}

/** Canonical event names, so typos fail loudly at import time instead of silently. */
export const EV = {
  // cast / line
  CAST_START: 'cast:start',
  CAST_LAND: 'cast:land',
  LURE_SETTLED: 'lure:settled',
  REEL_IN: 'reel:in',
  LINE_SNAP: 'line:snap',
  ROD_OVERLOAD: 'rod:overload',
  SNAGGED: 'line:snag',

  // bite FSM
  INTEREST: 'bite:interest',
  NIBBLE: 'bite:nibble',
  BITE_ON: 'bite:on',
  BITE_MISSED: 'bite:missed',
  SPOOKED: 'bite:spooked',
  HOOKED: 'bite:hooked',
  HOOKSET_EARLY: 'bite:early',

  // fight
  FIGHT_START: 'fight:start',
  FIGHT_STATE: 'fight:state',
  FISH_JUMP: 'fight:jump',
  HOOK_LOST: 'fight:hooklost',
  LANDED: 'fight:landed',
  FIGHT_END: 'fight:end',

  // progression
  XP_GAIN: 'prog:xp',
  LEVEL_UP: 'prog:levelup',
  MONEY: 'prog:money',
  RECORD: 'prog:record',
  UNLOCK: 'prog:unlock',
  QUEST_DONE: 'prog:quest',
  GEAR_EQUIP: 'prog:equip',
  GEAR_BUY: 'prog:buy',
  LURE_OUT: 'prog:lureout',

  // world / ui
  SPOT_CHANGE: 'world:spot',
  WEATHER_CHANGE: 'world:weather',
  TIME_PHASE: 'world:phase',
  RIPPLE: 'fx:ripple',
  SPLASH: 'fx:splash',
  TOAST: 'ui:toast',
  SAVE: 'ui:save',
};

export const bus = new EventBus();

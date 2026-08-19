/**
 * Entry point. Builds the simulation, builds the renderer, and runs the loop.
 *
 * The split matters: `step()` is the authoritative fixed-rate simulation and is
 * the exact code path the headless harness in tools/simulate.mjs drives, while
 * `render()` only reads telemetry. Nothing in the render half can affect the
 * outcome of a fight.
 */

import { RNG } from './core/rng.js';
import { bus, EV } from './core/events.js';
import { GameLoop, FIXED_DT } from './core/loop.js';
import { Input } from './core/input.js';
import { PlayerState } from './game/state.js';
import { World } from './game/world.js';
import { FishingGame, GAME_STATE } from './game/fishing.js';
import { SceneRenderer } from './render/scene.js';
import { HUD } from './ui/hud.js';
import { Panels } from './ui/panels.js';

const boot = document.getElementById('boot');
const bootMsg = document.getElementById('boot-msg');
const setBoot = (msg) => { if (bootMsg) bootMsg.textContent = msg; };

async function start() {
  const canvas = document.getElementById('view');
  const ui = document.getElementById('ui');

  setBoot('Menyediakan simulasi…');

  /* --- simulation --------------------------------------------------------- */
  const rng = new RNG(Date.now());
  const state = new PlayerState(bus);
  state.load();                       // resumes a save if there is one
  state.checkSpotUnlocks();

  const world = new World(rng.fork('world'), bus, state.spot);
  const game = new FishingGame({ rng, bus, state, world });

  /* --- rendering ---------------------------------------------------------- */
  setBoot('Menjana tekstur & aset…');
  // Yield a frame so the boot text paints before the synchronous texture
  // synthesis blocks the main thread.
  await new Promise((r) => requestAnimationFrame(() => r()));

  const quality = state.data.settings.quality ?? 'high';
  const renderer = new SceneRenderer({
    canvas, bus, rng: rng.fork('render'), spot: state.spot, quality,
  });
  renderer.setRod(state.gear().rod);
  renderer.lineVisual.setLine(state.gear().line);

  /* --- input & UI --------------------------------------------------------- */
  setBoot('Menyiapkan antara muka…');
  const input = new Input(window);

  const panels = new Panels(ui, {
    bus, state,
    getContext: () => ({ world, gear: game.gear, lureDepthNorm: game.lureDepthNorm }),
  });

  const hud = new HUD(ui, {
    bus, state,
    onAction: (kind, arg) => { if (kind === 'panel') panels.toggle(arg); },
  });

  // Panel hotkeys, and Escape to close.
  window.addEventListener('keydown', (e) => {
    const k = e.key.toLowerCase();
    if (k === 'escape') { panels.close(); return; }
    if (e.repeat) return;
    const map = { b: 'shop', i: 'bag', r: 'book', q: 'quests', t: 'travel' };
    if (map[k]) { e.preventDefault(); panels.toggle(map[k]); }
  });

  // While a panel is open the game should not be casting behind it.
  const gameInput = { reelAxis: 0, dragAxis: 0 };

  /* --- aiming ------------------------------------------------------------- */
  // Horizontal mouse position aims the cast; vertical sets the launch angle.
  window.addEventListener('pointermove', (e) => {
    if (panels.isOpen) return;
    const nx = (e.clientX / window.innerWidth) * 2 - 1;
    const ny = (e.clientY / window.innerHeight) * 2 - 1;
    game.aim(nx * 0.55, 0.75 - ny * 0.35);
  });

  /* --- the loop ----------------------------------------------------------- */
  let autosave = 0;

  const step = (dt) => {
    input.update(dt);

    if (panels.isOpen) {
      gameInput.reelAxis = 0;
      gameInput.dragAxis = 0;
    } else {
      gameInput.reelAxis = input.reelAxis;
      gameInput.dragAxis = input.dragAxis;

      // Hold to charge, release to cast.
      if (input.pressed.cast && game.phase === GAME_STATE.READY) game.beginCast();
      if (input.pressed.release && game.phase === GAME_STATE.CHARGING) game.releaseCast();
      if (input.pressed.strike) game.strike();
    }

    world.update(dt);
    game.update(dt, gameInput);
    input.endFrame();

    autosave += dt;
    if (autosave > 20) { autosave = 0; state.save(); }
  };

  const render = (alpha, frameDt) => {
    const dt = Math.min(frameDt, 0.1);
    renderer.update(dt, game, world, input);
    hud.update(dt, game.telemetry(), world, state);
    renderer.render();
  };

  const loop = new GameLoop(step, render);

  // First frame before revealing, so the player never sees an empty canvas.
  renderer.update(FIXED_DT, game, world, input);
  renderer.render();

  boot?.classList.add('gone');
  setTimeout(() => boot?.remove(), 700);

  loop.start();

  document.addEventListener('visibilitychange', () => {
    if (document.hidden) { state.save(); loop.stop(); }
    else loop.start();
  });
  window.addEventListener('beforeunload', () => state.save());

  // Handy for tuning from the console; not used by the game itself.
  Object.assign(window, { PANCING: { game, world, state, renderer, hud, panels, loop, bus, EV } });
}

start().catch((err) => {
  console.error(err);
  setBoot(`Gagal dimuatkan: ${err.message}`);
  if (boot) boot.classList.add('error');
});

/**
 * Scene orchestration.
 *
 * Owns the renderer, camera and every visual object, and subscribes to the
 * simulation's event bus. It is a pure consumer: nothing in here can change the
 * game state, which is why the whole simulation runs headless in the test
 * harness with this file absent.
 */

import * as THREE from 'three';
import { generateAll } from '../assets/textures.js';
import { Water } from './water.js';
import {
  buildTerrain, buildSky, buildFoliage, buildRain, buildLights, skyPalette, terrainHeight,
} from './environment.js';
import { RodVisual, LineVisual, LureVisual, FishVisual, SplashPool } from './tackle.js';
import { EV } from '../core/events.js';
import { GAME_STATE } from '../game/fishing.js';
import { clamp, clamp01, damp, lerp, smoothstep } from '../core/loop.js';

/** Where the angler stands. Everything is measured from here. */
export const ANGLER = new THREE.Vector3(0, 0.9, -2.2);

export class SceneRenderer {
  constructor({ canvas, bus, rng, spot, quality = 'high' }) {
    this.bus = bus;
    this.rng = rng;
    this.spot = spot;
    this.quality = quality;
    this.time = 0;

    /* --- renderer --------------------------------------------------------- */
    this.renderer = new THREE.WebGLRenderer({
      canvas, antialias: quality !== 'low', powerPreference: 'high-performance',
      stencil: false,
    });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, quality === 'high' ? 2 : 1.5));
    this.renderer.setSize(window.innerWidth, window.innerHeight);
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;
    this.renderer.toneMapping = THREE.ACESFilmicToneMapping;
    this.renderer.toneMappingExposure = 1.05;
    this.renderer.shadowMap.enabled = quality === 'high';
    this.renderer.shadowMap.type = THREE.PCFSoftShadowMap;

    this.scene = new THREE.Scene();
    this.camera = new THREE.PerspectiveCamera(58, window.innerWidth / window.innerHeight, 0.05, 900);
    this.camera.position.set(0, 2.35, -3.4);
    this.camera.lookAt(0, 0.6, 12);

    /* --- procedural assets ------------------------------------------------ */
    const built = generateAll(spot, { renderer: this.renderer, quality });
    this.textures = built.textures;
    this.canvases = built.canvases;

    /* --- world ------------------------------------------------------------ */
    const lights = buildLights();
    this.sun = lights.sun;
    this.hemi = lights.hemi;
    this.ambient = lights.ambient;
    this.scene.add(this.sun, this.sun.target, this.hemi, this.ambient);

    this.sky = buildSky(this.textures);
    this.scene.add(this.sky);

    this.terrain = buildTerrain(spot, this.textures, { quality });
    this.scene.add(this.terrain);

    this.foliage = buildFoliage(spot, this.textures, rng.fork('foliage'), { quality });
    this.scene.add(this.foliage);

    this.water = new Water({
      spot, renderer: this.renderer, normalMap: this.textures.waterNormals, quality,
    });
    this.scene.add(this.water.mesh);

    this.rain = buildRain(quality === 'low' ? 900 : 2600);
    this.scene.add(this.rain);

    this.scene.fog = new THREE.FogExp2(0xbcd0dc, 0.0055);

    /* --- tackle ----------------------------------------------------------- */
    this.rodVisual = null;      // built on first gear sync
    this.lineVisual = new LineVisual();
    this.lureVisual = new LureVisual();
    this.fishVisual = new FishVisual();
    this.splash = new SplashPool(quality === 'low' ? 200 : 420);

    this.rodPivot = new THREE.Group();
    this.rodPivot.position.copy(ANGLER);
    this.scene.add(this.rodPivot);
    this.scene.add(this.lineVisual.line, this.lureVisual.group, this.fishVisual.group, this.splash.points);

    /* --- camera state ----------------------------------------------------- */
    this.camYaw = 0;
    this.camPitch = 0.06;
    this.camDist = 4.1;
    this.camTarget = new THREE.Vector3(0, 0.8, 10);
    this.shake = 0;
    this.fovTarget = 58;

    this._tmpA = new THREE.Vector3();
    this._tmpB = new THREE.Vector3();

    this._wireEvents();
    window.addEventListener('resize', () => this.resize());
  }

  /* --- event wiring -------------------------------------------------------- */

  _wireEvents() {
    const b = this.bus;

    b.on(EV.RIPPLE, ({ pos, strength }) => {
      this.water.addRipple(pos.x, pos.z, strength);
    });

    b.on(EV.SPLASH, ({ pos, strength }) => {
      const y = this.water.heightAt(pos.x, pos.z);
      this.splash.burst(pos.x, y, pos.z, clamp(strength * 1.4, 0.2, 2.0));
      this.water.addRipple(pos.x, pos.z, strength);
      this.shake = Math.max(this.shake, strength * 0.35);
    });

    b.on(EV.NIBBLE, () => this.lureVisual.twitch(1));
    b.on(EV.BITE_ON, ({ phase }) => this.lureVisual.twitch(phase === 'window' ? 2.4 : 1.4));

    b.on(EV.HOOKED, ({ fish }) => {
      this.fishVisual.show(fish.species, fish.lengthCm);
      this.shake = 0.55;
      this.fovTarget = 63;
    });

    b.on(EV.FISH_JUMP, () => { this.shake = Math.max(this.shake, 0.5); });
    b.on(EV.LINE_SNAP, () => { this.shake = 0.8; this.fovTarget = 58; });

    b.on(EV.FIGHT_END, () => {
      this.fishVisual.hide();
      this.fovTarget = 58;
    });

    b.on(EV.REEL_IN, () => {
      this.lureVisual.setVisible(false);
      this.lineVisual.setVisible(false);
      this.fishVisual.hide();
    });

    b.on(EV.CAST_START, () => {
      this.lureVisual.setVisible(true);
      this.lineVisual.setVisible(true);
    });

    b.on(EV.GEAR_EQUIP, ({ slot, item }) => {
      if (slot === 'rod') this.setRod(item);
      if (slot === 'line') this.lineVisual.setLine(item);
    });

    b.on(EV.SPOT_CHANGE, ({ spot }) => this.setSpot(spot));
  }

  setRod(record) {
    if (this.rodVisual) {
      this.rodPivot.remove(this.rodVisual.group);
    }
    this.rodVisual = new RodVisual(record);
    this.rodPivot.add(this.rodVisual.group);
  }

  setSpot(spot) {
    this.spot = spot;

    // Regenerate the whole texture set for the new palette, then rebuild the
    // geometry that depends on the bathymetry.
    const built = generateAll(spot, { renderer: this.renderer, quality: this.quality });
    this.textures = built.textures;
    this.canvases = built.canvases;

    this.scene.remove(this.terrain);
    this.terrain.geometry.dispose();
    this.terrain.material.dispose();
    this.terrain = buildTerrain(spot, this.textures, { quality: this.quality });
    this.scene.add(this.terrain);

    this.scene.remove(this.foliage);
    this.foliage = buildFoliage(spot, this.textures, this.rng.fork(`foliage:${spot.id}`), { quality: this.quality });
    this.scene.add(this.foliage);

    this.water.setSpot(spot);
    this.water.uniforms.uNormalMap.value = this.textures.waterNormals;
    this.sky.userData.uniforms.uStars.value = this.textures.stars;
  }

  /* --- per-frame ----------------------------------------------------------- */

  /**
   * @param {number} dt      real frame delta
   * @param {object} game    FishingGame instance
   * @param {object} world   World instance
   * @param {object} input   Input instance
   */
  update(dt, game, world, input) {
    this.time += dt;
    const t = game.telemetry();

    this._updateSky(dt, world);
    this._updateTackle(dt, game, t, input);
    this._updateCamera(dt, game, t, input);

    this.water.update(dt);
    this.water.setEnvironment({
      wind: world.wind, chop: world.chop, light: world.light, rain: world.rain,
      sunDir: this.sun.position.clone().normalize(),
      sunColor: this.sun.color,
    });
    this.splash.update(dt);
    this.rain.userData.update(dt, world.rain, this.camera.position);

    const causticU = this.terrain.userData.causticUniforms;
    if (causticU) {
      causticU.uTime.value = this.time;
      causticU.uCausticStrength.value = world.light * 1.1;
    }

  }

  _updateSky(dt, world) {
    const p = skyPalette(world.hour, world.weather, this.spot.palette);
    const u = this.sky.userData.uniforms;

    u.uHorizon.value.lerp(p.horizon, 0.06);
    u.uZenith.value.lerp(p.zenith, 0.06);
    u.uSunColor.value.lerp(p.sunColor, 0.06);
    u.uSunDir.value.lerp(p.sunDir, 0.06).normalize();
    u.uLight.value = damp(u.uLight.value, p.light, 2, dt);
    u.uNight.value = damp(u.uNight.value, p.night, 2, dt);
    u.uCloud.value = damp(u.uCloud.value, 0.18 + (1 - world.weather.light) * 1.5, 1.2, dt);
    u.uTime.value = this.time;

    // Scene lighting follows the same palette, so nothing can disagree.
    this.sun.position.copy(p.sunDir).multiplyScalar(60);
    this.sun.color.lerp(p.sunColor, 0.06);
    this.sun.intensity = damp(this.sun.intensity, 0.25 + p.light * 2.3, 2, dt);
    this.hemi.intensity = damp(this.hemi.intensity, 0.25 + p.light * 0.7, 2, dt);
    this.hemi.color.lerp(p.zenith, 0.04);
    this.hemi.groundColor.lerp(new THREE.Color(this.spot.palette.sand), 0.04);
    this.ambient.intensity = damp(this.ambient.intensity, 0.08 + p.light * 0.18, 2, dt);

    // Fog picks up the horizon colour so the far shore dissolves correctly.
    this.scene.fog.color.lerp(p.horizon, 0.05);
    this.scene.fog.density = damp(this.scene.fog.density,
      0.0035 + world.rain * 0.011 + (1 - p.light) * 0.004, 1.5, dt);

    // Foliage billboards are unlit by construction, so daylight has to be
    // applied to their tint by hand or the trees glow at midnight.
    const foliageTint = 0.14 + p.light * 0.86;
    for (const m of this.foliage.userData.litMaterials ?? []) {
      m.color.setScalar(foliageTint);
    }
  }

  _updateTackle(dt, game, t, input) {
    if (!this.rodVisual) this.setRod(t.gear.rod);

    // The rod aims where the cast aims, and lifts as the cast charges.
    const yaw = game.cast.aimYaw;
    const charging = t.phase === GAME_STATE.CHARGING;
    const chargePitch = charging ? -t.cast.value * 0.55 - t.cast.overload * 0.3 : 0;
    // While fighting, the rod is held up; the bend does the rest.
    const fightPitch = t.phase === GAME_STATE.FIGHT ? -0.62 : -0.18;
    const shake = t.fish?.state === 'thrash' ? clamp01(t.rod.loadFrac * 1.4) : 0;

    const tip = this.rodVisual.update(dt, {
      bend: t.rod.bend,
      yaw,
      pitch: damp(this.rodVisual.group.rotation.z, chargePitch + fightPitch, 8, dt),
      shake,
      time: this.time,
    });

    const showLine = t.phase !== GAME_STATE.READY && t.phase !== GAME_STATE.CHARGING;
    this.lineVisual.setVisible(showLine);
    this.lureVisual.setVisible(showLine && t.phase !== GAME_STATE.FIGHT);

    if (!showLine) return;

    // Where the business end of the line actually is.
    let endPoint;
    if (t.phase === GAME_STATE.FIGHT && t.fish) {
      const f = t.fish;
      const surfaceY = this.water.heightAt(f.lateral, f.dist);
      this.fishVisual.update(dt, { ...f, maxForce: f.maxForce }, surfaceY);
      endPoint = this.fishVisual.group.position.clone();
      endPoint.y += 0.05;
    } else {
      const pos = game.cast.pos;
      const surfaceY = this.water.heightAt(pos.x, pos.z);
      const flying = t.phase === GAME_STATE.FLYING;
      this.lureVisual.update(dt, {
        pos,
        surfaceY: flying ? pos.y : surfaceY,
        submerged: !flying && t.lureDepthNorm > 0.02 && t.gear.lure.sink > 0.2,
        wobble: clamp01(game.retrieveRate * 1.2),
        time: this.time,
      });
      endPoint = this.lureVisual.group.position.clone();
    }

    this.lineVisual.update(tip, endPoint, t.rod.tension);
  }

  _updateCamera(dt, game, t, input) {
    // Look where the action is: at the lure while fishing, at the fish while
    // fighting, and pulled back a little while a big fish is running.
    const focus = this._tmpA;
    if (t.phase === GAME_STATE.FIGHT && t.fish) {
      focus.set(t.fish.lateral * 0.6, 0.35, Math.min(t.fish.dist, 26) * 0.8);
    } else if (t.phase === GAME_STATE.READY || t.phase === GAME_STATE.CHARGING) {
      focus.set(Math.sin(game.cast.aimYaw) * 12, 0.6, Math.cos(game.cast.aimYaw) * 12);
    } else {
      focus.set(game.cast.pos.x * 0.75, 0.35, game.cast.distance * 0.75);
    }
    this.camTarget.lerp(focus, 1 - Math.exp(-4 * dt));

    // Pull the camera back when there is a lot of line out, so the fish stays
    // framed without the player having to do anything.
    const wantDist = t.phase === GAME_STATE.FIGHT
      ? lerp(3.6, 6.4, clamp01(t.rod.lineOut / 30))
      : 4.1;
    this.camDist = damp(this.camDist, wantDist, 2.2, dt);

    const yaw = game.cast.aimYaw * 0.55;
    // High enough to look down the water rather than along it. At eye level the
    // near surface fills the lower half of the frame with steep-angle water,
    // which is physically right and compositionally useless. Measured from the
    // ground the angler is standing on, so a raised bank does not sink the view.
    const height = this.groundAt(ANGLER.x, ANGLER.z) + 2.9 + clamp01(t.rod.loadFrac) * 0.4;

    const desired = this._tmpB.set(
      ANGLER.x - Math.sin(yaw) * this.camDist,
      height,
      ANGLER.z - Math.cos(yaw) * this.camDist,
    );
    this.camera.position.lerp(desired, 1 - Math.exp(-5 * dt));

    // Shake: fed by splashes, hooksets and snaps.
    this.shake = damp(this.shake, 0, 4.5, dt);
    if (this.shake > 0.002) {
      this.camera.position.x += Math.sin(this.time * 47) * this.shake * 0.045;
      this.camera.position.y += Math.sin(this.time * 39 + 1.7) * this.shake * 0.035;
    }

    this.camera.lookAt(this.camTarget);

    // A tight line narrows the field of view slightly — a cheap, legible way to
    // make heavy tension feel heavy.
    const fov = this.fovTarget - clamp01(t.rod.loadFrac) * 4.5;
    if (Math.abs(this.camera.fov - fov) > 0.05) {
      this.camera.fov = damp(this.camera.fov, fov, 3, dt);
      this.camera.updateProjectionMatrix();
    }
  }

  render() {
    // Reflection and refraction first, with the water hidden from both.
    this.water.updateBuffers(this.renderer, this.scene, this.camera);
    this.renderer.render(this.scene, this.camera);
  }

  resize() {
    const w = window.innerWidth, h = window.innerHeight;
    this.camera.aspect = w / h;
    this.camera.updateProjectionMatrix();
    this.renderer.setSize(w, h);
  }

  /** Ground height under a world point, for placing things on the bank. */
  groundAt(x, z) {
    return terrainHeight(x, z, this.spot, this.spot.id.length * 37);
  }

  dispose() {
    this.water.dispose();
    this.renderer.dispose();
  }
}

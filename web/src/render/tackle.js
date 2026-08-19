/**
 * Everything attached to the angler: the rod, the line, the lure and the fish
 * on the end of it, plus the splashes they make.
 *
 * The important idea here is that none of this is decorative. The rod's bend is
 * the solver's `bend` value; the line's sag is computed from the actual tension
 * in newtons; the fish's position in the water is its real simulated distance,
 * depth and lateral offset. A player who never looks at the HUD can still read
 * the entire physical state off the scene, which is the whole point of putting
 * a tension model behind a fishing game.
 */

import * as THREE from 'three';
import { catenaryPoints, rodSpine } from '../physics/rod.js';
import { buildFishMesh } from '../assets/fishgen.js';
import { clamp, clamp01, damp, lerp } from '../core/loop.js';

/* --- rod ------------------------------------------------------------------- */

export class RodVisual {
  constructor(rodRecord) {
    this.group = new THREE.Group();
    this.group.name = 'rod';
    this.record = rodRecord;

    this.material = new THREE.MeshStandardMaterial({
      color: new THREE.Color(rodRecord.tipColor),
      roughness: 0.42, metalness: 0.25,
    });
    this.gripMaterial = new THREE.MeshStandardMaterial({ color: 0x2a2622, roughness: 0.9 });

    this.blank = new THREE.Mesh(new THREE.BufferGeometry(), this.material);
    this.group.add(this.blank);

    // Grip and reel seat sit at the butt and do not flex.
    const grip = new THREE.Mesh(new THREE.CylinderGeometry(0.024, 0.030, 0.34, 10), this.gripMaterial);
    grip.rotation.z = Math.PI / 2;
    grip.position.set(0.16, 0, 0);
    this.group.add(grip);

    const reel = new THREE.Mesh(new THREE.CylinderGeometry(0.055, 0.055, 0.05, 14),
      new THREE.MeshStandardMaterial({ color: 0x8c9299, roughness: 0.35, metalness: 0.65 }));
    reel.position.set(0.33, -0.075, 0);
    this.group.add(reel);
    this.reel = reel;

    this.tipWorld = new THREE.Vector3();
    this.bend = 0;
    this._spine = [];
    this.rebuild(0);
  }

  setRod(record) {
    this.record = record;
    this.material.color.set(record.tipColor);
    this.rebuild(this.bend);
  }

  /**
   * Rebuild the blank for the current bend. The spine comes straight from the
   * physics module so the curve on screen is the curve the solver used.
   */
  rebuild(bend) {
    const pts = rodSpine(this.record.length, bend, 12, this._spine);
    const curve = new THREE.CatmullRomCurve3(
      pts.map((p) => new THREE.Vector3(p.along, -p.drop, 0)),
    );
    const geo = new THREE.TubeGeometry(curve, 14, 0.016, 6, false);
    // Taper: shrink the radius toward the tip by scaling the ring vertices.
    const pos = geo.attributes.position;
    const tmp = new THREE.Vector3();
    for (let i = 0; i < pos.count; i++) {
      const t = Math.floor(i / 7) / 14;
      const scale = lerp(1.5, 0.34, t);
      tmp.fromBufferAttribute(pos, i);
      const onCurve = curve.getPoint(Math.min(t, 1));
      tmp.sub(onCurve).multiplyScalar(scale).add(onCurve);
      pos.setXYZ(i, tmp.x, tmp.y, tmp.z);
    }
    geo.computeVertexNormals();
    this.blank.geometry.dispose();
    this.blank.geometry = geo;
    this.bend = bend;
  }

  /**
   * @param {number} bend   0..1 from the tension solver
   * @param {number} yaw    aim direction
   * @param {number} shake  high-frequency wobble from a thrashing fish
   */
  update(dt, { bend, yaw, pitch = 0, shake = 0, time = 0 }) {
    // Only rebuild when the bend has actually changed enough to see.
    if (Math.abs(bend - this.bend) > 0.012) this.rebuild(bend);

    this.group.rotation.y = yaw;
    this.group.rotation.z = pitch + (shake > 0 ? Math.sin(time * 34) * shake * 0.045 : 0);
    this.group.rotation.x = shake > 0 ? Math.sin(time * 27 + 1.1) * shake * 0.03 : 0;

    // Spin the reel handle while line is moving.
    this.reel.rotation.y += dt * 6;

    // Cache the tip in world space; the line hangs from it.
    const pts = this._spine;
    const tip = pts[pts.length - 1];
    this.tipWorld.set(tip.along, -tip.drop, 0);
    this.group.localToWorld(this.tipWorld);
    return this.tipWorld;
  }
}

/* --- line ------------------------------------------------------------------ */

export class LineVisual {
  constructor(segments = 24) {
    this.segments = segments;
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array((segments + 1) * 3), 3));
    this.material = new THREE.LineBasicMaterial({
      color: 0xe8f0f4, transparent: true, opacity: 0.55, depthWrite: false,
    });
    this.line = new THREE.Line(geo, this.material);
    this.line.frustumCulled = false;
    this.line.name = 'fishing-line';
    this._pts = [];
  }

  setLine(lineRecord) {
    // A visible line is genuinely visible: braid shows, fluorocarbon does not.
    this.material.opacity = 0.22 + lineRecord.visibility * 0.55;
    this.material.color.set(lineRecord.visibility > 0.7 ? 0xdfe7ea : 0xeef4f7);
  }

  /** @param {number} tension newtons — drives the sag directly */
  update(from, to, tension) {
    const pts = catenaryPoints(from, to, tension, 1.0, this.segments, this._pts);
    const arr = this.line.geometry.attributes.position.array;
    for (let i = 0; i < pts.length; i++) {
      arr[i * 3] = pts[i].x; arr[i * 3 + 1] = pts[i].y; arr[i * 3 + 2] = pts[i].z;
    }
    this.line.geometry.attributes.position.needsUpdate = true;
    this.line.geometry.computeBoundingSphere();
  }

  setVisible(v) { this.line.visible = v; }
}

/* --- lure / bobber --------------------------------------------------------- */

export class LureVisual {
  constructor() {
    this.group = new THREE.Group();
    this.group.name = 'lure';

    const top = new THREE.Mesh(
      new THREE.SphereGeometry(0.055, 12, 10, 0, Math.PI * 2, 0, Math.PI / 2),
      new THREE.MeshStandardMaterial({ color: 0xd8422f, roughness: 0.4 }),
    );
    const bottom = new THREE.Mesh(
      new THREE.SphereGeometry(0.055, 12, 10, 0, Math.PI * 2, Math.PI / 2, Math.PI / 2),
      new THREE.MeshStandardMaterial({ color: 0xf2f0e8, roughness: 0.4 }),
    );
    const stem = new THREE.Mesh(
      new THREE.CylinderGeometry(0.007, 0.007, 0.11, 6),
      new THREE.MeshStandardMaterial({ color: 0x2b2b2b, roughness: 0.6 }),
    );
    stem.position.y = 0.08;
    this.group.add(top, bottom, stem);

    this.bobPhase = Math.random() * 6.28;
    this.dip = 0;
    this.targetDip = 0;
  }

  /** A nibble yanks the float under; this is the player's earliest signal. */
  twitch(strength = 1) { this.targetDip = Math.max(this.targetDip, 0.11 * strength); }

  update(dt, { pos, surfaceY, submerged, wobble = 0, time = 0 }) {
    this.dip = damp(this.dip, this.targetDip, 9, dt);
    this.targetDip = damp(this.targetDip, 0, 5, dt);

    const bob = Math.sin(time * 2.1 + this.bobPhase) * 0.018 * (1 + wobble * 2);
    this.group.position.set(pos.x, surfaceY + bob - this.dip - (submerged ? 0.12 : 0), pos.z);
    // Tilt with the surface slope so it never looks pasted on.
    this.group.rotation.z = Math.sin(time * 1.7 + this.bobPhase) * 0.16 * (1 + wobble);
    this.group.rotation.x = Math.cos(time * 1.35 + this.bobPhase) * 0.13 * (1 + wobble);
  }

  setVisible(v) { this.group.visible = v; }
}

/* --- the hooked fish ------------------------------------------------------- */

export class FishVisual {
  constructor() {
    this.group = new THREE.Group();
    this.group.name = 'hooked-fish';
    this.mesh = null;
    this.species = null;
    this.time = 0;
    this._cache = new Map();
  }

  /** Swap in the mesh for a species, building it the first time it is seen. */
  show(species, lengthCm) {
    const key = species.id;
    if (!this._cache.has(key)) this._cache.set(key, buildFishMesh(species.art, 1));
    const mesh = this._cache.get(key);
    if (this.mesh && this.mesh !== mesh) this.group.remove(this.mesh);
    this.mesh = mesh;
    if (!this.group.children.includes(mesh)) this.group.add(mesh);
    mesh.scale.setScalar(lengthCm / 100);
    this.species = species;
    this.group.visible = true;
  }

  hide() { this.group.visible = false; }

  /**
   * @param {object} s  fish telemetry: dist, depth, lateral, stamina, airborne,
   *                    state, plus the current surface height at its position
   */
  update(dt, s, surfaceY) {
    if (!this.mesh || !this.group.visible) return;
    this.time += dt;

    const y = s.airborne > 0.02
      ? surfaceY + s.airborne * 1.4
      : Math.min(surfaceY - 0.06, -s.depth);

    this.group.position.set(s.lateral, y, s.dist);

    // Face back toward the angler, plus a lateral lean while running.
    const heading = Math.atan2(-s.lateral, -s.dist);
    this.group.rotation.y = damp(this.group.rotation.y, heading + Math.PI, 4, dt);

    // Exhausted fish roll onto their side — the classic "it's beaten" tell.
    const roll = (1 - clamp01(s.stamina / 0.35)) * 1.15;
    this.group.rotation.z = damp(this.group.rotation.z, roll, 3, dt);

    // A jumping fish pitches nose-up on the way out and nose-down coming back.
    this.group.rotation.x = damp(this.group.rotation.x, s.airborne * -0.7, 6, dt);

    // Tail beat scales with how hard it is working; a thrashing fish shakes.
    const effort = clamp01(s.pull / Math.max(s.maxForce, 1));
    const swimAmount = 0.35 + effort * 1.3;
    const bend = s.state === 'thrash' ? Math.sin(this.time * 21) * 0.9 : 0;
    this.mesh.userData.swim?.(this.time * (0.6 + effort), swimAmount, bend);
  }
}

/* --- splashes -------------------------------------------------------------- */

/**
 * A pooled particle burst. One geometry, one draw call, no allocation after
 * construction — splashes happen often enough that garbage matters.
 */
export class SplashPool {
  constructor(capacity = 420) {
    this.capacity = capacity;
    this.positions = new Float32Array(capacity * 3);
    this.velocities = new Float32Array(capacity * 3);
    this.life = new Float32Array(capacity);
    this.maxLife = new Float32Array(capacity);
    this.sizes = new Float32Array(capacity);
    this.cursor = 0;

    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(this.positions, 3));
    geo.setAttribute('size', new THREE.BufferAttribute(this.sizes, 1));

    this.material = new THREE.PointsMaterial({
      color: 0xf2f8fa, size: 0.09, transparent: true, opacity: 0.9,
      depthWrite: false, sizeAttenuation: true,
      blending: THREE.NormalBlending,
    });
    this.points = new THREE.Points(geo, this.material);
    this.points.frustumCulled = false;
    this.points.name = 'splash';

    // Park unused particles far below the world rather than toggling visibility.
    for (let i = 0; i < capacity; i++) this.positions[i * 3 + 1] = -999;
  }

  burst(x, y, z, strength = 1, count = null) {
    const n = count ?? Math.round(14 + strength * 46);
    for (let i = 0; i < n; i++) {
      const idx = this.cursor;
      this.cursor = (this.cursor + 1) % this.capacity;
      const a = Math.random() * Math.PI * 2;
      const r = Math.random() * 0.35 * strength;
      const up = (1.6 + Math.random() * 3.4) * (0.4 + strength);
      this.positions[idx * 3] = x + Math.cos(a) * r;
      this.positions[idx * 3 + 1] = y + 0.03;
      this.positions[idx * 3 + 2] = z + Math.sin(a) * r;
      this.velocities[idx * 3] = Math.cos(a) * (0.7 + Math.random() * 2.1) * strength;
      this.velocities[idx * 3 + 1] = up;
      this.velocities[idx * 3 + 2] = Math.sin(a) * (0.7 + Math.random() * 2.1) * strength;
      this.maxLife[idx] = 0.45 + Math.random() * 0.75;
      this.life[idx] = this.maxLife[idx];
      this.sizes[idx] = 0.5 + Math.random() * 0.9;
    }
  }

  update(dt) {
    let any = false;
    for (let i = 0; i < this.capacity; i++) {
      if (this.life[i] <= 0) continue;
      any = true;
      this.life[i] -= dt;
      if (this.life[i] <= 0) { this.positions[i * 3 + 1] = -999; continue; }
      this.velocities[i * 3 + 1] -= 9.81 * dt;
      this.positions[i * 3] += this.velocities[i * 3] * dt;
      this.positions[i * 3 + 1] += this.velocities[i * 3 + 1] * dt;
      this.positions[i * 3 + 2] += this.velocities[i * 3 + 2] * dt;
      // Drop out on contact with the surface.
      if (this.positions[i * 3 + 1] < 0) { this.life[i] = 0; this.positions[i * 3 + 1] = -999; }
    }
    if (any) this.points.geometry.attributes.position.needsUpdate = true;
  }
}

/**
 * Fish synthesis — one genome, two outputs.
 *
 * Every species in data/species.js carries an `art` genome: a body profile
 * spline, a palette, a pattern type, fin styles and a seed. This module turns
 * that genome into BOTH the 3D mesh that swims in the lake AND the 2D card
 * artwork on the catch screen, from the same numbers.
 *
 * That shared derivation is the point. A hand-drawn sprite and a hand-modelled
 * mesh drift apart the moment either is edited; here the card art is guaranteed
 * to be a portrait of the thing the player actually fought, because both are
 * evaluated from the same body function.
 *
 *   bodyRadius(u)  →  half-height of the fish at position u along its length
 *   patternAt(u,v) →  0..1 accent mask, in body-surface coordinates
 *   colourAt(u,v)  →  countershaded base colour + pattern accent
 *
 * The mesh lofts cross-sections through bodyRadius and bakes colourAt into
 * vertex colours; the sprite rasterises the same two functions in 2D. No
 * textures, no UV unwrapping, no files.
 */

import * as THREE from 'three';
import {
  spline, fbm2, worley2, clamp01, smoothstep,
  hexToRgb, mixRgb, shade, mix,
} from './noise.js';
import { makeCanvas } from './textures.js';

/* --- the shared body functions --------------------------------------------- */

/**
 * Half-height of the body at u (0 = snout, 1 = tail root), normalised to length.
 * The genome's `profile` array is the control polygon.
 */
export function bodyRadius(art, u) {
  const t = clamp01(u);
  // Taper hard at both ends regardless of the profile, so no fish has a blunt
  // nose or a slab-ended tail root.
  const cap = Math.pow(Math.sin(Math.PI * Math.pow(t, 0.78)), 0.55);
  return Math.max(0.004, spline(art.profile, t) * cap);
}

/** Lateral half-width. Fish are compressed side to side; crustaceans are not. */
export function bodyWidth(art, u) {
  const compress = art.crustacean ? 0.85 : 0.34 + art.gloss * 0.10;
  return bodyRadius(art, u) * compress;
}

/**
 * Pattern mask in body coordinates. u runs nose→tail, v runs belly(0)→back(1).
 * Returns 0..1, where 1 is full accent colour.
 */
export function patternAt(art, u, v) {
  const s = art.seed;
  const amt = art.patternAmt ?? 0;
  if (amt <= 0) return 0;
  let m = 0;

  switch (art.pattern) {
    case 'bars':      // vertical banding, tilapia-style
      m = smoothstep(0.35, 0.65, 0.5 + 0.5 * Math.sin(u * Math.PI * 14 + s));
      m *= smoothstep(0.05, 0.45, v);
      break;
    case 'stripe':    // one lateral line down the flank
      m = 1 - smoothstep(0.0, 0.13, Math.abs(v - 0.52));
      break;
    case 'band':      // broad blotchy horizontal band, sebarau / toman
      m = (1 - smoothstep(0.0, 0.24, Math.abs(v - 0.5)))
        * smoothstep(0.25, 0.55, fbm2(u * 7, v * 3, { octaves: 3, seed: s }));
      break;
    case 'chevron':   // haruan's angled flank marks
      m = smoothstep(0.42, 0.62, 0.5 + 0.5 * Math.sin(u * Math.PI * 11 - v * 4.2 + s));
      m *= smoothstep(0.08, 0.5, v);
      break;
    case 'spots':
      m = Math.pow(clamp01(1 - worley2(u * 11, v * 5, { cells: 11, seed: s }).f1 * 2.6), 2.2);
      break;
    case 'mottle':
      m = smoothstep(0.48, 0.72, fbm2(u * 9, v * 6, { octaves: 4, seed: s }));
      break;
    case 'scales':    // kelah's big reflective plates
      m = Math.pow(clamp01(1 - worley2(u * 22, v * 9, { cells: 22, seed: s }).edge * 4.0), 2.0) * 0.8;
      break;
    case 'segments':  // crustacean plating
      m = smoothstep(0.30, 0.52, 0.5 + 0.5 * Math.sin(u * Math.PI * 9 + s)) * 0.9;
      break;
    default:
      m = 0;
  }
  return clamp01(m) * amt;
}

/**
 * Countershading: dark back, mid flank, pale belly — the near-universal
 * colouring of open-water fish, and the thing that makes a generated fish read
 * as a fish rather than as a coloured blob.
 */
export function colourAt(art, u, v) {
  const [backHex, flankHex, bellyHex, accentHex] = art.palette;
  const back = hexToRgb(backHex);
  const flank = hexToRgb(flankHex);
  const belly = hexToRgb(bellyHex);
  const accent = hexToRgb(accentHex);

  // v: 0 belly → 1 back. Two-stage ramp with the transition biased low, because
  // the pale belly is usually a narrow strip.
  let c = v < 0.45
    ? mixRgb(belly, flank, smoothstep(0.10, 0.45, v))
    : mixRgb(flank, back, smoothstep(0.45, 0.92, v));

  // Fine skin grain so large flat flanks are not dead.
  const grain = fbm2(u * 30, v * 14, { octaves: 3, seed: art.seed + 3 });
  c = shade(c, (grain - 0.5) * 0.14);

  // Pattern accent.
  c = mixRgb(c, accent, patternAt(art, u, v));

  // Gill plate and head shading.
  if (u < 0.22) c = shade(c, 0.06 * (1 - u / 0.22));
  return c;
}

/* --- 3D mesh --------------------------------------------------------------- */

/**
 * Build a fish mesh from the genome.
 *
 * @param {object} art     species.art genome
 * @param {number} lengthM real length in metres (drives absolute scale)
 * @param {object} opts    { segments, radial }
 * @returns {THREE.Group}  body + fins, with a `swim(t, amount)` animator
 */
export function buildFishMesh(art, lengthM = 0.3, opts = {}) {
  const segments = opts.segments ?? 34;
  const radial = opts.radial ?? 14;

  const positions = [];
  const normals = [];
  const colors = [];
  const uvs = [];
  const indices = [];

  // --- body: loft an ellipse along the spine -------------------------------
  for (let i = 0; i <= segments; i++) {
    const u = i / segments;
    const rh = bodyRadius(art, u);            // vertical half-height
    const rw = bodyWidth(art, u);             // lateral half-width
    const x = (u - 0.5);                      // spine runs -0.5 .. +0.5

    for (let j = 0; j <= radial; j++) {
      const a = (j / radial) * Math.PI * 2;
      const ca = Math.cos(a), sa = Math.sin(a);

      // Belly is fuller than the back: bias the vertical radius by angle.
      const bellyBias = ca < 0 ? 1.14 : 0.94;
      const y = ca * rh * bellyBias;
      const z = sa * rw;

      positions.push(x, y, z);

      // Analytic-ish normal: ellipse normal, then flattened by the taper.
      const nx = -(bodyRadius(art, Math.min(u + 0.02, 1)) - bodyRadius(art, Math.max(u - 0.02, 0))) / 0.04;
      const n = new THREE.Vector3(nx * 0.5, ca / Math.max(rh, 1e-3), sa / Math.max(rw, 1e-3)).normalize();
      normals.push(n.x, n.y, n.z);

      // v in body coordinates: 0 at the belly (angle PI), 1 at the back (0).
      const vBody = 1 - (a > Math.PI ? (Math.PI * 2 - a) : a) / Math.PI;
      const c = colourAt(art, u, vBody);
      colors.push(c.r / 255, c.g / 255, c.b / 255);
      uvs.push(u, vBody);
    }
  }

  const ring = radial + 1;
  for (let i = 0; i < segments; i++) {
    for (let j = 0; j < radial; j++) {
      const a = i * ring + j, b = a + ring;
      indices.push(a, b, a + 1, b, b + 1, a + 1);
    }
  }

  const bodyGeo = new THREE.BufferGeometry();
  bodyGeo.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3));
  bodyGeo.setAttribute('normal', new THREE.Float32BufferAttribute(normals, 3));
  bodyGeo.setAttribute('color', new THREE.Float32BufferAttribute(colors, 3));
  bodyGeo.setAttribute('uv', new THREE.Float32BufferAttribute(uvs, 2));
  bodyGeo.setIndex(indices);

  const material = new THREE.MeshStandardMaterial({
    vertexColors: true,
    roughness: clamp01(0.82 - art.gloss * 0.55),
    metalness: art.gloss * 0.22,
    side: THREE.DoubleSide,
  });

  const group = new THREE.Group();
  const body = new THREE.Mesh(bodyGeo, material);
  group.add(body);

  // --- fins ----------------------------------------------------------------
  const finMat = new THREE.MeshStandardMaterial({
    color: new THREE.Color(art.palette[1]),
    roughness: 0.75, metalness: 0.0,
    side: THREE.DoubleSide, transparent: true, opacity: 0.88,
  });

  group.add(makeTailFin(art, finMat));
  if (art.dorsal > 0.05) group.add(makeDorsalFin(art, finMat));
  if (!art.crustacean) {
    for (const side of [-1, 1]) group.add(makePectoralFin(art, finMat, side));
  }
  if (art.barbels) group.add(makeBarbels(art));

  // --- eyes ----------------------------------------------------------------
  if (art.eye > 0.01) {
    const eyeGeo = new THREE.SphereGeometry(art.eye, 10, 8);
    const eyeMat = new THREE.MeshStandardMaterial({ color: 0x0a0c0e, roughness: 0.15, metalness: 0.3 });
    const ex = -0.5 + 0.13;
    const ey = bodyRadius(art, 0.13) * 0.38;
    for (const side of [-1, 1]) {
      const e = new THREE.Mesh(eyeGeo, eyeMat);
      e.position.set(ex, ey, side * bodyWidth(art, 0.13) * 0.82);
      group.add(e);
    }
  }

  // Scale the unit-length fish to its real size.
  group.scale.setScalar(lengthM);

  /* --- swim animation ----------------------------------------------------
   * A travelling sine wave down the spine, amplitude increasing toward the
   * tail. Applied to the body vertices directly so the whole fish flexes
   * rather than pivoting like a plank.
   */
  const basePos = bodyGeo.attributes.position.array.slice();
  group.userData.swim = (t, amount = 1, bend = 0) => {
    const pos = bodyGeo.attributes.position.array;
    for (let i = 0; i < pos.length; i += 3) {
      const u = basePos[i] + 0.5;                     // 0..1 along the body
      const amp = Math.pow(u, 2.1) * 0.20 * amount;
      const wave = Math.sin(t * 9.5 - u * 5.6) * amp;
      pos[i + 2] = basePos[i + 2] + wave;
      // A steady lateral bend, used when a hooked fish turns against the line.
      pos[i + 2] += Math.pow(u, 1.6) * bend * 0.35;
    }
    bodyGeo.attributes.position.needsUpdate = true;
    bodyGeo.computeVertexNormals();

    // Tail fin follows the end of the wave.
    const tail = group.children[1];
    if (tail) tail.rotation.y = Math.sin(t * 9.5 - 5.6) * 0.55 * amount + bend * 0.5;
  };

  group.userData.art = art;
  return group;
}

function makeTailFin(art, mat) {
  const r = bodyRadius(art, 0.97);
  const shape = new THREE.Shape();
  const span = r * (art.tail === 'lunate' ? 4.2 : art.tail === 'forked' ? 3.4 : 2.6);
  const len = r * (art.tail === 'truncate' ? 1.6 : art.tail === 'round' ? 2.0 : 2.8);

  shape.moveTo(0, 0);
  switch (art.tail) {
    case 'forked':
      shape.lineTo(-len, span * 0.5);
      shape.quadraticCurveTo(-len * 0.45, 0, -len, -span * 0.5);
      break;
    case 'lunate':
      shape.quadraticCurveTo(-len * 0.7, span * 0.30, -len, span * 0.5);
      shape.quadraticCurveTo(-len * 0.30, 0, -len, -span * 0.5);
      shape.quadraticCurveTo(-len * 0.7, -span * 0.30, 0, 0);
      break;
    case 'round':
      shape.quadraticCurveTo(-len * 1.25, span * 0.55, -len * 0.9, 0);
      shape.quadraticCurveTo(-len * 1.25, -span * 0.55, 0, 0);
      break;
    case 'fan':
      shape.lineTo(-len * 0.9, span * 0.6);
      shape.lineTo(-len * 1.1, 0);
      shape.lineTo(-len * 0.9, -span * 0.6);
      break;
    default: // truncate
      shape.lineTo(-len, span * 0.45);
      shape.lineTo(-len, -span * 0.45);
  }
  shape.closePath();

  const geo = new THREE.ShapeGeometry(shape, 12);
  const mesh = new THREE.Mesh(geo, mat);
  mesh.rotation.y = Math.PI / 2;      // face the fin into the XY plane of the body
  mesh.position.x = 0.5;
  return mesh;
}

function makeDorsalFin(art, mat) {
  const shape = new THREE.Shape();
  const from = 0.30, to = 0.30 + art.dorsal * 0.55;
  const steps = 14;
  shape.moveTo(from - 0.5, bodyRadius(art, from) * 0.9);
  for (let i = 1; i <= steps; i++) {
    const u = mix(from, to, i / steps);
    const h = bodyRadius(art, u) * (0.9 + Math.sin((i / steps) * Math.PI) * art.dorsal * 2.4);
    shape.lineTo(u - 0.5, h);
  }
  for (let i = steps; i >= 0; i--) {
    const u = mix(from, to, i / steps);
    shape.lineTo(u - 0.5, bodyRadius(art, u) * 0.86);
  }
  shape.closePath();
  const mesh = new THREE.Mesh(new THREE.ShapeGeometry(shape, 8), mat);
  return mesh;   // already in the XY plane, which is the fish's vertical plane
}

function makePectoralFin(art, mat, side) {
  const u = 0.26;
  const r = bodyRadius(art, u);
  const shape = new THREE.Shape();
  shape.moveTo(0, 0);
  shape.quadraticCurveTo(-r * 1.5, -r * 0.5, -r * 2.1, -r * 1.5);
  shape.quadraticCurveTo(-r * 0.8, -r * 0.8, 0, 0);
  const mesh = new THREE.Mesh(new THREE.ShapeGeometry(shape, 8), mat);
  mesh.position.set(u - 0.5, -r * 0.15, side * bodyWidth(art, u) * 0.9);
  mesh.rotation.set(side * 0.5, 0, 0);
  return mesh;
}

function makeBarbels(art) {
  const group = new THREE.Group();
  const mat = new THREE.MeshStandardMaterial({ color: new THREE.Color(art.palette[1]), roughness: 0.9 });
  const n = art.barbels;
  const r = bodyRadius(art, 0.06);
  for (let i = 0; i < n; i++) {
    const side = i % 2 === 0 ? 1 : -1;
    const tier = Math.floor(i / 2);
    const len = 0.16 - tier * 0.05;
    const curve = new THREE.CatmullRomCurve3([
      new THREE.Vector3(-0.46, -r * 0.3 - tier * 0.02, side * r * 0.5),
      new THREE.Vector3(-0.46 - len * 0.5, -r * 0.8 - tier * 0.04, side * (r * 0.8 + len * 0.3)),
      new THREE.Vector3(-0.46 - len, -r * 1.4 - tier * 0.05, side * (r + len * 0.5)),
    ]);
    group.add(new THREE.Mesh(new THREE.TubeGeometry(curve, 8, 0.006, 5, false), mat));
  }
  return group;
}

/* --- 2D sprite ------------------------------------------------------------- */

/**
 * Render the species as a side-on card portrait, from the same body functions
 * the mesh uses. Returns a canvas.
 *
 * @param {object} opts { width, height, background, glow }
 */
export function buildFishSprite(art, opts = {}) {
  const W = opts.width ?? 512;
  const H = opts.height ?? 256;
  const canvas = makeCanvas(W, H);
  const ctx = canvas.getContext('2d', { willReadFrequently: true });
  ctx.clearRect(0, 0, W, H);

  const padX = W * 0.10;
  const bodyLen = W - padX * 2;
  const midY = H * 0.52;
  // Scale so the deepest point of the fish fits the card height.
  let maxR = 0;
  for (let i = 0; i <= 64; i++) maxR = Math.max(maxR, bodyRadius(art, i / 64));
  const scale = Math.min(bodyLen, (H * 0.40) / Math.max(maxR, 1e-3));

  const xOf = (u) => padX + u * bodyLen;
  const yOf = (u, sign) => midY - sign * bodyRadius(art, u) * scale;

  /* --- fins behind the body -------------------------------------------- */
  ctx.save();
  ctx.globalAlpha = 0.85;
  ctx.fillStyle = shadeCss(art.palette[1], -0.15);
  drawTail(ctx, art, xOf, yOf, midY, scale, bodyLen);
  if (art.dorsal > 0.05) drawDorsal(ctx, art, xOf, midY, scale);
  ctx.restore();

  /* --- body silhouette --------------------------------------------------- */
  ctx.beginPath();
  ctx.moveTo(xOf(0), midY);
  for (let i = 0; i <= 90; i++) { const u = i / 90; ctx.lineTo(xOf(u), yOf(u, 1)); }
  for (let i = 90; i >= 0; i--) { const u = i / 90; ctx.lineTo(xOf(u), yOf(u, -1.12)); }
  ctx.closePath();
  ctx.save();
  ctx.clip();

  // Fill by evaluating colourAt per pixel inside the silhouette. This is the
  // same function the mesh bakes into vertex colours, so card and fish match.
  const img = ctx.createImageData(W, H);
  const d = img.data;
  for (let py = 0; py < H; py++) {
    for (let px = 0; px < W; px++) {
      const u = (px - padX) / bodyLen;
      if (u < 0 || u > 1) continue;
      const r = bodyRadius(art, u) * scale;
      if (r <= 0) continue;
      const dy = midY - py;
      const half = dy >= 0 ? r : r * 1.12;
      if (Math.abs(dy) > half) continue;
      const v = clamp01(0.5 + (dy / half) * 0.5);
      const c = colourAt(art, u, v);
      // Specular sheen along the upper flank.
      const sheen = Math.pow(clamp01(1 - Math.abs(v - 0.66) * 4.5), 2.5) * art.gloss * 0.5;
      const lit = shade(c, sheen);
      const i = (py * W + px) * 4;
      d[i] = lit.r; d[i + 1] = lit.g; d[i + 2] = lit.b; d[i + 3] = 255;
    }
  }
  ctx.putImageData(img, 0, 0);
  ctx.restore();

  /* --- overlays ---------------------------------------------------------- */
  ctx.save();
  ctx.beginPath();
  ctx.moveTo(xOf(0), midY);
  for (let i = 0; i <= 90; i++) { const u = i / 90; ctx.lineTo(xOf(u), yOf(u, 1)); }
  for (let i = 90; i >= 0; i--) { const u = i / 90; ctx.lineTo(xOf(u), yOf(u, -1.12)); }
  ctx.closePath();
  ctx.lineWidth = Math.max(1.2, W / 380);
  ctx.strokeStyle = shadeCss(art.palette[0], -0.5);
  ctx.stroke();

  // Gill plate.
  ctx.beginPath();
  ctx.moveTo(xOf(0.20), midY - bodyRadius(art, 0.20) * scale * 0.85);
  ctx.quadraticCurveTo(xOf(0.15), midY, xOf(0.20), midY + bodyRadius(art, 0.20) * scale * 0.95);
  ctx.strokeStyle = shadeCss(art.palette[0], -0.28);
  ctx.lineWidth = Math.max(1, W / 520);
  ctx.stroke();

  // Pectoral fin in front.
  ctx.globalAlpha = 0.8;
  ctx.fillStyle = shadeCss(art.palette[1], -0.28);
  const pu = 0.28, pr = bodyRadius(art, pu) * scale;
  ctx.beginPath();
  ctx.moveTo(xOf(pu), midY + pr * 0.1);
  ctx.quadraticCurveTo(xOf(pu) - pr * 0.4, midY + pr * 1.5, xOf(pu) + pr * 0.9, midY + pr * 1.15);
  ctx.quadraticCurveTo(xOf(pu) + pr * 0.4, midY + pr * 0.4, xOf(pu), midY + pr * 0.1);
  ctx.fill();
  ctx.globalAlpha = 1;

  // Barbels.
  if (art.barbels) {
    ctx.strokeStyle = shadeCss(art.palette[1], -0.1);
    ctx.lineWidth = Math.max(1, W / 620);
    for (let i = 0; i < art.barbels; i++) {
      const sgn = i % 2 === 0 ? 1 : -1;
      const tier = Math.floor(i / 2);
      const len = bodyLen * (0.16 - tier * 0.045);
      ctx.beginPath();
      ctx.moveTo(xOf(0.04), midY + sgn * bodyRadius(art, 0.06) * scale * 0.3);
      ctx.quadraticCurveTo(xOf(0.02) - len * 0.4, midY + sgn * (0.4 + tier * 0.2) * scale * 0.2,
        xOf(0.03) - len, midY + sgn * (0.9 + tier * 0.3) * scale * 0.22);
      ctx.stroke();
    }
  }

  // Eye.
  if (art.eye > 0.01) {
    const ex = xOf(0.13);
    const ey = midY - bodyRadius(art, 0.13) * scale * 0.34;
    const er = art.eye * scale * 1.15;
    ctx.fillStyle = shadeCss(art.palette[2], 0.25);
    ctx.beginPath(); ctx.arc(ex, ey, er * 1.35, 0, Math.PI * 2); ctx.fill();
    ctx.fillStyle = '#0b0d10';
    ctx.beginPath(); ctx.arc(ex, ey, er, 0, Math.PI * 2); ctx.fill();
    ctx.fillStyle = 'rgba(255,255,255,0.85)';
    ctx.beginPath(); ctx.arc(ex - er * 0.32, ey - er * 0.32, er * 0.34, 0, Math.PI * 2); ctx.fill();
  }
  ctx.restore();

  return canvas;
}

function drawTail(ctx, art, xOf, yOf, midY, scale, bodyLen) {
  const r = bodyRadius(art, 0.97) * scale;
  const x0 = xOf(1.0);
  const span = r * (art.tail === 'lunate' ? 4.2 : art.tail === 'forked' ? 3.4 : 2.6);
  const len = r * (art.tail === 'truncate' ? 1.6 : art.tail === 'round' ? 2.0 : 2.8);
  ctx.beginPath();
  ctx.moveTo(x0, midY);
  switch (art.tail) {
    case 'forked':
      ctx.lineTo(x0 + len, midY - span * 0.5);
      ctx.quadraticCurveTo(x0 + len * 0.45, midY, x0 + len, midY + span * 0.5);
      break;
    case 'lunate':
      ctx.quadraticCurveTo(x0 + len * 0.7, midY - span * 0.3, x0 + len, midY - span * 0.5);
      ctx.quadraticCurveTo(x0 + len * 0.3, midY, x0 + len, midY + span * 0.5);
      ctx.quadraticCurveTo(x0 + len * 0.7, midY + span * 0.3, x0, midY);
      break;
    case 'round':
      ctx.quadraticCurveTo(x0 + len * 1.25, midY - span * 0.55, x0 + len * 0.9, midY);
      ctx.quadraticCurveTo(x0 + len * 1.25, midY + span * 0.55, x0, midY);
      break;
    case 'fan':
      ctx.lineTo(x0 + len * 0.9, midY - span * 0.6);
      ctx.lineTo(x0 + len * 1.1, midY);
      ctx.lineTo(x0 + len * 0.9, midY + span * 0.6);
      break;
    default:
      ctx.lineTo(x0 + len, midY - span * 0.45);
      ctx.lineTo(x0 + len, midY + span * 0.45);
  }
  ctx.closePath();
  ctx.fill();
}

function drawDorsal(ctx, art, xOf, midY, scale) {
  const from = 0.30, to = 0.30 + art.dorsal * 0.55;
  ctx.beginPath();
  ctx.moveTo(xOf(from), midY - bodyRadius(art, from) * scale * 0.9);
  const steps = 16;
  for (let i = 1; i <= steps; i++) {
    const u = mix(from, to, i / steps);
    const h = bodyRadius(art, u) * scale * (0.9 + Math.sin((i / steps) * Math.PI) * art.dorsal * 2.4);
    ctx.lineTo(xOf(u), midY - h);
  }
  for (let i = steps; i >= 0; i--) {
    const u = mix(from, to, i / steps);
    ctx.lineTo(xOf(u), midY - bodyRadius(art, u) * scale * 0.86);
  }
  ctx.closePath();
  ctx.fill();
}

function shadeCss(hex, amount) {
  const c = shade(hexToRgb(hex), amount);
  return `rgb(${c.r | 0},${c.g | 0},${c.b | 0})`;
}

/* --- caching --------------------------------------------------------------- */

const spriteCache = new Map();

/** Sprites are identical for every fish of a species, so build once. */
export function spriteFor(species, opts = {}) {
  const key = `${species.id}:${opts.width ?? 512}x${opts.height ?? 256}`;
  if (!spriteCache.has(key)) spriteCache.set(key, buildFishSprite(species.art, opts));
  return spriteCache.get(key);
}

export function spriteDataURL(species, opts = {}) {
  const c = spriteFor(species, opts);
  if (c.convertToBlob) return null;    // OffscreenCanvas: use transferToImageBitmap
  return c.toDataURL('image/png');
}

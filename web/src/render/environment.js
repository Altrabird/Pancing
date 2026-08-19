/**
 * The world around the water: lake bed, banks, sky, foliage, light and rain.
 *
 * The terrain is generated from the same `spot.depthAt()` the gameplay reads,
 * extended outward with shoreline falloff and noise detail. Nothing is
 * authored — change the spot record and the whole location changes shape.
 *
 * The sky is a shader dome rather than a baked texture so that dawn→day→dusk
 * →night is a continuous lerp of a handful of colours instead of a set of
 * cross-faded images.
 */

import * as THREE from 'three';
import { fbm2, ridged2, warped2, clamp01, smoothstep, mix } from '../assets/noise.js';
import { synth, toTexture } from '../assets/textures.js';

/* --- terrain --------------------------------------------------------------- */

/** Lateral extent of the fishable channel, matching the gameplay UV mapping. */
const PLAY_HALF_WIDTH = 18;
const PLAY_LENGTH = 34;

/**
 * Terrain height at a world point. Negative is underwater.
 * Composed of: the spot's bathymetry inside the play area, shoreline falloff on
 * all sides, a raised near bank for the angler to stand on, and noise detail.
 */
export function terrainHeight(x, z, spot, seed = 0) {
  const u = clamp01(z / PLAY_LENGTH);
  const v = Math.max(-1, Math.min(1, x / PLAY_HALF_WIDTH));

  let depth = Math.max(0, spot.depthAt(u, v)) * spot.maxDepth;

  // Shelve up toward the near bank, the far shore and both sides. The near
  // bank is deliberately steep: a long ultra-shallow margin reads on screen as
  // a wide band of bare mud between the angler and the water.
  depth *= smoothstep(-0.4, 1.7, z);
  const lateral = 1 - smoothstep(21, 54, Math.abs(x));
  depth *= lateral;
  depth *= 1 - smoothstep(115, 168, z);

  // Land rises where there is no water.
  const dryness = 1 - clamp01(depth / 0.6);
  let land = 0;
  if (dryness > 0) {
    const nearBank = smoothstep(2.5, -14, z) * 1.7;
    const sideBank = smoothstep(24, 60, Math.abs(x)) * 3.2;
    const farBank = smoothstep(118, 175, z) * 5.5;
    land = (nearBank + sideBank + farBank) * dryness;
  }

  const base = -depth + land;

  // Detail: broad undulation plus fine grain.
  const macro = (warped2(x * 0.012, z * 0.012, { octaves: 4, seed, warp: 0.7 }) - 0.5) * 1.9;
  const micro = (fbm2(x * 0.09, z * 0.09, { octaves: 3, seed: seed + 5 }) - 0.5) * 0.45;

  // Fade the detail out as the ground approaches the waterline. Undamped, the
  // relief is larger than the bank is tall, so noise dips punch below y=0 and
  // fill with stray puddles while noise peaks poke dry islands through the
  // shallows. Hills and the deep bed still get their full variation.
  const nearWater = smoothstep(0.05, 1.4, Math.abs(base));
  const detail = (macro + micro) * mix(0.25, 1.0, dryness) * mix(0.12, 1.0, nearWater);

  return base + detail;
}

export function buildTerrain(spot, textures, { quality = 'high' } = {}) {
  const segX = quality === 'low' ? 96 : quality === 'medium' ? 144 : 200;
  const segZ = quality === 'low' ? 110 : quality === 'medium' ? 170 : 240;
  const W = 240, L = 220, Z0 = -26;

  const geo = new THREE.PlaneGeometry(W, L, segX, segZ);
  geo.rotateX(-Math.PI / 2);
  geo.translate(0, 0, Z0 + L * 0.5);

  const seed = spot.id.length * 37;
  const pos = geo.attributes.position;
  for (let i = 0; i < pos.count; i++) {
    const x = pos.getX(i), z = pos.getZ(i);
    pos.setY(i, terrainHeight(x, z, spot, seed));
  }
  geo.computeVertexNormals();

  // Vertex colours blend the underwater bed palette into the dry bank palette,
  // so a single mesh and a single material cover the whole shoreline.
  const colors = new Float32Array(pos.count * 3);
  const wet = new THREE.Color(spot.palette.sand);
  const dry = new THREE.Color(spot.palette.grass);
  const deep = new THREE.Color(spot.palette.deep);
  const c = new THREE.Color();
  for (let i = 0; i < pos.count; i++) {
    const y = pos.getY(i);
    if (y < 0) c.copy(wet).lerp(deep, clamp01(-y / (spot.maxDepth * 0.8)));
    else c.copy(wet).lerp(dry, smoothstep(0.05, 1.1, y));
    colors[i * 3] = c.r; colors[i * 3 + 1] = c.g; colors[i * 3 + 2] = c.b;
  }
  geo.setAttribute('color', new THREE.BufferAttribute(colors, 3));

  const mat = new THREE.MeshStandardMaterial({
    map: textures.bed,
    normalMap: textures.bedNormals,
    normalScale: new THREE.Vector2(0.7, 0.7),
    vertexColors: true,
    roughness: 0.95,
    metalness: 0.0,
  });

  // Caustics: the light net cast on the bottom by the surface above. Injected
  // into the standard material so the bed still receives normal lighting.
  const causticUniforms = {
    uCaustics: { value: textures.caustics },
    uTime: { value: 0 },
    uCausticStrength: { value: 1 },
  };
  mat.onBeforeCompile = (shader) => {
    Object.assign(shader.uniforms, causticUniforms);
    shader.vertexShader = shader.vertexShader
      .replace('#include <common>', '#include <common>\n varying vec3 vWorldPosC;')
      .replace('#include <worldpos_vertex>',
        '#include <worldpos_vertex>\n vWorldPosC = (modelMatrix * vec4(transformed, 1.0)).xyz;');
    shader.fragmentShader = shader.fragmentShader
      .replace('#include <common>', `#include <common>
        varying vec3 vWorldPosC;
        uniform sampler2D uCaustics;
        uniform float uTime;
        uniform float uCausticStrength;`)
      .replace('#include <dithering_fragment>', `#include <dithering_fragment>
        // Two caustic layers drifting in opposite directions; their product is
        // what gives the characteristic writhing net rather than a sliding one.
        float depthBelow = -vWorldPosC.y;
        if (depthBelow > 0.0) {
          vec2 cuv1 = vWorldPosC.xz * 0.155 + vec2(uTime * 0.026, uTime * 0.018);
          vec2 cuv2 = vWorldPosC.xz * 0.205 - vec2(uTime * 0.021, uTime * 0.031);
          float c1 = texture2D(uCaustics, cuv1).r;
          float c2 = texture2D(uCaustics, cuv2).r;
          float caustic = c1 * c2 * 1.45;
          // Strongest just under the surface, gone in the depths.
          float fade = exp(-depthBelow * 0.55);
          gl_FragColor.rgb += vec3(0.85, 0.95, 0.88) * caustic * fade * uCausticStrength;
        }`);
    mat.userData.shader = shader;
  };

  const mesh = new THREE.Mesh(geo, mat);
  mesh.receiveShadow = true;
  mesh.name = 'terrain';
  mesh.userData.causticUniforms = causticUniforms;
  return mesh;
}

/* --- sky ------------------------------------------------------------------- */

const skyVert = /* glsl */ `
  varying vec3 vDir;
  varying vec2 vUvS;
  void main() {
    vDir = normalize(position);
    vUvS = uv;
    gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
  }
`;

const skyFrag = /* glsl */ `
  uniform vec3 uHorizon;
  uniform vec3 uZenith;
  uniform vec3 uSunColor;
  uniform vec3 uSunDir;
  uniform float uCloud;
  uniform float uLight;
  uniform float uNight;
  uniform float uTime;
  uniform sampler2D uClouds;
  uniform sampler2D uStars;

  varying vec3 vDir;
  varying vec2 vUvS;

  void main() {
    float h = clamp(vDir.y, 0.0, 1.0);
    // Nonlinear gradient: real skies deepen quickly just above the horizon.
    vec3 col = mix(uHorizon, uZenith, pow(h, 0.62));

    // Stars, only at night, fading out toward the horizon haze.
    if (uNight > 0.01) {
      vec4 stars = texture2D(uStars, vUvS * vec2(2.0, 1.0));
      col += stars.rgb * stars.a * uNight * smoothstep(0.02, 0.35, h);
    }

    // Sun / moon disc and its glow.
    float sunDot = max(dot(normalize(vDir), normalize(uSunDir)), 0.0);
    col += uSunColor * pow(sunDot, 900.0) * 3.0;
    col += uSunColor * pow(sunDot, 12.0) * 0.35 * uLight;
    // Horizon scatter warms the sky near the sun at low elevation.
    col += uSunColor * pow(sunDot, 3.0) * 0.16 * (1.0 - smoothstep(0.0, 0.5, h));

    // Cloud deck: sampled in a flattened projection so it converges at the
    // horizon like a real ceiling instead of wrapping like a ball.
    if (h > -0.02) {
      vec2 cuv = vDir.xz / max(vDir.y + 0.16, 0.055) * 0.10;
      cuv += vec2(uTime * 0.0032, uTime * 0.0021);
      float n = texture2D(uClouds, cuv).r;
      float n2 = texture2D(uClouds, cuv * 2.3 + 0.37).r;
      float density = n * 0.68 + n2 * 0.32;
      float cover = smoothstep(0.60 - uCloud * 0.36, 0.85 - uCloud * 0.16, density);
      cover *= smoothstep(0.0, 0.18, h);
      // Lit tops, shaded bellies.
      vec3 cloudCol = mix(vec3(0.30, 0.33, 0.38), vec3(1.02, 1.0, 0.97), density);
      cloudCol *= mix(0.35, 1.0, uLight);
      cloudCol += uSunColor * pow(sunDot, 6.0) * 0.30 * uLight;
      col = mix(col, cloudCol, cover * clamp(uCloud * 1.5, 0.0, 1.0));
    }

    gl_FragColor = vec4(col, 1.0);
    #include <tonemapping_fragment>
    #include <colorspace_fragment>
  }
`;

export function buildSky(textures, seed = 91) {
  // Grayscale cloud field, generated once and animated by scrolling.
  const cloudCanvas = synth(512, (x, y, u, v) => {
    const n = warped2(u * 4, v * 4, { octaves: 6, seed, warp: 1.0, period: 4 });
    const wisp = ridged2(u * 8, v * 8, { octaves: 3, seed: seed + 3, period: 8 });
    const c = clamp01(n * 0.78 + wisp * 0.22) * 255;
    return { r: c, g: c, b: c };
  });

  const uniforms = {
    uHorizon: { value: new THREE.Color('#dfe9ee') },
    uZenith: { value: new THREE.Color('#5f8fbe') },
    uSunColor: { value: new THREE.Color('#fff0d4') },
    uSunDir: { value: new THREE.Vector3(0.4, 0.6, 0.5).normalize() },
    uCloud: { value: 0.35 },
    uLight: { value: 1 },
    uNight: { value: 0 },
    uTime: { value: 0 },
    uClouds: { value: toTexture(cloudCanvas, { repeat: 1 }) },
    uStars: { value: textures.stars },
  };

  const mesh = new THREE.Mesh(
    new THREE.SphereGeometry(600, 48, 32),
    new THREE.ShaderMaterial({
      uniforms, vertexShader: skyVert, fragmentShader: skyFrag,
      side: THREE.BackSide, depthWrite: false, fog: false,
    }),
  );
  mesh.name = 'sky';
  mesh.renderOrder = -1;
  mesh.userData.uniforms = uniforms;
  return mesh;
}

/* --- foliage --------------------------------------------------------------- */

/**
 * Trees and reeds as billboards, instanced into two draw calls. At 30–80 m
 * across water this reads better than low-poly geometry and costs almost
 * nothing. They are given a fixed random yaw at placement rather than tracking
 * the camera, which is fine here because the view never orbits more than a few
 * degrees — and it keeps them in one static instanced draw.
 *
 * The materials are unlit (billboard normals are meaningless), so the scene
 * modulates their colour by the time-of-day light level instead. Without that
 * the trees stay in full daylight at midnight.
 */
export function buildFoliage(spot, textures, rng, { quality = 'high' } = {}) {
  const group = new THREE.Group();
  group.name = 'foliage';

  const treeCount = quality === 'low' ? 60 : quality === 'medium' ? 110 : 180;
  const reedCount = quality === 'low' ? 80 : quality === 'medium' ? 150 : 260;
  const seed = spot.id.length * 37;

  const treeGeo = new THREE.PlaneGeometry(1, 1);
  const treeMat = new THREE.MeshBasicMaterial({
    map: textures.tree, transparent: true, alphaTest: 0.28,
    side: THREE.DoubleSide, depthWrite: true,
  });
  const trees = new THREE.InstancedMesh(treeGeo, treeMat, treeCount);
  const dummy = new THREE.Object3D();

  let placed = 0, tries = 0;
  while (placed < treeCount && tries < treeCount * 40) {
    tries++;
    const x = rng.float(-115, 115);
    const z = rng.float(-24, 190);
    const y = terrainHeight(x, z, spot, seed);
    // Only on dry land, and not on the little bank the angler occupies.
    if (y < 0.85) continue;
    if (Math.abs(x) < 26 && z < 12) continue;
    const scale = rng.float(6, 15) * (1 + Math.min(z, 150) / 260);
    dummy.position.set(x, y + scale * 0.48, z);
    dummy.scale.set(scale * 0.85, scale, 1);
    dummy.rotation.set(0, rng.float(-0.35, 0.35), 0);
    dummy.updateMatrix();
    trees.setMatrixAt(placed++, dummy.matrix);
  }
  trees.count = placed;
  trees.instanceMatrix.needsUpdate = true;
  trees.userData.billboard = true;
  group.add(trees);

  const reedGeo = new THREE.PlaneGeometry(1, 1);
  const reedMat = new THREE.MeshBasicMaterial({
    map: textures.reed, transparent: true, alphaTest: 0.2,
    side: THREE.DoubleSide, depthWrite: false,
  });
  const reeds = new THREE.InstancedMesh(reedGeo, reedMat, reedCount);
  let rplaced = 0; tries = 0;
  while (rplaced < reedCount && tries < reedCount * 40) {
    tries++;
    const x = rng.float(-60, 60);
    const z = rng.float(-6, 90);
    const y = terrainHeight(x, z, spot, seed);
    // Reeds live right at the waterline.
    if (y > 0.35 || y < -0.75) continue;
    if (Math.abs(x) < 8 && z < 8) continue;
    const scale = rng.float(1.1, 2.6);
    dummy.position.set(x, y + scale * 0.42, z);
    dummy.scale.set(scale * 0.8, scale, 1);
    dummy.rotation.set(0, rng.float(-0.5, 0.5), 0);
    dummy.updateMatrix();
    reeds.setMatrixAt(rplaced++, dummy.matrix);
  }
  reeds.count = rplaced;
  reeds.instanceMatrix.needsUpdate = true;
  group.add(reeds);

  // Unlit billboards: the scene tints these by daylight each frame.
  group.userData.litMaterials = [treeMat, reedMat];

  // Structure markers: the snags the fight AI actually steers toward, made
  // visible so the player can read the danger before hooking into it.
  const snagMat = new THREE.MeshStandardMaterial({ color: 0x3b3128, roughness: 0.95 });
  for (const s of spot.structure) {
    const wx = s.v * PLAY_HALF_WIDTH;
    const wz = s.u * PLAY_LENGTH;
    const bed = terrainHeight(wx, wz, spot, seed);
    if (s.kind === 'timber') {
      const log = new THREE.Mesh(new THREE.CylinderGeometry(0.16, 0.22, s.r * 22, 7), snagMat);
      log.position.set(wx, bed + 0.35, wz);
      log.rotation.set(0.15, rng.float(0, Math.PI), Math.PI / 2 + rng.float(-0.3, 0.3));
      group.add(log);
    } else if (s.kind === 'rock') {
      const rock = new THREE.Mesh(new THREE.IcosahedronGeometry(s.r * 9, 1), snagMat);
      rock.position.set(wx, bed + s.r * 4, wz);
      rock.scale.set(1, 0.65, 1.1);
      group.add(rock);
    }
  }

  return group;
}

/* --- rain ------------------------------------------------------------------ */

export function buildRain(count = 2600) {
  const positions = new Float32Array(count * 3);
  const speeds = new Float32Array(count);
  for (let i = 0; i < count; i++) {
    positions[i * 3] = (Math.random() - 0.5) * 90;
    positions[i * 3 + 1] = Math.random() * 34;
    positions[i * 3 + 2] = Math.random() * 90 - 12;
    speeds[i] = 18 + Math.random() * 16;
  }
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
  geo.setAttribute('speed', new THREE.BufferAttribute(speeds, 1));

  const mat = new THREE.PointsMaterial({
    color: 0xbcd2dd, size: 0.055, transparent: true, opacity: 0.0,
    depthWrite: false, sizeAttenuation: true,
  });
  const points = new THREE.Points(geo, mat);
  points.frustumCulled = false;
  points.name = 'rain';

  points.userData.update = (dt, intensity, camPos) => {
    mat.opacity = intensity * 0.55;
    if (intensity <= 0.01) { points.visible = false; return; }
    points.visible = true;
    const p = geo.attributes.position.array;
    const s = geo.attributes.speed.array;
    for (let i = 0; i < count; i++) {
      p[i * 3 + 1] -= s[i] * dt * (0.5 + intensity);
      if (p[i * 3 + 1] < -1) {
        p[i * 3 + 1] = 30 + Math.random() * 6;
        p[i * 3] = camPos.x + (Math.random() - 0.5) * 90;
        p[i * 3 + 2] = camPos.z + (Math.random() - 0.2) * 90;
      }
    }
    geo.attributes.position.needsUpdate = true;
  };
  return points;
}

/* --- lighting -------------------------------------------------------------- */

export function buildLights() {
  const sun = new THREE.DirectionalLight(0xfff2d8, 2.2);
  sun.position.set(28, 40, 20);
  sun.castShadow = true;
  sun.shadow.mapSize.set(1024, 1024);
  sun.shadow.camera.near = 1;
  sun.shadow.camera.far = 140;
  const d = 45;
  sun.shadow.camera.left = -d; sun.shadow.camera.right = d;
  sun.shadow.camera.top = d; sun.shadow.camera.bottom = -d;
  sun.shadow.bias = -0.0008;

  const hemi = new THREE.HemisphereLight(0xbcd8ee, 0x4a4636, 0.85);
  const ambient = new THREE.AmbientLight(0xffffff, 0.18);

  return { sun, hemi, ambient };
}

/**
 * Time-of-day palette. One function maps the world clock to every colour in the
 * scene, so sky, sun, water tint and fog can never drift out of agreement.
 */
export function skyPalette(hour, weather, spotPalette) {
  const stops = [
    { h: 0.0,  horizon: '#141c2b', zenith: '#050810', sun: '#6f7fa8', light: 0.05, night: 1.0 },
    { h: 5.0,  horizon: '#3a3550', zenith: '#101a33', sun: '#8b7ba6', light: 0.10, night: 0.85 },
    { h: 6.5,  horizon: '#e8a273', zenith: '#4a6d99', sun: '#ffc78a', light: 0.45, night: 0.15 },
    { h: 8.0,  horizon: '#cfe0ea', zenith: '#5f8fbe', sun: '#fff0d4', light: 0.88, night: 0.0 },
    { h: 12.0, horizon: '#dfe9ee', zenith: '#4f86c4', sun: '#ffffff', light: 1.00, night: 0.0 },
    { h: 16.5, horizon: '#dbe4e6', zenith: '#5c8dbe', sun: '#fff4de', light: 0.90, night: 0.0 },
    { h: 18.3, horizon: '#e79a5c', zenith: '#4a5f8d', sun: '#ff9f5c', light: 0.42, night: 0.12 },
    { h: 19.6, horizon: '#7b5a70', zenith: '#222c4a', sun: '#c07a86', light: 0.16, night: 0.55 },
    { h: 21.0, horizon: '#1d2436', zenith: '#080c18', sun: '#7d8bb0', light: 0.06, night: 1.0 },
    { h: 24.0, horizon: '#141c2b', zenith: '#050810', sun: '#6f7fa8', light: 0.05, night: 1.0 },
  ];

  const h = ((hour % 24) + 24) % 24;
  let a = stops[0], b = stops[stops.length - 1], t = 0;
  for (let i = 0; i < stops.length - 1; i++) {
    if (h >= stops[i].h && h <= stops[i + 1].h) {
      a = stops[i]; b = stops[i + 1];
      t = (h - a.h) / Math.max(b.h - a.h, 1e-4);
      break;
    }
  }

  const lerpC = (x, y) => new THREE.Color(x).lerp(new THREE.Color(y), t);
  const overcast = weather ? 1 - (1 - weather.light) * 0.9 : 1;

  return {
    horizon: lerpC(a.horizon, b.horizon),
    zenith: lerpC(a.zenith, b.zenith),
    sunColor: lerpC(a.sun, b.sun),
    light: mix(a.light, b.light, t) * overcast,
    night: mix(a.night, b.night, t),
    // Sun travels a simple arc; the shader and the DirectionalLight share it.
    sunDir: new THREE.Vector3(
      Math.cos((h / 24) * Math.PI * 2 - Math.PI * 0.5) * 0.8,
      Math.sin(((h - 6) / 12) * Math.PI) * 0.9 + 0.08,
      0.45,
    ).normalize(),
  };
}

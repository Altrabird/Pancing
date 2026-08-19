/**
 * Water.
 *
 * Four things have to be true at once for water to read as water:
 *   1. the surface must actually move (displacement, not a scrolling texture)
 *   2. it must reflect the sky and the bank
 *   3. it must get darker and less transparent with depth
 *   4. it must respond to things touching it
 *
 * So: Gerstner waves displace real vertices in the vertex shader with normals
 * derived analytically from the wave derivatives; a mirrored camera renders a
 * planar reflection; a second pass gives a refraction buffer sampled through a
 * normal-distorted UV; and a small ring buffer of ripple sources injects radial
 * waves wherever the lure, a fish or a splash touches the surface.
 *
 * The depth term does not come from a depth buffer — it is sampled from a map
 * baked off `terrainHeight()`, which is built on the SAME `spot.depthAt()` the
 * gameplay uses to decide which fish live where. So the shallows you can see
 * are the shallows you are fishing, and the water ends exactly where the ground
 * rises out of it rather than at some separately authored waterline.
 */

import * as THREE from 'three';
import { terrainHeight } from './environment.js';

export const MAX_RIPPLES = 14;

/* --- the shader ------------------------------------------------------------ */

const vertexShader = /* glsl */ `
  #define MAX_RIPPLES ${MAX_RIPPLES}

  uniform float uTime;
  uniform float uWind;          // 0..~2, scales amplitude and steepness
  uniform float uChop;
  uniform vec4  uRipples[MAX_RIPPLES];   // xz = world pos, z = birth time, w = strength
  uniform vec4  uBounds;        // minX, minZ, spanX, spanZ of the water plane
  uniform sampler2D uDepthMap;
  uniform float uMaxDepth;

  varying vec3 vWorld;
  varying vec3 vNormal;
  varying vec4 vClip;
  varying float vDepth;         // metres of water beneath this point
  varying float vCrest;         // 0..1 how close to a breaking crest
  varying vec2 vUv;
  varying vec2 vDepthUv;        // re-sampled per fragment for a smooth waterline

  // One Gerstner wave: returns displacement, and accumulates the surface
  // tangent derivatives so the normal stays exact under displacement.
  vec3 gerstner(vec2 pos, vec2 dir, float steep, float wavelength, float speed,
                float t, inout vec3 tangent, inout vec3 binormal) {
    float k = 6.28318530718 / wavelength;
    float c = sqrt(9.81 / k);
    vec2 d = normalize(dir);
    float f = k * (dot(d, pos) - c * speed * t);
    float a = steep / k;

    tangent += vec3(
      -d.x * d.x * steep * sin(f),
       d.x * steep * cos(f),
      -d.x * d.y * steep * sin(f)
    );
    binormal += vec3(
      -d.x * d.y * steep * sin(f),
       d.y * steep * cos(f),
      -d.y * d.y * steep * sin(f)
    );
    return vec3(d.x * a * cos(f), a * sin(f), d.y * a * cos(f));
  }

  void main() {
    vec3 world = (modelMatrix * vec4(position, 1.0)).xyz;
    vUv = uv;

    // Depth under this vertex, from the bathymetry baked off the terrain.
    // uBounds = (minX, minZ, spanX, spanZ) of the water plane in world space, so
    // this lookup covers the whole plane and agrees with the ground exactly —
    // including the dry banks, where the depth is zero and the water vanishes.
    vec2 duv = vec2(
      (world.x - uBounds.x) / uBounds.z,
      (world.z - uBounds.y) / uBounds.w
    );
    vDepthUv = clamp(duv, 0.0, 1.0);
    float depthN = texture2D(uDepthMap, vDepthUv).r;
    vDepth = depthN * uMaxDepth;

    // Waves die out in the shallows: a swell cannot be taller than the water
    // it is standing in. This is what makes the margins settle down naturally.
    float shoal = smoothstep(0.0, 0.9, vDepth);
    // Inland scale. Wave height is steepness * wavelength / 2pi, so these two
    // together give roughly 8 cm of chop in a light breeze and 40 cm in a
    // storm — lake water, not open ocean.
    float amp = (0.35 + uWind * 0.55) * shoal;
    float steep = (0.055 + uChop * 0.055) * shoal;

    vec3 tangent = vec3(1.0, 0.0, 0.0);
    vec3 binormal = vec3(0.0, 0.0, 1.0);
    vec3 disp = vec3(0.0);

    disp += gerstner(world.xz, vec2( 1.00,  0.22), steep * 1.00, 9.4 * amp, 1.00, uTime, tangent, binormal);
    disp += gerstner(world.xz, vec2( 0.62, -0.78), steep * 0.62, 5.1 * amp, 1.18, uTime, tangent, binormal);
    disp += gerstner(world.xz, vec2(-0.35,  0.94), steep * 0.44, 2.7 * amp, 1.42, uTime, tangent, binormal);
    disp += gerstner(world.xz, vec2( 0.88,  0.47), steep * 0.28, 1.35 * amp, 1.75, uTime, tangent, binormal);

    // --- dynamic ripples ---------------------------------------------------
    // Each source is a decaying radial wave packet. Cheap, and it is what makes
    // the lure landing feel like it hit something real.
    for (int i = 0; i < MAX_RIPPLES; i++) {
      vec4 r = uRipples[i];
      if (r.w <= 0.001) continue;
      float age = uTime - r.z;
      if (age < 0.0 || age > 3.2) continue;
      float dist = distance(world.xz, r.xy);
      float front = age * 2.6;                       // expanding ring
      float band = exp(-pow((dist - front) * 1.7, 2.0));
      float decay = exp(-age * 1.15) * exp(-dist * 0.10);
      float h = sin((dist - front) * 7.0) * band * decay * r.w * 0.30;
      disp.y += h;
      // Perturb the tangents so the ripple actually catches light.
      vec2 dir = dist > 0.001 ? (world.xz - r.xy) / dist : vec2(0.0);
      tangent.y  += dir.x * h * 6.0;
      binormal.y += dir.y * h * 6.0;
    }

    world += disp;
    vWorld = world;
    vNormal = normalize(cross(binormal, tangent));
    vCrest = smoothstep(0.25, 0.85, disp.y / max(amp * 0.55, 0.001));

    vec4 mvPosition = viewMatrix * vec4(world, 1.0);
    vClip = projectionMatrix * mvPosition;
    gl_Position = vClip;
  }
`;

const fragmentShader = /* glsl */ `
  uniform float uTime;
  uniform vec3  uShallow;
  uniform vec3  uDeep;
  uniform vec3  uFoamColor;
  uniform vec3  uSunDir;
  uniform vec3  uSunColor;
  uniform float uLight;
  uniform float uClarity;
  uniform float uWind;
  uniform float uRain;
  uniform sampler2D uNormalMap;
  uniform sampler2D uReflection;
  uniform sampler2D uRefraction;
  uniform float uReflectStrength;

  varying vec3 vWorld;
  varying vec3 vNormal;
  varying vec4 vClip;
  varying float vDepth;
  varying float vCrest;
  varying vec2 vUv;
  varying vec2 vDepthUv;
  uniform sampler2D uDepthMap;
  uniform float uMaxDepth;

  void main() {
    vec3 viewDir = normalize(cameraPosition - vWorld);

    // Re-sample the bathymetry per fragment. The vertex-stage value is fine for
    // shaping waves, but using it for the shoreline quantises the waterline to
    // the mesh tessellation and gives a visibly sawtoothed edge.
    float depth = texture2D(uDepthMap, vDepthUv).r * uMaxDepth;

    // --- surface normal ----------------------------------------------------
    // Wave normal from the vertex stage, detailed by two normal maps scrolling
    // at different speeds and scales. The speed difference is what stops the
    // detail from looking like a sliding decal.
    vec2 uv1 = vWorld.xz * 0.055 + vec2(uTime * 0.021, uTime * 0.014);
    vec2 uv2 = vWorld.xz * 0.145 - vec2(uTime * 0.033, uTime * 0.027);
    vec3 n1 = texture2D(uNormalMap, uv1).xyz * 2.0 - 1.0;
    vec3 n2 = texture2D(uNormalMap, uv2).xyz * 2.0 - 1.0;
    vec3 detail = normalize(n1 + n2 * 0.55);
    // Rain stipples the surface at a third, much finer scale.
    if (uRain > 0.01) {
      vec3 n3 = texture2D(uNormalMap, vWorld.xz * 0.9 + vec2(uTime * 0.4, uTime * 0.31)).xyz * 2.0 - 1.0;
      detail = normalize(detail + n3 * uRain * 0.9);
    }
    float detailAmt = 0.28 + uWind * 0.22;
    vec3 normal = normalize(vNormal + vec3(detail.x, 0.0, detail.y) * detailAmt);

    // --- screen-space UVs for the reflection / refraction buffers ----------
    vec2 ndc = (vClip.xy / vClip.w) * 0.5 + 0.5;
    vec2 distort = vec2(detail.x, detail.y) * (0.020 + uWind * 0.012);

    vec3 reflCol = texture2D(uReflection, clamp(vec2(1.0 - ndc.x, ndc.y) + distort, 0.001, 0.999)).rgb;
    vec3 refrCol = texture2D(uRefraction, clamp(ndc + distort * 0.55, 0.001, 0.999)).rgb;

    // --- body colour -------------------------------------------------------
    // Beer-Lambert style falloff: how much of the bottom survives the trip back
    // up through the water. Clear water shows the bed much deeper.
    float clarity = mix(0.35, 1.4, uClarity);
    float transmit = exp(-depth / max(clarity * 1.6, 0.05));
    vec3 waterBody = mix(uDeep, uShallow, transmit);
    vec3 belowSurface = mix(waterBody, refrCol, transmit * 0.85);

    // --- fresnel -----------------------------------------------------------
    // Schlick. At grazing angles water is a mirror; looking straight down it is
    // a window. Getting this term right does more for realism than anything.
    float cosTheta = clamp(dot(viewDir, normal), 0.0, 1.0);
    float fresnel = 0.02 + 0.98 * pow(1.0 - cosTheta, 5.0);
    fresnel = clamp(fresnel, 0.02, 0.95) * uReflectStrength;

    vec3 color = mix(belowSurface, reflCol, fresnel);

    // --- specular sun highlight -------------------------------------------
    vec3 halfDir = normalize(uSunDir + viewDir);
    float spec = pow(max(dot(normal, halfDir), 0.0), 220.0);
    // A second, broad lobe gives the shimmering path across the water.
    float glitter = pow(max(dot(normal, halfDir), 0.0), 42.0) * 0.075;
    color += uSunColor * (spec * 2.2 + glitter) * uLight;

    // --- subsurface scatter on wave crests ---------------------------------
    // Backlit wave tops glow. Cheap approximation: brighten where the wave is
    // high and the view is grazing.
    float sss = vCrest * pow(1.0 - cosTheta, 2.0) * 0.35;
    color += uShallow * sss * uLight;

    // --- foam ---------------------------------------------------------------
    // Shoreline foam where the water is shallow, plus whitecaps on crests when
    // it is blowing. The shore band is kept tight: on a gently shelving bank a
    // wide depth range projects into a huge stripe across the screen.
    // Foam is wave energy, not a property of shallow water. Gated on wind so a
    // dead-calm pond gets none — otherwise every shoreline wears a white halo.
    float shoreFoam = (1.0 - smoothstep(0.02, 0.26, depth)) * smoothstep(0.25, 1.1, uWind);
    float capFoam = smoothstep(0.72, 1.0, vCrest) * smoothstep(0.6, 1.6, uWind);
    float foam = clamp(shoreFoam * 0.55 + capFoam * 0.7, 0.0, 1.0);
    // Break the foam edge up so it is not a clean contour line.
    float wobble = texture2D(uNormalMap, vWorld.xz * 0.35 + uTime * 0.02).r;
    foam *= smoothstep(0.25, 0.75, wobble + 0.25);
    color = mix(color, uFoamColor, foam);

    // Dry ground carries no water at all: the plane covers the whole basin
    // including the banks, so anything at zero depth must disappear rather than
    // lie over the terrain as a translucent sheet.
    if (depth <= 0.010) discard;
    float edge = smoothstep(0.010, 0.16, depth);

    gl_FragColor = vec4(color, edge);

    #include <tonemapping_fragment>
    #include <colorspace_fragment>
  }
`;

/* --- the water plane ------------------------------------------------------- */

export class Water {
  /**
   * @param {object} opts
   *   spot      location record (palette, depthAt, maxDepth, waterClarity)
   *   extent    { x, z } world half-width and length of the water plane
   *   renderer  THREE.WebGLRenderer (for capability queries)
   *   normalMap THREE.Texture from the procedural pipeline
   *   quality   'low' | 'medium' | 'high'
   */
  constructor(opts) {
    const { spot, renderer, normalMap, quality = 'high' } = opts;
    this.spot = spot;
    this.extent = opts.extent ?? { x: 110, z: 190 };

    const seg = quality === 'low' ? 96 : quality === 'medium' ? 160 : 240;
    const geo = new THREE.PlaneGeometry(this.extent.x * 2, this.extent.z, seg, seg);
    geo.rotateX(-Math.PI / 2);
    geo.translate(0, 0, this.extent.z * 0.5 - 8);

    // World-space footprint of the plane; the depth map is baked over exactly
    // this rectangle so the shader lookup is a straight linear remap.
    this.bounds = new THREE.Vector4(
      -this.extent.x, -8, this.extent.x * 2, this.extent.z,
    );
    this.depthMap = buildDepthMap(spot, this.bounds, 320);

    const rtSize = quality === 'low' ? 256 : quality === 'medium' ? 512 : 768;
    const rtOpts = { minFilter: THREE.LinearFilter, magFilter: THREE.LinearFilter, type: THREE.HalfFloatType };
    this.reflectionRT = new THREE.WebGLRenderTarget(rtSize, rtSize, rtOpts);
    this.refractionRT = new THREE.WebGLRenderTarget(rtSize, rtSize, rtOpts);

    const p = spot.palette;
    this.uniforms = {
      uTime: { value: 0 },
      uWind: { value: 0.6 },
      uChop: { value: 0.6 },
      uRipples: { value: Array.from({ length: MAX_RIPPLES }, () => new THREE.Vector4(0, 0, 0, 0)) },
      uBounds: { value: this.bounds },
      uDepthMap: { value: this.depthMap },
      uMaxDepth: { value: spot.maxDepth },
      uShallow: { value: new THREE.Color(p.shallow) },
      uDeep: { value: new THREE.Color(p.deep) },
      uFoamColor: { value: new THREE.Color(p.foam) },
      uSunDir: { value: new THREE.Vector3(0.4, 0.7, 0.55).normalize() },
      uSunColor: { value: new THREE.Color('#fff2d8') },
      uLight: { value: 1 },
      uClarity: { value: spot.waterClarity },
      uRain: { value: 0 },
      uNormalMap: { value: normalMap },
      uReflection: { value: this.reflectionRT.texture },
      uRefraction: { value: this.refractionRT.texture },
      uReflectStrength: { value: 1 },
    };

    this.material = new THREE.ShaderMaterial({
      uniforms: this.uniforms,
      vertexShader,
      fragmentShader,
      transparent: true,
      side: THREE.FrontSide,
    });

    this.mesh = new THREE.Mesh(geo, this.material);
    this.mesh.name = 'water';
    this.mesh.renderOrder = 2;

    // Planar reflection machinery.
    this.reflectCamera = new THREE.PerspectiveCamera();
    this.reflectPlane = new THREE.Plane(new THREE.Vector3(0, 1, 0), 0);
    this._ripplePtr = 0;
    this._clipBias = 0.02;
  }

  /** Inject a ripple at a world position. Oldest slot is recycled. */
  addRipple(x, z, strength = 0.5) {
    const slot = this.uniforms.uRipples.value[this._ripplePtr];
    slot.set(x, z, this.uniforms.uTime.value, Math.min(strength, 1.6));
    this._ripplePtr = (this._ripplePtr + 1) % MAX_RIPPLES;
  }

  /** Drive the surface from the world state each frame. */
  setEnvironment({ wind, chop, light, rain, sunDir, sunColor }) {
    const u = this.uniforms;
    if (wind != null) u.uWind.value = wind;
    if (chop != null) u.uChop.value = chop;
    if (light != null) u.uLight.value = light;
    if (rain != null) u.uRain.value = rain;
    if (sunDir) u.uSunDir.value.copy(sunDir).normalize();
    if (sunColor) u.uSunColor.value.copy(sunColor);
  }

  setSpot(spot) {
    this.spot = spot;
    this.depthMap?.dispose();
    this.depthMap = buildDepthMap(spot, this.bounds, 320);
    const u = this.uniforms;
    u.uDepthMap.value = this.depthMap;
    u.uMaxDepth.value = spot.maxDepth;
    u.uClarity.value = spot.waterClarity;
    u.uShallow.value.set(spot.palette.shallow);
    u.uDeep.value.set(spot.palette.deep);
    u.uFoamColor.value.set(spot.palette.foam);
  }

  /**
   * Render the reflection and refraction buffers. Must be called before the
   * main scene render, with the water hidden from both passes.
   */
  updateBuffers(renderer, scene, camera) {
    this.mesh.visible = false;

    // --- refraction: the scene as seen straight through, water removed -----
    const prevTarget = renderer.getRenderTarget();
    renderer.setRenderTarget(this.refractionRT);
    renderer.clear();
    renderer.render(scene, camera);

    // --- reflection: mirror the camera through the water plane -------------
    // Standard planar reflection: reflect position and orientation about y=0,
    // then skew the projection's near plane onto the water so nothing below the
    // surface leaks into the mirror image.
    const rc = this.reflectCamera;
    rc.copy(camera);
    rc.position.copy(camera.position);
    rc.position.y = -camera.position.y;      // water sits at y = 0

    const target = new THREE.Vector3();
    camera.getWorldDirection(target);
    target.add(camera.position);
    target.y = -target.y;
    rc.up.set(0, 1, 0);
    rc.lookAt(target);
    rc.up.set(0, -1, 0);
    rc.lookAt(target);
    rc.updateMatrixWorld();
    rc.projectionMatrix.copy(camera.projectionMatrix);

    const clipPlane = new THREE.Plane(new THREE.Vector3(0, 1, 0), this._clipBias);
    clipPlane.applyMatrix4(rc.matrixWorldInverse);
    const cp = new THREE.Vector4(clipPlane.normal.x, clipPlane.normal.y, clipPlane.normal.z, clipPlane.constant);
    const proj = rc.projectionMatrix;
    const q = new THREE.Vector4(
      (Math.sign(cp.x) + proj.elements[8]) / proj.elements[0],
      (Math.sign(cp.y) + proj.elements[9]) / proj.elements[5],
      -1.0,
      (1.0 + proj.elements[10]) / proj.elements[14],
    );
    cp.multiplyScalar(2.0 / cp.dot(q));
    proj.elements[2] = cp.x;
    proj.elements[6] = cp.y;
    proj.elements[10] = cp.z + 1.0;
    proj.elements[14] = cp.w;

    renderer.setRenderTarget(this.reflectionRT);
    renderer.clear();
    renderer.render(scene, rc);

    renderer.setRenderTarget(prevTarget);
    this.mesh.visible = true;
  }

  update(dt) {
    this.uniforms.uTime.value += dt;
  }

  /** Surface height at a world point, matching the vertex shader's waves.
   *  Used to float the bobber and the fish on the real surface. */
  heightAt(x, z) {
    const t = this.uniforms.uTime.value;
    const depth = this.depthAtWorld(x, z);
    const shoal = smoothstep01(0, 0.9, depth);
    const amp = (0.35 + this.uniforms.uWind.value * 0.55) * shoal;
    const steep = (0.055 + this.uniforms.uChop.value * 0.055) * shoal;
    let y = 0;
    // Must stay in lockstep with the four gerstner() calls in the vertex shader,
    // or floating objects will sit off the surface they appear to be on.
    const waves = [
      [1.00, 0.22, 1.00, 9.4, 1.00],
      [0.62, -0.78, 0.62, 5.1, 1.18],
      [-0.35, 0.94, 0.44, 2.7, 1.42],
      [0.88, 0.47, 0.28, 1.35, 1.75],
    ];
    for (const [dx, dz, s, wl, sp] of waves) {
      const len = Math.hypot(dx, dz) || 1;
      const k = (2 * Math.PI) / Math.max(wl * amp, 0.001);
      const c = Math.sqrt(9.81 / k);
      const f = k * ((dx / len) * x + (dz / len) * z - c * sp * t);
      y += (steep * s / k) * Math.sin(f);
    }
    return y;
  }

  depthAtWorld(x, z) {
    return Math.max(0, -terrainHeight(x, z, this.spot, this.spot.id.length * 37));
  }

  dispose() {
    this.mesh.geometry.dispose();
    this.material.dispose();
    this.reflectionRT.dispose();
    this.refractionRT.dispose();
    this.depthMap.dispose();
  }
}

/**
 * Bake `spot.depthAt()` into a texture the shader can sample. Red channel is
 * normalised depth. This is the bridge between the gameplay bathymetry and the
 * visual one — they cannot disagree, because there is only one function.
 */
/**
 * Bake the bathymetry the shader samples.
 *
 * This is derived from `terrainHeight()` — the same function that builds the
 * lake-bed mesh, which in turn is built on `spot.depthAt()`, the function the
 * gameplay uses to decide which fish live where. One source of truth: the water
 * cannot be deep where the ground is high, and the shallows you can see are the
 * shallows the catch table is scoring against.
 */
export function buildDepthMap(spot, bounds, size = 320) {
  const seed = spot.id.length * 37;
  const data = new Uint8Array(size * size * 4);
  for (let j = 0; j < size; j++) {
    const z = bounds.y + (j / (size - 1)) * bounds.w;
    for (let i = 0; i < size; i++) {
      const x = bounds.x + (i / (size - 1)) * bounds.z;
      const depth = Math.max(0, -terrainHeight(x, z, spot, seed));
      const n = Math.min(1, depth / spot.maxDepth);
      const idx = (j * size + i) * 4;
      const b = Math.round(n * 255);
      data[idx] = b; data[idx + 1] = b; data[idx + 2] = b; data[idx + 3] = 255;
    }
  }
  const tex = new THREE.DataTexture(data, size, size, THREE.RGBAFormat);
  tex.minFilter = THREE.LinearFilter;
  tex.magFilter = THREE.LinearFilter;
  tex.wrapS = tex.wrapT = THREE.ClampToEdgeWrapping;
  tex.needsUpdate = true;
  return tex;
}

function smoothstep01(a, b, x) {
  const t = Math.min(Math.max((x - a) / (b - a), 0), 1);
  return t * t * (3 - 2 * t);
}

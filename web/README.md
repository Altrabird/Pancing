# Pancing

A physics-driven fishing game on Three.js. Malaysian freshwater species, a real
rod-tension model, a bite state machine that responds to how you present the
bait, and an art pipeline where every texture, sprite and fish mesh is generated
from a seed at boot — there are no asset files in this repository.

```bash
npm install
npm run dev          # http://127.0.0.1:5173
```

Controls: **hold** space / mouse to charge a cast and release to throw ·
**Enter / E** to strike · **W** to reel · **A / D** to adjust the drag ·
**B / I / R / Q / T** for shop, bag, records, quests, travel.

---

## The three systems that matter

### 1. Rod tension — `src/physics/rod.js`

The rod and line are two springs in series between the reel and the fish:

```
reel ──[ drag clutch ]── rod blank ──tip──[ line ]── fish
```

The line is near-linear and reaches its rated breaking force exactly when its
strain reaches the material's rated stretch, which gives a spring constant that
falls with length the way `k = EA/L` does. The rod is deliberately **not**
linear — a real blank has a progressive taper, soft at the tip and stiff at the
butt, modelled as a saturating exponential:

```
e_rod(T) = maxDeflect · (1 − e^(−T / power))
```

That curve is the shock absorber. It is why a soft rod protects light line and a
broomstick snaps it, and it falls out of one line of code.

Both springs carry the same tension and their extensions must sum to the
overshoot between where the fish is and how much line is out. Rather than
integrating a stiff ODE — which explodes with near-zero-stretch braid — the
solver **bisects for the tension directly**. That is unconditionally stable at
any timestep and costs about twenty float operations.

The drag is not a clamp. Above the clutch setting the spool slips and pays out
line, which lengthens the line out, which drops the strain, which drops the
tension back toward the setting. The feedback loop *is* the drag. And the clutch
is deliberately **not** limited to the line's strength — a reel that can out-pull
its line is exactly how anglers break off, so winding the drag up beats the fish
faster and moves you closer to a snap. That is the central risk decision.

Two ways to lose a hooked fish that has not broken you off: let the line go slack
so the hook backs out, or bury the rod and tear the hole open. Both are tracked
as separate wear values.

### 2. Bite detection — `src/physics/bite.js`

A bite is not a coin flip on a timer. It is an attraction budget that fills or
empties every tick according to whether what you are doing matches what the fish
that is actually down there wants. Presentation is scored from four independent
factors, multiplied, so one bad choice can kill a bite outright:

| factor | source |
| --- | --- |
| lure match | the species' own lure table |
| depth match | how close the bait sits to its preferred band |
| action match | whether your retrieve suits its aggression |
| stealth | line visibility against water clarity, plus surface noise |

States run `SEARCHING → INTEREST → NIBBLING → COMMITTED → HOOKED`, with a
`SPOOKED` cooldown. The hookset window is species-dependent — 320 ms for a Toman,
1.4 s for a prawn — and strike quality is how centred in the window you were,
which becomes the hook's starting hold. Striking on a nibble spooks the fish;
striking at nothing costs attraction. The HUD shows all four presentation factors
live, so a dead spot is diagnosable rather than mysterious.

### 3. The fight — `src/game/fish.js`

The fish is an agent, not a health bar. It picks behaviour (`RUN`, `DIVE`,
`THRASH`, `CIRCLE`, `SURGE`, `REST`, `JUMP`, `BEATEN`) weighted by its archetype,
its remaining stamina, how hard you are pulling and whether there is structure
within reach.

Movement is pure force balance against water drag: the fish swims away only
while it out-pulls the line, and is dragged in when the line out-pulls it.
Nothing in it knows about the reel or the clutch — reeling shortens the line,
which raises tension, which flips the sign of the net force. The tug of war is
emergent, which is why a locked drag and a loose drag feel completely different
without either being special-cased.

Stamina drain is measured against **the fish's own strength**, not against the
line's breaking strain. 30 N is a crushing workload for a Tilapia and a gentle
stretch for a Toman, so heavy tackle beats small fish quickly and still cannot
bully a big one. (Scaling by the line instead makes better line tire fish
*slower*, which is backwards — that bug is documented in the source.)

---

## The asset pipeline

There are **no image files**. `src/assets/` synthesises everything at boot from
an integer seed:

- `noise.js` — seeded value noise, tileable FBM, ridged multifractal, domain
  warping, Worley cells, Catmull-Rom splines, palette ramps.
- `textures.js` — water normals, caustics, foam, lake bed + matching normal map,
  bank, tree and reed billboards, star field. All tileable, all palette-driven
  from the location record.
- `fishgen.js` — **one genome, two outputs.** Every species carries an `art`
  block: a body-profile spline, a four-colour palette, a pattern type, fin
  styles, a seed. From it the module derives `bodyRadius(u)`, `patternAt(u,v)`
  and `colourAt(u,v)`, then lofts a 3D mesh that bakes those colours into vertex
  attributes *and* rasterises the same functions into the 2D card portrait. A
  hand-drawn sprite and a hand-modelled mesh drift apart the moment either is
  edited; here the catch card is guaranteed to be a portrait of the thing you
  actually fought.

> **On "AI pipelines":** this is a *generative* pipeline, not calls out to a
> hosted image model. A diffusion service would mean a network dependency,
> per-asset latency and non-determinism, none of which a real-time game wants,
> and no image-generation API was available in this environment. The seam is
> deliberate and single: `generateAll()` in `textures.js` returns a map of named
> canvases, so an implementation that fetches generated images instead of
> synthesising them is a drop-in replacement with no renderer changes.

## The water — `src/render/water.js`

Gerstner waves displace real vertices, with normals derived analytically from
the wave derivatives rather than sampled from a height texture. A mirrored
camera renders a planar reflection; a second pass supplies a refraction buffer
sampled through a normal-distorted UV. Fresnel is Schlick — at grazing angles
water is a mirror, looking straight down it is a window, and getting that term
right does more for realism than anything else in the shader. A ring buffer of
ripple sources injects decaying radial wave packets wherever the lure, a fish or
a splash touches the surface.

The depth term does not come from a depth buffer. It is baked off
`terrainHeight()`, which is built on `spot.depthAt()` — **the same function the
catch table scores against**. One source of truth: the water cannot be deep where
the ground is high, it ends exactly where the ground rises out of it, and the
shallows you can see are the shallows you are fishing.

## Progression — `src/game/state.js`

XP curve, wallet, gear ownership and equipping, per-species record book, eight
quests, three locations with entry fees and level gates. One store, one shape,
one save. Every mutation emits an event, so the UI is a pure subscriber and never
polls. The save is versioned with forward migrations, and unknown ids are dropped
rather than crashing — a fishing game lives or dies on whether last week's record
book survives this week's patch.

---

## Architecture

```
src/
  core/      rng, event bus, fixed-timestep loop, input
  data/      species catch tables, gear stats, locations   ← pure data
  physics/   rod tension solver, bite FSM, cast ballistics
  game/      fight AI, catch resolution, world clock, the fishing loop, state
  assets/    noise, procedural textures, fish mesh + sprite synthesis
  render/    water shader, environment, tackle visuals, scene orchestration
  ui/        HUD, modal panels
```

The simulation is **headless**. `FishingGame` never touches Three.js, the DOM or
the wall clock; the renderer is a pure consumer of telemetry and events. That is
what lets the entire game be tested without a browser.

Simulation runs at a locked 120 Hz accumulator (the tension solver is only
meaningful at a fixed dt); rendering runs free and interpolates.

Adding a fish means adding one record to `data/species.js` — the catch table,
fight AI, bite timing, 3D mesh, card art and record book all pick it up with no
other edits.

---

## Verifying it

Two harnesses, both real:

```bash
npm run sim -- 250 --seed=42 --spot=tasik --gear=starter   # headless balance
npm run smoke                                              # real browser, real WebGL
```

`tools/simulate.mjs` drives the actual game loop with an autopilot angler and
reports the funnel, failure modes, fight statistics, catch composition and
economy pace — plus a solver stability check. `tools/smoketest.py` loads the game
in Chromium with WebGL, plays a full cast → bite → hookset → fight → land cycle
through the public input path, opens every panel, and fails on any console error
or context loss. `tools/probe.py` watches every frame for non-finite values.

Representative results (150 casts each):

| scenario | landed | snaps | hook pulled | avg fight |
| --- | --- | --- | --- | --- |
| starter tackle, careful | 100 % | 0 % | 0 % | 25 s |
| starter tackle, reckless | 89 % | 8.7 % | 2.7 % | 21 s |
| light tackle vs. deep-lake monsters | 89 % | 0.8 % | — | 24 s (max 99 s) |
| matched tackle, careful | 100 % | 0 % | 0 % | 19 s |

Reckless play is about 25 % faster and loses roughly one fish in nine. That gap
is the game.

`npm run shots` re-renders the reference screenshots in `shots/` across times of
day, weather and locations.

## Known limits

- Casting aim is mouse-X only; there is no free camera orbit.
- No audio.
- Fish meshes are rebuilt per species and cached, but a very large record book
  keeps them all resident.
- The smoke test runs on SwiftShader (software GL), so its reported frame rate
  is not indicative of real hardware.

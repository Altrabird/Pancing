/**
 * Export the game's data tables from the JavaScript reference build to JSON.
 *
 * The JS build is the golden reference for the Unity port, and its data lives in
 * ES modules full of comments and derived maps. Hand-transcribing 20 species ×
 * 30 fields into C# would be a typo farm, and the typos would be silent — a fish
 * with the wrong allometry still runs, it just weighs the wrong amount. So we
 * generate instead, and re-generate whenever the tables move.
 *
 *   node shared/tools/export-data.mjs
 *
 * Writes shared/data/*.json and copies them into the Unity project's Resources
 * folder, which is where Unity can load them synchronously on every platform
 * (StreamingAssets would need UnityWebRequest on Android).
 *
 * NOT exportable: `spot.depthAt(u, v)` is a function, not data. It lives as code
 * in both engines — web/src/data/spots.js and unity/…/Sim/Data/SpotShapes.cs —
 * and shared/parity pins the two implementations against each other.
 */

import { writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { SPECIES, RARITY, FIGHT_PROFILES, LURE_MISMATCH } from '../../web/src/data/species.js';
import { GEAR_TABLES, STARTER_KIT } from '../../web/src/data/gear.js';
import { SPOTS, TIME_PHASES, WEATHER } from '../../web/src/data/spots.js';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, '..', '..');
const OUT = join(ROOT, 'shared', 'data');
const UNITY_RES = join(ROOT, 'unity', 'Pancing', 'Assets', 'Pancing', 'Resources');

/** Infinity does not survive JSON. Encode it the same way the save file does. */
const encode = (_k, v) => (v === Infinity ? '__Inf__' : v);

/** Strip the function-valued fields; they are code, not data. See header. */
function plainSpot(s) {
  const { depthAt, ...rest } = s;
  return rest;
}

const files = {
  'species.json': {
    lureMismatch: LURE_MISMATCH,
    rarity: RARITY,
    fightProfiles: FIGHT_PROFILES,
    species: SPECIES,
  },
  'gear.json': {
    starterKit: STARTER_KIT,
    // Flattened with an explicit slot, mirroring how GEAR_BY_ID is built, so the
    // C# side does not have to know that "the fourth table is lures".
    items: Object.entries(GEAR_TABLES).flatMap(([slot, table]) =>
      table.map((item) => ({ ...item, slot }))),
  },
  'spots.json': {
    spots: SPOTS.map(plainSpot),
    timePhases: TIME_PHASES,
    weather: WEATHER,
  },
};

for (const dir of [OUT, UNITY_RES]) {
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true });
}

let totalBytes = 0;
for (const [name, payload] of Object.entries(files)) {
  const json = JSON.stringify(payload, encode, 2) + '\n';
  totalBytes += json.length;
  writeFileSync(join(OUT, name), json);
  // Unity treats *.json in Resources as a TextAsset only if the extension is
  // one it recognises — .json works, and Resources.Load drops the extension.
  writeFileSync(join(UNITY_RES, name), json);
  console.log(`  ${name.padEnd(14)} ${String(json.length).padStart(7)} bytes`);
}

console.log(`\nexported ${Object.keys(files).length} files, ${totalBytes} bytes`);
console.log(`  -> ${OUT}`);
console.log(`  -> ${UNITY_RES}`);

// A quick shape assertion, so a rename in the JS tables fails here loudly rather
// than producing a JSON file the C# loader will silently read as empty.
const problems = [];
if (SPECIES.length < 10) problems.push(`only ${SPECIES.length} species`);
for (const s of SPECIES) {
  if (!s.id || !s.fight || !s.bite || !s.art) problems.push(`species ${s.id}: missing block`);
  if (!FIGHT_PROFILES[s.fight.profile]) problems.push(`species ${s.id}: unknown profile ${s.fight.profile}`);
  if (!RARITY[s.rarity]) problems.push(`species ${s.id}: unknown rarity ${s.rarity}`);
}
for (const spot of SPOTS) {
  for (const id of Object.keys(spot.pool)) {
    if (!SPECIES.some((s) => s.id === id)) problems.push(`spot ${spot.id}: pool references unknown species ${id}`);
  }
}
if (problems.length) {
  console.error('\nDATA PROBLEMS:');
  for (const p of problems) console.error('  - ' + p);
  process.exit(1);
}
console.log('data checks passed');

/**
 * Species catch table.
 *
 * Every field here is data, not code: the catch resolver, the fight AI, the bite
 * FSM and the procedural art pipeline all read from these records. Adding a fish
 * to the game means adding an entry to this array and nothing else.
 *
 *  rarity      tier used for UI + trophy rolls
 *  weight      base weight in the spot's weighted draw (pre-modifier)
 *  length      truncated-normal length distribution, centimetres
 *  allometry   kg = a * L^b  (standard fisheries length-weight relation)
 *  depth       preferred normalised depth band [near-surface 0 .. bottom 1]
 *  times       multiplier by time-of-day phase
 *  weather     multiplier by weather state
 *  lures       multiplier by lure id (absent = LURE_MISMATCH, fish ignores it)
 *  fight       parameters consumed by game/fish.js (the fight AI)
 *  bite        parameters consumed by physics/bite.js (the bite FSM)
 *  art         genome consumed by assets/fishgen.js (sprite + mesh synthesis)
 */

export const LURE_MISMATCH = 0.3;

export const RARITY = {
  junk:      { label: 'Sampah',  color: '#7a7f74', order: 0, trophyBonus: 0.00 },
  common:    { label: 'Biasa',   color: '#b9c4ae', order: 1, trophyBonus: 0.02 },
  uncommon:  { label: 'Jarang',  color: '#6fc48b', order: 2, trophyBonus: 0.04 },
  rare:      { label: 'Sukar',   color: '#5aa9e6', order: 3, trophyBonus: 0.07 },
  epic:      { label: 'Hebat',   color: '#b57ce8', order: 4, trophyBonus: 0.11 },
  legendary: { label: 'Legenda', color: '#f0a63c', order: 5, trophyBonus: 0.18 },
};

export const SPECIES = [
  {
    id: 'tilapia', name: 'Tilapia', latin: 'Oreochromis mossambicus',
    rarity: 'common', weight: 120, minLevel: 1, value: 6, xp: 10,
    length: { min: 11, max: 45, mean: 21, sd: 6.0 },
    allometry: { a: 0.0000205, b: 3.02 },
    depth: [0.10, 0.55],
    times: { dawn: 1.30, day: 1.00, dusk: 1.25, night: 0.45 },
    weather: { clear: 1.0, cloudy: 1.15, rain: 1.30, storm: 0.55 },
    lures: { pellet: 2.1, worm: 1.7, dough: 1.9, spinner: 0.45, frog: 0.2 },
    fight: { profile: 'thrasher', strength: 0.34, stamina: 0.42, aggression: 0.55,
             burst: 0.35, hookHold: 0.72, structureSeek: 0.25, jumpChance: 0.05 },
    bite: { caution: 0.30, window: 0.85, nibbles: [1, 3], speed: 0.75 },
    art: {
      seed: 1101, palette: ['#4c5a48', '#8fa07c', '#dfe0c4', '#2b3128'],
      profile: [0.06, 0.42, 0.52, 0.40, 0.14], depth: 0.46, tail: 'truncate',
      pattern: 'bars', patternAmt: 0.35, dorsal: 0.34, eye: 0.075, gloss: 0.45,
    },
  },
  {
    id: 'keli', name: 'Ikan Keli', latin: 'Clarias batrachus',
    rarity: 'common', weight: 110, minLevel: 1, value: 7, xp: 12,
    length: { min: 18, max: 62, mean: 30, sd: 8.0 },
    allometry: { a: 0.0000041, b: 3.21 },
    depth: [0.62, 1.00],
    times: { dawn: 0.9, day: 0.5, dusk: 1.35, night: 2.10 },
    weather: { clear: 0.85, cloudy: 1.0, rain: 1.55, storm: 1.25 },
    lures: { worm: 2.3, dough: 1.6, pellet: 1.2, shrimp: 2.0, spinner: 0.25 },
    fight: { profile: 'digger', strength: 0.46, stamina: 0.66, aggression: 0.40,
             burst: 0.22, hookHold: 0.80, structureSeek: 0.55, jumpChance: 0.0 },
    bite: { caution: 0.18, window: 1.20, nibbles: [1, 2], speed: 0.55 },
    art: {
      seed: 2202, palette: ['#3a332b', '#6a5c47', '#c9bda0', '#211d18'],
      profile: [0.10, 0.30, 0.28, 0.22, 0.09], depth: 0.30, tail: 'round',
      pattern: 'plain', patternAmt: 0.10, dorsal: 0.55, eye: 0.040, gloss: 0.30,
      barbels: 4,
    },
  },
  {
    id: 'lampam', name: 'Lampam Jawa', latin: 'Barbonymus gonionotus',
    rarity: 'common', weight: 95, minLevel: 1, value: 8, xp: 13,
    length: { min: 14, max: 40, mean: 23, sd: 5.5 },
    allometry: { a: 0.0000230, b: 3.05 },
    depth: [0.20, 0.70],
    times: { dawn: 1.25, day: 1.10, dusk: 1.15, night: 0.55 },
    weather: { clear: 1.10, cloudy: 1.05, rain: 1.10, storm: 0.60 },
    lures: { dough: 2.2, pellet: 1.8, worm: 1.3, fruit: 2.4, spinner: 0.35 },
    fight: { profile: 'runner', strength: 0.40, stamina: 0.52, aggression: 0.45,
             burst: 0.58, hookHold: 0.58, structureSeek: 0.30, jumpChance: 0.08 },
    bite: { caution: 0.52, window: 0.55, nibbles: [2, 4], speed: 0.85 },
    art: {
      seed: 3303, palette: ['#8d9099', '#d7dce2', '#f6f3e6', '#e8b13c'],
      profile: [0.06, 0.48, 0.58, 0.42, 0.13], depth: 0.52, tail: 'forked',
      pattern: 'plain', patternAmt: 0.06, dorsal: 0.40, eye: 0.085, gloss: 0.72,
    },
  },
  {
    id: 'puyu', name: 'Ikan Puyu', latin: 'Anabas testudineus',
    rarity: 'common', weight: 85, minLevel: 1, value: 9, xp: 11,
    length: { min: 8, max: 24, mean: 13, sd: 3.0 },
    allometry: { a: 0.0000260, b: 3.10 },
    depth: [0.05, 0.45],
    times: { dawn: 1.20, day: 1.00, dusk: 1.20, night: 0.80 },
    weather: { clear: 1.0, cloudy: 1.1, rain: 1.45, storm: 0.9 },
    lures: { worm: 2.1, dough: 1.4, shrimp: 1.5, spinner: 0.6 },
    fight: { profile: 'thrasher', strength: 0.22, stamina: 0.35, aggression: 0.70,
             burst: 0.45, hookHold: 0.85, structureSeek: 0.20, jumpChance: 0.02 },
    bite: { caution: 0.22, window: 0.70, nibbles: [1, 3], speed: 0.90 },
    art: {
      seed: 4404, palette: ['#4a4436', '#7d7a5c', '#c2b98e', '#2a271f'],
      profile: [0.08, 0.38, 0.44, 0.36, 0.12], depth: 0.42, tail: 'round',
      pattern: 'mottle', patternAmt: 0.40, dorsal: 0.60, eye: 0.090, gloss: 0.35,
    },
  },
  {
    id: 'haruan', name: 'Haruan', latin: 'Channa striata',
    rarity: 'uncommon', weight: 62, minLevel: 2, value: 16, xp: 26,
    length: { min: 22, max: 85, mean: 38, sd: 10.0 },
    allometry: { a: 0.0000060, b: 3.15 },
    depth: [0.08, 0.50],
    times: { dawn: 1.60, day: 0.90, dusk: 1.55, night: 0.75 },
    weather: { clear: 1.15, cloudy: 1.0, rain: 1.20, storm: 0.70 },
    lures: { frog: 2.6, spinner: 1.8, worm: 1.1, shrimp: 1.4, popper: 2.3 },
    fight: { profile: 'runner', strength: 0.62, stamina: 0.60, aggression: 0.85,
             burst: 0.72, hookHold: 0.62, structureSeek: 0.70, jumpChance: 0.22 },
    bite: { caution: 0.20, window: 0.45, nibbles: [1, 1], speed: 1.10 },
    art: {
      seed: 5505, palette: ['#33382b', '#5f6a4a', '#b8b58c', '#1b1e17'],
      profile: [0.12, 0.30, 0.31, 0.27, 0.10], depth: 0.31, tail: 'round',
      pattern: 'chevron', patternAmt: 0.55, dorsal: 0.72, eye: 0.055, gloss: 0.40,
    },
  },
  {
    id: 'sebarau', name: 'Sebarau', latin: 'Hampala macrolepidota',
    rarity: 'uncommon', weight: 55, minLevel: 3, value: 20, xp: 32,
    length: { min: 20, max: 70, mean: 34, sd: 9.0 },
    allometry: { a: 0.0000115, b: 3.06 },
    depth: [0.15, 0.65],
    times: { dawn: 1.70, day: 1.05, dusk: 1.65, night: 0.35 },
    weather: { clear: 1.25, cloudy: 1.10, rain: 0.85, storm: 0.50 },
    lures: { spinner: 2.7, popper: 2.2, minnow: 2.5, worm: 0.8, frog: 1.3 },
    fight: { profile: 'jumper', strength: 0.66, stamina: 0.55, aggression: 0.90,
             burst: 0.88, hookHold: 0.50, structureSeek: 0.45, jumpChance: 0.55 },
    bite: { caution: 0.15, window: 0.35, nibbles: [1, 1], speed: 1.35 },
    art: {
      seed: 6606, palette: ['#5c6470', '#aab4bd', '#eef1f3', '#c8452f'],
      profile: [0.07, 0.40, 0.46, 0.34, 0.11], depth: 0.42, tail: 'forked',
      pattern: 'band', patternAmt: 0.65, dorsal: 0.38, eye: 0.090, gloss: 0.85,
    },
  },
  {
    id: 'baung', name: 'Baung', latin: 'Hemibagrus nemurus',
    rarity: 'uncommon', weight: 48, minLevel: 3, value: 22, xp: 30,
    length: { min: 20, max: 72, mean: 33, sd: 8.5 },
    allometry: { a: 0.0000058, b: 3.18 },
    depth: [0.70, 1.00],
    times: { dawn: 1.0, day: 0.45, dusk: 1.50, night: 2.30 },
    weather: { clear: 0.85, cloudy: 1.05, rain: 1.60, storm: 1.35 },
    lures: { shrimp: 2.6, worm: 2.0, dough: 1.2, minnow: 1.5 },
    fight: { profile: 'digger', strength: 0.58, stamina: 0.78, aggression: 0.50,
             burst: 0.30, hookHold: 0.75, structureSeek: 0.72, jumpChance: 0.0 },
    bite: { caution: 0.25, window: 0.95, nibbles: [1, 2], speed: 0.60 },
    art: {
      seed: 7707, palette: ['#4a3f31', '#8a7355', '#e2d3b0', '#26201a'],
      profile: [0.09, 0.32, 0.30, 0.21, 0.08], depth: 0.32, tail: 'forked',
      pattern: 'plain', patternAmt: 0.12, dorsal: 0.50, eye: 0.050, gloss: 0.42,
      barbels: 4,
    },
  },
  {
    id: 'jelawat', name: 'Jelawat', latin: 'Leptobarbus hoevenii',
    rarity: 'rare', weight: 26, minLevel: 5, value: 38, xp: 55,
    length: { min: 30, max: 95, mean: 46, sd: 12.0 },
    allometry: { a: 0.0000130, b: 3.04 },
    depth: [0.25, 0.75],
    times: { dawn: 1.40, day: 1.05, dusk: 1.35, night: 0.60 },
    weather: { clear: 1.05, cloudy: 1.15, rain: 1.25, storm: 0.65 },
    lures: { fruit: 2.8, dough: 2.0, pellet: 1.7, worm: 1.0 },
    fight: { profile: 'runner', strength: 0.74, stamina: 0.72, aggression: 0.60,
             burst: 0.80, hookHold: 0.55, structureSeek: 0.40, jumpChance: 0.12 },
    bite: { caution: 0.60, window: 0.50, nibbles: [2, 4], speed: 0.80 },
    art: {
      seed: 8808, palette: ['#6d7a72', '#bfc9bd', '#f2efe0', '#d8663f'],
      profile: [0.07, 0.42, 0.50, 0.36, 0.12], depth: 0.46, tail: 'forked',
      pattern: 'stripe', patternAmt: 0.30, dorsal: 0.36, eye: 0.080, gloss: 0.80,
    },
  },
  {
    id: 'patin', name: 'Patin', latin: 'Pangasius nasutus',
    rarity: 'rare', weight: 22, minLevel: 6, value: 42, xp: 62,
    length: { min: 35, max: 110, mean: 55, sd: 14.0 },
    allometry: { a: 0.0000075, b: 3.12 },
    depth: [0.55, 1.00],
    times: { dawn: 1.10, day: 0.80, dusk: 1.30, night: 1.60 },
    weather: { clear: 0.95, cloudy: 1.10, rain: 1.40, storm: 1.10 },
    lures: { dough: 2.5, pellet: 2.2, shrimp: 1.8, worm: 1.3 },
    fight: { profile: 'digger', strength: 0.82, stamina: 0.88, aggression: 0.45,
             burst: 0.35, hookHold: 0.68, structureSeek: 0.60, jumpChance: 0.0 },
    bite: { caution: 0.45, window: 0.80, nibbles: [1, 3], speed: 0.55 },
    art: {
      seed: 9909, palette: ['#5b6470', '#a9b3bd', '#eef2f5', '#2f353c'],
      profile: [0.10, 0.36, 0.34, 0.22, 0.09], depth: 0.34, tail: 'forked',
      pattern: 'plain', patternAmt: 0.05, dorsal: 0.55, eye: 0.060, gloss: 0.88,
      barbels: 2,
    },
  },
  {
    id: 'toman', name: 'Toman', latin: 'Channa micropeltes',
    rarity: 'epic', weight: 11, minLevel: 8, value: 70, xp: 120,
    length: { min: 45, max: 130, mean: 68, sd: 16.0 },
    allometry: { a: 0.0000068, b: 3.14 },
    depth: [0.10, 0.60],
    times: { dawn: 1.75, day: 1.00, dusk: 1.70, night: 0.50 },
    weather: { clear: 1.20, cloudy: 1.05, rain: 1.10, storm: 0.60 },
    lures: { frog: 3.0, popper: 2.8, minnow: 2.4, spinner: 1.7 },
    fight: { profile: 'runner', strength: 0.92, stamina: 0.82, aggression: 0.95,
             burst: 0.90, hookHold: 0.58, structureSeek: 0.78, jumpChance: 0.35 },
    bite: { caution: 0.12, window: 0.32, nibbles: [1, 1], speed: 1.50 },
    art: {
      seed: 1212, palette: ['#2f3a33', '#54705a', '#c3c08f', '#c24a2c'],
      profile: [0.13, 0.32, 0.33, 0.28, 0.11], depth: 0.33, tail: 'round',
      pattern: 'band', patternAmt: 0.70, dorsal: 0.75, eye: 0.055, gloss: 0.55,
    },
  },
  {
    id: 'belida', name: 'Belida', latin: 'Chitala lopis',
    rarity: 'epic', weight: 9, minLevel: 9, value: 82, xp: 140,
    length: { min: 40, max: 120, mean: 62, sd: 15.0 },
    allometry: { a: 0.0000090, b: 3.02 },
    depth: [0.45, 0.95],
    times: { dawn: 1.30, day: 0.60, dusk: 1.60, night: 1.90 },
    weather: { clear: 0.90, cloudy: 1.10, rain: 1.35, storm: 1.05 },
    lures: { shrimp: 2.9, minnow: 2.5, worm: 1.4, frog: 1.6 },
    fight: { profile: 'thrasher', strength: 0.86, stamina: 0.70, aggression: 0.75,
             burst: 0.65, hookHold: 0.45, structureSeek: 0.55, jumpChance: 0.10 },
    bite: { caution: 0.55, window: 0.42, nibbles: [1, 2], speed: 0.95 },
    art: {
      seed: 1313, palette: ['#3d4550', '#7d879a', '#dfe6ee', '#1c2027'],
      profile: [0.05, 0.55, 0.62, 0.30, 0.06], depth: 0.60, tail: 'lunate',
      pattern: 'spots', patternAmt: 0.50, dorsal: 0.20, eye: 0.070, gloss: 0.90,
    },
  },
  {
    id: 'kelah', name: 'Kelah Merah', latin: 'Tor tambroides',
    rarity: 'legendary', weight: 3.2, minLevel: 12, value: 190, xp: 400,
    length: { min: 50, max: 145, mean: 78, sd: 17.0 },
    allometry: { a: 0.0000140, b: 3.08 },
    depth: [0.35, 0.90],
    times: { dawn: 2.00, day: 0.70, dusk: 1.85, night: 0.55 },
    weather: { clear: 1.10, cloudy: 1.20, rain: 1.50, storm: 0.70 },
    lures: { fruit: 3.2, dough: 2.0, shrimp: 1.8, worm: 1.2 },
    fight: { profile: 'runner', strength: 1.00, stamina: 0.95, aggression: 0.80,
             burst: 0.95, hookHold: 0.48, structureSeek: 0.85, jumpChance: 0.18 },
    bite: { caution: 0.80, window: 0.38, nibbles: [3, 5], speed: 0.60 },
    art: {
      seed: 1414, palette: ['#7a3a2e', '#c96b45', '#f6d9a8', '#3a1c16'],
      profile: [0.07, 0.44, 0.53, 0.38, 0.12], depth: 0.48, tail: 'forked',
      pattern: 'scales', patternAmt: 0.60, dorsal: 0.38, eye: 0.080, gloss: 0.95,
    },
  },
  {
    id: 'udang_galah', name: 'Udang Galah', latin: 'Macrobrachium rosenbergii',
    rarity: 'rare', weight: 24, minLevel: 4, value: 45, xp: 48,
    length: { min: 12, max: 34, mean: 19, sd: 4.0 },
    allometry: { a: 0.0000180, b: 2.95 },
    depth: [0.75, 1.00],
    times: { dawn: 0.8, day: 0.4, dusk: 1.4, night: 2.4 },
    weather: { clear: 0.9, cloudy: 1.0, rain: 1.4, storm: 1.1 },
    lures: { worm: 2.4, shrimp: 1.2, dough: 1.6, pellet: 1.4 },
    fight: { profile: 'digger', strength: 0.28, stamina: 0.40, aggression: 0.35,
             burst: 0.50, hookHold: 0.30, structureSeek: 0.65, jumpChance: 0.0 },
    bite: { caution: 0.75, window: 1.40, nibbles: [3, 6], speed: 0.45 },
    art: {
      seed: 1515, palette: ['#3f5670', '#6f93b5', '#cfe0ec', '#1d2833'],
      profile: [0.10, 0.26, 0.30, 0.24, 0.14], depth: 0.28, tail: 'fan',
      pattern: 'segments', patternAmt: 0.70, dorsal: 0.10, eye: 0.100, gloss: 0.85,
    },
  },

  /* --- junk: keeps the catch table honest and funds the early economy --- */
  {
    id: 'boot', name: 'Kasut Buruk', latin: 'Calceus abandonatus',
    rarity: 'junk', weight: 30, minLevel: 1, value: 2, xp: 3,
    length: { min: 24, max: 32, mean: 28, sd: 2 },
    allometry: { a: 0.0000400, b: 2.60 },
    depth: [0.80, 1.00],
    times: { dawn: 1, day: 1, dusk: 1, night: 1 },
    weather: { clear: 1, cloudy: 1, rain: 1, storm: 1.4 },
    lures: null,
    fight: { profile: 'deadweight', strength: 0.18, stamina: 0.30, aggression: 0.0,
             burst: 0.0, hookHold: 0.95, structureSeek: 0.0, jumpChance: 0.0 },
    bite: { caution: 0.0, window: 2.0, nibbles: [1, 1], speed: 0.4 },
    art: {
      seed: 2001, palette: ['#3a332e', '#5d5149', '#8a7a6c', '#211d1a'],
      profile: [0.20, 0.30, 0.26, 0.34, 0.30], depth: 0.32, tail: 'truncate',
      pattern: 'plain', patternAmt: 0.0, dorsal: 0.0, eye: 0.0, gloss: 0.25,
    },
  },
  {
    id: 'tin', name: 'Tin Karat', latin: 'Ferrum oxidatum',
    rarity: 'junk', weight: 26, minLevel: 1, value: 1, xp: 2,
    length: { min: 10, max: 16, mean: 13, sd: 1.5 },
    allometry: { a: 0.0000300, b: 2.70 },
    depth: [0.75, 1.00],
    times: { dawn: 1, day: 1, dusk: 1, night: 1 },
    weather: { clear: 1, cloudy: 1, rain: 1, storm: 1.3 },
    lures: null,
    fight: { profile: 'deadweight', strength: 0.10, stamina: 0.20, aggression: 0.0,
             burst: 0.0, hookHold: 0.95, structureSeek: 0.0, jumpChance: 0.0 },
    bite: { caution: 0.0, window: 2.0, nibbles: [1, 1], speed: 0.4 },
    art: {
      seed: 2002, palette: ['#5a4b3a', '#8d7a5f', '#b8a184', '#2c2419'],
      profile: [0.30, 0.34, 0.34, 0.34, 0.30], depth: 0.34, tail: 'truncate',
      pattern: 'mottle', patternAmt: 0.6, dorsal: 0.0, eye: 0.0, gloss: 0.55,
    },
  },
  {
    id: 'plastik', name: 'Beg Plastik', latin: 'Polyethylena flotans',
    rarity: 'junk', weight: 22, minLevel: 1, value: 1, xp: 4,
    length: { min: 20, max: 40, mean: 28, sd: 5 },
    allometry: { a: 0.0000020, b: 2.20 },
    depth: [0.00, 0.45],
    times: { dawn: 1, day: 1, dusk: 1, night: 1 },
    weather: { clear: 1, cloudy: 1, rain: 1.2, storm: 1.6 },
    lures: null,
    fight: { profile: 'deadweight', strength: 0.08, stamina: 0.15, aggression: 0.0,
             burst: 0.0, hookHold: 0.90, structureSeek: 0.0, jumpChance: 0.0 },
    bite: { caution: 0.0, window: 2.0, nibbles: [1, 1], speed: 0.5 },
    art: {
      seed: 2003, palette: ['#8f9aa2', '#c4ced6', '#eef3f6', '#5c666e'],
      profile: [0.24, 0.40, 0.44, 0.36, 0.20], depth: 0.40, tail: 'round',
      pattern: 'plain', patternAmt: 0.0, dorsal: 0.0, eye: 0.0, gloss: 0.95,
    },
  },
  {
    id: 'ranting', name: 'Ranting Kayu', latin: 'Ramus submersus',
    rarity: 'junk', weight: 24, minLevel: 1, value: 1, xp: 2,
    length: { min: 25, max: 70, mean: 42, sd: 9 },
    allometry: { a: 0.0000060, b: 2.50 },
    depth: [0.60, 1.00],
    times: { dawn: 1, day: 1, dusk: 1, night: 1 },
    weather: { clear: 1, cloudy: 1, rain: 1.3, storm: 1.7 },
    lures: null,
    fight: { profile: 'deadweight', strength: 0.14, stamina: 0.25, aggression: 0.0,
             burst: 0.0, hookHold: 0.92, structureSeek: 0.0, jumpChance: 0.0 },
    bite: { caution: 0.0, window: 2.0, nibbles: [1, 1], speed: 0.4 },
    art: {
      seed: 2004, palette: ['#42352a', '#6d5a44', '#9c8768', '#241c15'],
      profile: [0.10, 0.13, 0.12, 0.11, 0.08], depth: 0.13, tail: 'truncate',
      pattern: 'stripe', patternAmt: 0.4, dorsal: 0.0, eye: 0.0, gloss: 0.20,
    },
  },
];

export const SPECIES_BY_ID = Object.fromEntries(SPECIES.map((s) => [s.id, s]));

/**
 * Behavioural archetypes. The fight AI blends these biases with the individual
 * fish's rolled stats, so two Toman never fight identically but both fight like
 * a Toman.
 */
export const FIGHT_PROFILES = {
  /** Long horizontal runs, sudden acceleration. Punishes a locked drag. */
  runner:     { runBias: 1.35, diveBias: 0.55, thrashBias: 0.60, circleBias: 0.90,
                surgeRate: 1.25, restRate: 0.85, hookWear: 0.85 },
  /** Heads for the bottom and sits there. Punishes impatience. */
  digger:     { runBias: 0.50, diveBias: 1.60, thrashBias: 0.55, circleBias: 1.10,
                surgeRate: 0.75, restRate: 1.20, hookWear: 0.70 },
  /** Violent head-shakes. Shreds hook hold if you keep the line piano-tight. */
  thrasher:   { runBias: 0.75, diveBias: 0.80, thrashBias: 1.75, circleBias: 0.85,
                surgeRate: 1.10, restRate: 0.95, hookWear: 1.55 },
  /** Airborne. A jump against a tight line throws the hook — bow to the fish. */
  jumper:     { runBias: 1.15, diveBias: 0.45, thrashBias: 1.20, circleBias: 0.75,
                surgeRate: 1.40, restRate: 0.80, hookWear: 1.30 },
  /** Junk. No agency, just mass and drag. */
  deadweight: { runBias: 0.0, diveBias: 0.30, thrashBias: 0.0, circleBias: 0.0,
                surgeRate: 0.0, restRate: 2.00, hookWear: 0.10 },
};

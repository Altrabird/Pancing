/**
 * Player state: progression, wallet, inventory, records, quests, save/load.
 *
 * One store, one shape, one save file. Every mutation goes through a method
 * that emits an event, so the UI is a pure subscriber and never polls. The save
 * is versioned with forward migrations, because a fishing game lives or dies on
 * whether last week's record book survives this week's patch.
 */

import { EV } from '../core/events.js';
import { GEAR_BY_ID, GEAR_TABLES, STARTER_KIT, LURES } from '../data/gear.js';
import { SPECIES_BY_ID } from '../data/species.js';
import { SPOTS, SPOTS_BY_ID } from '../data/spots.js';
import { clamp01 } from '../core/loop.js';

export const SAVE_KEY = 'pancing.save.v1';
export const SAVE_VERSION = 3;

/** Level curve: gentle at first, then a steady grind that never walls. */
export function xpForLevel(level) {
  return Math.round(60 * Math.pow(level, 1.62));
}

export function totalXpForLevel(level) {
  let sum = 0;
  for (let i = 1; i < level; i++) sum += xpForLevel(i);
  return sum;
}

export const QUESTS = [
  { id: 'first_fish', name: 'Tarikan Pertama', desc: 'Daratkan ikan pertama anda.',
    reward: { money: 50, xp: 30 }, check: (s) => s.stats.landed >= 1 },
  { id: 'five_species', name: 'Pengumpul', desc: 'Daratkan 5 spesies berbeza.',
    reward: { money: 200, xp: 120 }, check: (s) => Object.keys(s.records).length >= 5 },
  { id: 'kilo_club', name: 'Kelab Sekilo', desc: 'Daratkan ikan melebihi 1.0 kg.',
    reward: { money: 150, xp: 90 }, check: (s) => s.stats.heaviestKg >= 1.0 },
  { id: 'no_snap', name: 'Tangan Halus', desc: 'Daratkan 10 ikan tanpa tali putus.',
    reward: { money: 300, xp: 180 }, check: (s) => s.stats.landedStreak >= 10 },
  { id: 'night_owl', name: 'Burung Hantu', desc: 'Daratkan 5 ikan pada waktu malam.',
    reward: { money: 250, xp: 150 }, check: (s) => (s.stats.byPhase.night ?? 0) >= 5 },
  { id: 'rare_hunter', name: 'Pemburu Sukar', desc: 'Daratkan satu ikan gred Sukar atau lebih.',
    reward: { money: 400, xp: 260 }, check: (s) => s.stats.bestRarityOrder >= 3 },
  { id: 'trophy', name: 'Piala', desc: 'Daratkan seekor ikan trofi.',
    reward: { money: 600, xp: 400 }, check: (s) => s.stats.trophies >= 1 },
  { id: 'legend', name: 'Legenda Sungai', desc: 'Daratkan Kelah Merah.',
    reward: { money: 3000, xp: 1500 }, check: (s) => !!s.records.kelah },
];

function freshState() {
  return {
    version: SAVE_VERSION,
    createdAt: Date.now(),
    level: 1,
    xp: 0,
    money: 120,
    spot: 'kolam',
    equipped: { ...STARTER_KIT },
    owned: {
      rod: [STARTER_KIT.rod],
      reel: [STARTER_KIT.reel],
      line: [STARTER_KIT.line],
      lure: [STARTER_KIT.lure],
    },
    /** Consumable bait counts. Non-consumables are absent from this map. */
    stock: { worm: Infinity },
    unlockedSpots: ['kolam'],
    /** Best catch per species: { lengthCm, massKg, at, trophy }. */
    records: {},
    quests: {},
    stats: {
      casts: 0, bites: 0, hooked: 0, landed: 0, lost: 0, snaps: 0,
      spooked: 0, missed: 0, junk: 0, trophies: 0,
      landedStreak: 0, bestStreak: 0,
      heaviestKg: 0, longestCm: 0, bestRarityOrder: 0,
      totalMassKg: 0, totalEarned: 0, playSeconds: 0,
      byPhase: {}, bySpecies: {}, bySpot: {},
    },
    settings: { sound: true, quality: 'high', metric: true },
  };
}

export class PlayerState {
  constructor(bus) {
    this.bus = bus;
    this.data = freshState();
  }

  /* --- derived ------------------------------------------------------------ */

  get level() { return this.data.level; }
  get money() { return this.data.money; }
  get spot() { return SPOTS_BY_ID[this.data.spot] ?? SPOTS[0]; }

  /** Resolved gear records for the physics layer. */
  gear() {
    const e = this.data.equipped;
    return {
      rod: GEAR_BY_ID[e.rod], reel: GEAR_BY_ID[e.reel],
      line: GEAR_BY_ID[e.line], lure: GEAR_BY_ID[e.lure],
    };
  }

  xpProgress() {
    const need = xpForLevel(this.data.level);
    return { current: this.data.xp, need, pct: clamp01(this.data.xp / need) };
  }

  /** Luck rises slowly with level; it only ever widens the good tail. */
  luck() { return clamp01((this.data.level - 1) * 0.028); }

  owns(id) {
    const item = GEAR_BY_ID[id];
    return !!item && this.data.owned[item.slot]?.includes(id);
  }

  stockOf(id) {
    const item = GEAR_BY_ID[id];
    if (!item || !item.consumable) return Infinity;
    return this.data.stock[id] ?? 0;
  }

  /* --- mutations ---------------------------------------------------------- */

  addMoney(amount, reason = '') {
    this.data.money = Math.max(0, this.data.money + amount);
    if (amount > 0) this.data.stats.totalEarned += amount;
    this.bus.emit(EV.MONEY, { money: this.data.money, delta: amount, reason });
    return this.data.money;
  }

  addXp(amount) {
    this.data.xp += amount;
    this.bus.emit(EV.XP_GAIN, { amount, ...this.xpProgress() });
    let leveled = 0;
    while (this.data.xp >= xpForLevel(this.data.level)) {
      this.data.xp -= xpForLevel(this.data.level);
      this.data.level++;
      leveled++;
      this.bus.emit(EV.LEVEL_UP, { level: this.data.level, unlocked: this.newlyUnlocked() });
    }
    if (leveled) this.checkSpotUnlocks();
    return leveled;
  }

  /** Gear that just became purchasable at the current level. */
  newlyUnlocked() {
    const out = [];
    for (const table of Object.values(GEAR_TABLES)) {
      for (const item of table) {
        if (item.level === this.data.level) out.push(item);
      }
    }
    return out;
  }

  checkSpotUnlocks() {
    for (const s of SPOTS) {
      if (s.level <= this.data.level && !this.data.unlockedSpots.includes(s.id)) {
        this.data.unlockedSpots.push(s.id);
        this.bus.emit(EV.UNLOCK, { kind: 'spot', spot: s });
      }
    }
  }

  buy(id, qty = 1) {
    const item = GEAR_BY_ID[id];
    if (!item) return { ok: false, reason: 'unknown' };
    if (item.level > this.data.level) return { ok: false, reason: 'level', need: item.level };

    const isRestock = item.consumable && this.owns(id);
    const cost = item.price * (isRestock ? qty : 1);
    if (cost > this.data.money) return { ok: false, reason: 'money', need: cost };

    this.addMoney(-cost, `buy:${id}`);
    if (!this.owns(id)) this.data.owned[item.slot].push(id);
    if (item.consumable) {
      const current = this.data.stock[id];
      const add = item.stock === Infinity ? Infinity : item.stock * qty;
      this.data.stock[id] = current === Infinity || add === Infinity
        ? Infinity
        : (current ?? 0) + add;
    }
    this.bus.emit(EV.GEAR_BUY, { item, cost, qty });
    return { ok: true, item, cost };
  }

  equip(id) {
    const item = GEAR_BY_ID[id];
    if (!item || !this.owns(id)) return false;
    if (item.consumable && this.stockOf(id) <= 0) return false;
    this.data.equipped[item.slot] = id;
    this.bus.emit(EV.GEAR_EQUIP, { slot: item.slot, item });
    return true;
  }

  /** Bait is spent per cast, not per catch. Falls back to worms when empty. */
  consumeBait() {
    const id = this.data.equipped.lure;
    const item = GEAR_BY_ID[id];
    if (!item?.consumable) return true;
    const have = this.data.stock[id];
    if (have === Infinity) return true;
    if (!have || have <= 0) {
      this.data.equipped.lure = 'worm';
      this.bus.emit(EV.LURE_OUT, { item });
      return false;
    }
    this.data.stock[id] = have - 1;
    return true;
  }

  travel(spotId) {
    const spot = SPOTS_BY_ID[spotId];
    if (!spot) return { ok: false, reason: 'unknown' };
    if (!this.data.unlockedSpots.includes(spotId)) return { ok: false, reason: 'locked', need: spot.level };
    if (spot.entryFee > this.data.money) return { ok: false, reason: 'money', need: spot.entryFee };
    if (spot.entryFee) this.addMoney(-spot.entryFee, `entry:${spotId}`);
    this.data.spot = spotId;
    this.bus.emit(EV.SPOT_CHANGE, { spot });
    return { ok: true, spot };
  }

  /* --- catch recording ---------------------------------------------------- */

  /**
   * Record a landed fish. Returns the reward summary so the catch card can be
   * built from one object.
   */
  recordCatch(fish, { value, xp, phase, keep = true }) {
    const s = this.data.stats;
    const sp = fish.species;

    s.landed++;
    s.landedStreak++;
    s.bestStreak = Math.max(s.bestStreak, s.landedStreak);
    s.totalMassKg = Math.round((s.totalMassKg + fish.massKg) * 1000) / 1000;
    s.bySpecies[sp.id] = (s.bySpecies[sp.id] ?? 0) + 1;
    s.bySpot[this.data.spot] = (s.bySpot[this.data.spot] ?? 0) + 1;
    if (phase) s.byPhase[phase] = (s.byPhase[phase] ?? 0) + 1;
    if (sp.rarity === 'junk') s.junk++;
    if (fish.trophy) s.trophies++;
    s.heaviestKg = Math.max(s.heaviestKg, fish.massKg);
    s.longestCm = Math.max(s.longestCm, fish.lengthCm);

    const rarityOrder = { junk: 0, common: 1, uncommon: 2, rare: 3, epic: 4, legendary: 5 };
    s.bestRarityOrder = Math.max(s.bestRarityOrder, rarityOrder[sp.rarity] ?? 0);

    // Record book, keyed by species and beaten on length.
    const prev = this.data.records[sp.id];
    const isRecord = !prev || fish.lengthCm > prev.lengthCm;
    if (isRecord) {
      this.data.records[sp.id] = {
        lengthCm: fish.lengthCm, massKg: fish.massKg, trophy: fish.trophy,
        at: Date.now(), spot: this.data.spot,
      };
      this.bus.emit(EV.RECORD, { species: sp, fish, previous: prev });
    }

    if (keep && value) this.addMoney(value, `catch:${sp.id}`);
    const levels = this.addXp(xp);
    const questRewards = this.checkQuests();

    return { isRecord, levels, questRewards, value, xp };
  }

  registerLoss(kind) {
    const s = this.data.stats;
    s.landedStreak = 0;
    if (kind === 'snap') s.snaps++;
    else if (kind === 'spooked') s.spooked++;
    else if (kind === 'missed') s.missed++;
    else s.lost++;
  }

  checkQuests() {
    const done = [];
    for (const q of QUESTS) {
      if (this.data.quests[q.id]) continue;
      if (q.check(this.data)) {
        this.data.quests[q.id] = Date.now();
        if (q.reward.money) this.addMoney(q.reward.money, `quest:${q.id}`);
        if (q.reward.xp) this.addXp(q.reward.xp);
        this.bus.emit(EV.QUEST_DONE, { quest: q });
        done.push(q);
      }
    }
    return done;
  }

  questProgress() {
    return QUESTS.map((q) => ({
      ...q, done: !!this.data.quests[q.id], at: this.data.quests[q.id] ?? null,
    }));
  }

  recordBook() {
    return Object.entries(this.data.records)
      .map(([id, rec]) => ({ id, species: SPECIES_BY_ID[id], ...rec }))
      .filter((r) => r.species)
      .sort((a, b) => (b.species.value * b.massKg) - (a.species.value * a.massKg));
  }

  /* --- persistence -------------------------------------------------------- */

  save(storage = globalThis.localStorage) {
    if (!storage) return false;
    try {
      // Infinity does not survive JSON; encode it explicitly.
      const payload = JSON.stringify(this.data, (k, v) =>
        v === Infinity ? '__Inf__' : v);
      storage.setItem(SAVE_KEY, payload);
      this.bus.emit(EV.SAVE, { at: Date.now() });
      return true;
    } catch (err) {
      console.warn('[state] save failed', err);
      return false;
    }
  }

  load(storage = globalThis.localStorage) {
    if (!storage) return false;
    try {
      const raw = storage.getItem(SAVE_KEY);
      if (!raw) return false;
      const parsed = JSON.parse(raw, (k, v) => v === '__Inf__' ? Infinity : v);
      this.data = migrate(parsed);
      return true;
    } catch (err) {
      console.warn('[state] load failed, starting fresh', err);
      this.data = freshState();
      return false;
    }
  }

  reset() { this.data = freshState(); }

  wipe(storage = globalThis.localStorage) {
    this.reset();
    try { storage?.removeItem(SAVE_KEY); } catch { /* private mode */ }
  }
}

/**
 * Forward migrations. Each step upgrades one version, so a save from any
 * released version reaches the current shape by replaying the chain.
 */
export function migrate(save) {
  const base = freshState();
  let s = { ...base, ...save };
  s.stats = { ...base.stats, ...(save.stats ?? {}) };
  s.stats.byPhase = { ...(save.stats?.byPhase ?? {}) };
  s.stats.bySpecies = { ...(save.stats?.bySpecies ?? {}) };
  s.stats.bySpot = { ...(save.stats?.bySpot ?? {}) };
  s.owned = { ...base.owned, ...(save.owned ?? {}) };
  s.equipped = { ...base.equipped, ...(save.equipped ?? {}) };
  s.settings = { ...base.settings, ...(save.settings ?? {}) };

  const v = save.version ?? 1;

  if (v < 2) {
    // v2 added the quest log and the per-spot stat breakdown.
    s.quests = s.quests ?? {};
    s.stats.bySpot = s.stats.bySpot ?? {};
  }
  if (v < 3) {
    // v3 split consumable stock out of `owned` and added spot unlocks.
    s.stock = s.stock ?? { worm: Infinity };
    s.unlockedSpots = s.unlockedSpots ?? ['kolam'];
    for (const l of LURES) {
      if (!l.consumable) continue;
      if (s.owned.lure?.includes(l.id) && s.stock[l.id] == null) {
        s.stock[l.id] = l.stock === Infinity ? Infinity : l.stock;
      }
    }
  }

  // Drop anything that no longer exists in the data tables rather than crashing
  // on a renamed id.
  for (const slot of Object.keys(s.owned)) {
    s.owned[slot] = (s.owned[slot] ?? []).filter((id) => GEAR_BY_ID[id]);
    if (!s.owned[slot].length) s.owned[slot] = [STARTER_KIT[slot]];
  }
  for (const [slot, id] of Object.entries(s.equipped)) {
    if (!GEAR_BY_ID[id] || !s.owned[slot].includes(id)) s.equipped[slot] = STARTER_KIT[slot];
  }
  for (const id of Object.keys(s.records)) {
    if (!SPECIES_BY_ID[id]) delete s.records[id];
  }
  s.unlockedSpots = (s.unlockedSpots ?? ['kolam']).filter((id) => SPOTS_BY_ID[id]);
  if (!s.unlockedSpots.length) s.unlockedSpots = ['kolam'];
  if (!SPOTS_BY_ID[s.spot] || !s.unlockedSpots.includes(s.spot)) s.spot = s.unlockedSpots[0];

  s.version = SAVE_VERSION;
  return s;
}

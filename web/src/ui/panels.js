/**
 * Modal panels: shop, tackle bag, record book, quests and travel.
 *
 * Every panel is rendered from the data tables and the player state, so adding
 * a rod to data/gear.js makes it appear in the shop with no UI work at all.
 */

import { GEAR_TABLES, GEAR_BY_ID } from '../data/gear.js';
import { SPOTS } from '../data/spots.js';
import { SPECIES, SPECIES_BY_ID, RARITY } from '../data/species.js';
import { spriteFor } from '../assets/fishgen.js';
import { odds } from '../game/catchtable.js';
import { EV } from '../core/events.js';

const SLOT_LABELS = { rod: 'Joran', reel: 'Reel', line: 'Tali', lure: 'Umpan' };

export class Panels {
  constructor(root, { bus, state, getContext }) {
    this.bus = bus;
    this.state = state;
    this.getContext = getContext;   // () => { world, gear } for the odds panel
    this.open = null;

    this.overlay = document.createElement('div');
    this.overlay.className = 'panel-overlay hidden';
    this.overlay.setAttribute('data-ui', '');
    this.overlay.innerHTML = `
      <div class="panel">
        <div class="panel-head">
          <div class="panel-tabs">
            <button data-tab="shop">Kedai</button>
            <button data-tab="bag">Beg</button>
            <button data-tab="book">Rekod</button>
            <button data-tab="quests">Misi</button>
            <button data-tab="travel">Jelajah</button>
          </div>
          <button class="panel-close">&times;</button>
        </div>
        <div class="panel-body"></div>
      </div>`;
    root.appendChild(this.overlay);

    this.body = this.overlay.querySelector('.panel-body');
    this.overlay.querySelector('.panel-close').addEventListener('click', () => this.close());
    this.overlay.addEventListener('click', (e) => {
      if (e.target === this.overlay) this.close();
    });
    this.overlay.querySelectorAll('[data-tab]').forEach((b) => {
      b.addEventListener('click', () => this.show(b.dataset.tab));
    });

    // Any state change while a panel is open should redraw it.
    for (const ev of [EV.GEAR_BUY, EV.GEAR_EQUIP, EV.MONEY, EV.SPOT_CHANGE, EV.LEVEL_UP]) {
      bus.on(ev, () => { if (this.open) this.render(); });
    }
  }

  show(tab) {
    this.open = tab;
    this.overlay.classList.remove('hidden');
    this.overlay.querySelectorAll('[data-tab]').forEach((b) => {
      b.classList.toggle('active', b.dataset.tab === tab);
    });
    this.render();
  }

  toggle(tab) {
    if (this.open === tab) this.close();
    else this.show(tab);
  }

  close() {
    this.open = null;
    this.overlay.classList.add('hidden');
  }

  get isOpen() { return this.open !== null; }

  render() {
    switch (this.open) {
      case 'shop': this.body.innerHTML = this._shop(); this._bindShop(); break;
      case 'bag': this.body.innerHTML = this._bag(); this._bindBag(); break;
      case 'book': this.body.innerHTML = this._book(); break;
      case 'quests': this.body.innerHTML = this._quests(); break;
      case 'travel': this.body.innerHTML = this._travel(); this._bindTravel(); break;
      default: this.body.innerHTML = '';
    }
  }

  /* --- shop ---------------------------------------------------------------- */

  _shop() {
    const d = this.state.data;
    let html = `<div class="panel-sub">Duit anda: <b class="money">${d.money.toLocaleString('ms-MY')}</b></div>`;

    for (const [slot, table] of Object.entries(GEAR_TABLES)) {
      html += `<h3>${SLOT_LABELS[slot]}</h3><div class="grid">`;
      for (const item of table) {
        const owned = this.state.owns(item.id);
        const locked = item.level > d.level;
        const afford = d.money >= item.price;
        const stock = item.consumable ? this.state.stockOf(item.id) : null;
        const stockTxt = stock === Infinity ? '∞' : stock;

        let action;
        if (locked) action = `<button disabled>Tahap ${item.level}</button>`;
        else if (item.consumable) {
          action = `<button data-buy="${item.id}" ${afford ? '' : 'disabled'}>
            Beli ${item.price ? `· ${item.price}` : '· percuma'}</button>`;
        } else if (owned) action = `<button disabled>Dimiliki</button>`;
        else action = `<button data-buy="${item.id}" ${afford ? '' : 'disabled'}>Beli · ${item.price}</button>`;

        html += `
          <div class="card ${locked ? 'locked' : ''} ${owned ? 'owned' : ''}">
            <div class="card-name">${item.name}</div>
            <div class="card-desc">${item.desc}</div>
            <div class="card-stats">${this._statLine(slot, item)}</div>
            ${item.consumable && owned ? `<div class="card-stock">Simpanan: ${stockTxt}</div>` : ''}
            <div class="card-action">${action}</div>
          </div>`;
      }
      html += `</div>`;
    }
    return html;
  }

  _statLine(slot, item) {
    switch (slot) {
      case 'rod': return `Kuasa ${item.power} N · Panjang ${item.length} m · Sensitif ${(item.sensitivity * 100) | 0}%`;
      case 'reel': return `Klac maks ${item.drag} N · Kilas ${item.retrieve} m/s · Nisbah ${item.ratio}:1`;
      case 'line': return `Kekuatan ${item.test} kg · Regangan ${(item.stretch * 100).toFixed(0)}% · Nampak ${(item.visibility * 100) | 0}%`;
      case 'lure': return `Tenggelam ${(item.sink * 100) | 0}% · Gerakan ${(item.action * 100) | 0}% · Bising ${(item.noise * 100) | 0}%`;
      default: return '';
    }
  }

  _bindShop() {
    this.body.querySelectorAll('[data-buy]').forEach((b) => {
      b.addEventListener('click', () => {
        const res = this.state.buy(b.dataset.buy);
        if (!res.ok) {
          const msg = res.reason === 'money' ? `Duit tidak cukup (perlu ${res.need}).`
            : res.reason === 'level' ? `Perlu Tahap ${res.need}.` : 'Tidak boleh dibeli.';
          this.bus.emit(EV.TOAST, { text: msg, kind: 'warn' });
        } else {
          this.bus.emit(EV.TOAST, { text: `Dibeli: ${res.item.name}`, kind: 'good' });
        }
      });
    });
  }

  /* --- bag ----------------------------------------------------------------- */

  _bag() {
    const d = this.state.data;
    let html = '';
    for (const [slot, ids] of Object.entries(d.owned)) {
      html += `<h3>${SLOT_LABELS[slot]}</h3><div class="grid">`;
      for (const id of ids) {
        const item = GEAR_BY_ID[id];
        if (!item) continue;
        const equipped = d.equipped[slot] === id;
        const stock = item.consumable ? this.state.stockOf(id) : null;
        const empty = item.consumable && stock !== Infinity && stock <= 0;
        html += `
          <div class="card ${equipped ? 'equipped' : ''} ${empty ? 'locked' : ''}">
            <div class="card-name">${item.name}</div>
            <div class="card-stats">${this._statLine(slot, item)}</div>
            ${item.consumable ? `<div class="card-stock">Simpanan: ${stock === Infinity ? '∞' : stock}</div>` : ''}
            <div class="card-action">
              <button data-equip="${id}" ${equipped || empty ? 'disabled' : ''}>
                ${equipped ? 'Dipakai' : empty ? 'Habis' : 'Pakai'}</button>
            </div>
          </div>`;
      }
      html += `</div>`;
    }

    // What is actually biting right now, given the current setup. This is the
    // panel that turns the catch table from folklore into information.
    const ctx = this.getContext();
    if (ctx) {
      const rows = odds({
        spot: this.state.spot, phase: ctx.world.phase, weather: ctx.world.weather,
        lure: ctx.gear.lure, lureDepthNorm: ctx.lureDepthNorm ?? 0.5, level: this.state.level,
      });
      html += `<h3>Peluang sekarang · ${ctx.gear.lure.name} · ${ctx.world.phase.label} · ${ctx.world.weather.label}</h3>`;
      html += `<div class="odds">`;
      for (const r of rows.slice(0, 10)) {
        const rar = RARITY[r.species.rarity];
        html += `
          <div class="odds-row">
            <span class="odds-name" style="color:${rar.color}">${r.species.name}</span>
            <div class="odds-bar"><div style="width:${Math.min(r.pct, 100)}%;background:${rar.color}"></div></div>
            <span class="odds-pct">${r.pct.toFixed(1)}%</span>
            <span class="odds-mods">umpan ×${r.mods.lure.toFixed(2)} · masa ×${r.mods.time.toFixed(2)} · dalam ×${r.mods.depth.toFixed(2)}</span>
          </div>`;
      }
      html += `</div>`;
    }
    return html;
  }

  _bindBag() {
    this.body.querySelectorAll('[data-equip]').forEach((b) => {
      b.addEventListener('click', () => this.state.equip(b.dataset.equip));
    });
  }

  /* --- record book --------------------------------------------------------- */

  _book() {
    const d = this.state.data;
    const s = d.stats;
    let html = `
      <div class="stat-strip">
        <div><b>${s.landed}</b><span>didaratkan</span></div>
        <div><b>${s.casts}</b><span>lontaran</span></div>
        <div><b>${s.snaps}</b><span>tali putus</span></div>
        <div><b>${s.trophies}</b><span>trofi</span></div>
        <div><b>${s.heaviestKg.toFixed(2)}</b><span>kg terberat</span></div>
        <div><b>${s.bestStreak}</b><span>rentetan terbaik</span></div>
      </div>
      <h3>Buku rekod · ${Object.keys(d.records).length} / ${SPECIES.length} spesies</h3>
      <div class="grid book">`;

    const sorted = [...SPECIES].sort((a, b) =>
      RARITY[b.rarity].order - RARITY[a.rarity].order || a.name.localeCompare(b.name));

    for (const sp of sorted) {
      const rec = d.records[sp.id];
      const rar = RARITY[sp.rarity];
      let art = '';
      if (rec) {
        const c = spriteFor(sp, { width: 320, height: 160 });
        if (c.toDataURL) art = `<img src="${c.toDataURL('image/png')}" alt="">`;
      }
      html += `
        <div class="card book-card ${rec ? '' : 'unknown'}" style="--rarity:${rar.color}">
          <div class="book-art">${art || '<div class="silhouette">?</div>'}</div>
          <div class="card-name">${rec ? sp.name : '???'}</div>
          <div class="book-rarity" style="color:${rar.color}">${rar.label}</div>
          ${rec
            ? `<div class="book-rec"><b>${rec.lengthCm} cm</b> · ${rec.massKg.toFixed(2)} kg
                 ${rec.trophy ? '<span class="trophy-flag sm">TROFI</span>' : ''}</div>`
            : `<div class="book-rec dim">Belum ditangkap</div>`}
        </div>`;
    }
    return html + `</div>`;
  }

  /* --- quests -------------------------------------------------------------- */

  _quests() {
    const list = this.state.questProgress();
    let html = `<h3>Misi</h3><div class="quest-list">`;
    for (const q of list) {
      html += `
        <div class="quest ${q.done ? 'done' : ''}">
          <div class="quest-check">${q.done ? '✓' : ''}</div>
          <div class="quest-text">
            <div class="quest-name">${q.name}</div>
            <div class="quest-desc">${q.desc}</div>
          </div>
          <div class="quest-reward">${q.reward.money ? `${q.reward.money} duit` : ''}
            ${q.reward.xp ? `· ${q.reward.xp} XP` : ''}</div>
        </div>`;
    }
    return html + `</div>`;
  }

  /* --- travel -------------------------------------------------------------- */

  _travel() {
    const d = this.state.data;
    let html = `<h3>Lokasi memancing</h3><div class="grid">`;
    for (const spot of SPOTS) {
      const unlocked = d.unlockedSpots.includes(spot.id);
      const here = d.spot === spot.id;
      const afford = d.money >= spot.entryFee;
      const pool = Object.keys(spot.pool)
        .map((id) => SPECIES_BY_ID[id])
        .filter((s) => s && s.rarity !== 'junk')
        .sort((a, b) => RARITY[b.rarity].order - RARITY[a.rarity].order)
        .slice(0, 5);

      html += `
        <div class="card spot-card ${unlocked ? '' : 'locked'} ${here ? 'equipped' : ''}">
          <div class="card-name">${spot.name}</div>
          <div class="card-desc">${spot.tagline}</div>
          <div class="card-stats">
            Dalam maks ${spot.maxDepth} m · Jernih ${(spot.waterClarity * 100) | 0}% ·
            Arus ${(spot.current * 100) | 0}% · Reba ${(spot.snagDensity * 100) | 0}%
          </div>
          <div class="spot-species">
            ${pool.map((s) => `<span style="color:${RARITY[s.rarity].color}">${s.name}</span>`).join(' · ')}
          </div>
          <div class="card-action">
            ${here ? '<button disabled>Anda di sini</button>'
              : unlocked ? `<button data-travel="${spot.id}" ${afford ? '' : 'disabled'}>
                  Pergi ${spot.entryFee ? `· ${spot.entryFee}` : '· percuma'}</button>`
              : `<button disabled>Buka pada Tahap ${spot.level}</button>`}
          </div>
        </div>`;
    }
    return html + `</div>`;
  }

  _bindTravel() {
    this.body.querySelectorAll('[data-travel]').forEach((b) => {
      b.addEventListener('click', () => {
        const res = this.state.travel(b.dataset.travel);
        if (!res.ok) {
          this.bus.emit(EV.TOAST, {
            text: res.reason === 'money' ? `Duit tidak cukup (perlu ${res.need}).` : 'Belum dibuka.',
            kind: 'warn',
          });
        } else {
          this.bus.emit(EV.TOAST, { text: `Tiba di ${res.spot.name}`, kind: 'good' });
          this.close();
        }
      });
    });
  }
}

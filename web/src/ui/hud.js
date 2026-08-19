/**
 * The HUD.
 *
 * Reads telemetry, writes DOM. It holds no state of its own beyond animation
 * smoothing, and it can never call into the simulation — every interaction goes
 * back through the event bus or an explicit callback.
 *
 * The tension meter is the centrepiece and deserves a note: it shows the line
 * load as a fraction of breaking strain, with the four physics zones drawn as
 * bands, the drag setting as a separate marker, and line integrity and hook
 * hold as their own bars. All four numbers come straight out of RodSystem —
 * the HUD does no maths, so what the player reads is exactly what the solver
 * used.
 */

import { EV } from '../core/events.js';
import { ZONE, ZONE_LIMITS } from '../physics/rod.js';
import { GAME_STATE } from '../game/fishing.js';
import { BITE_STATE } from '../physics/bite.js';
import { RARITY } from '../data/species.js';
import { spriteFor } from '../assets/fishgen.js';
import { clamp01, damp } from '../core/loop.js';

const el = (tag, cls, html) => {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (html != null) n.innerHTML = html;
  return n;
};

export class HUD {
  constructor(root, { bus, state, onAction }) {
    this.bus = bus;
    this.state = state;
    this.onAction = onAction ?? (() => {});
    this.root = root;
    this.toasts = [];
    this.smoothLoad = 0;
    this.catchCardTimer = 0;

    this._build();
    this._wire();
  }

  _build() {
    const r = this.root;

    /* --- top status bar --------------------------------------------------- */
    this.topBar = el('div', 'hud-top');
    this.topBar.setAttribute('data-ui', '');
    this.topBar.innerHTML = `
      <div class="hud-chip hud-level">
        <span class="lbl">Tahap</span><span class="val" id="hud-level">1</span>
        <div class="xp-track"><div class="xp-fill" id="hud-xp"></div></div>
      </div>
      <div class="hud-chip"><span class="lbl">Duit</span><span class="val money" id="hud-money">0</span></div>
      <div class="hud-chip"><span class="lbl">Masa</span><span class="val" id="hud-clock">06:00</span>
        <span class="sub" id="hud-phase">Subuh</span></div>
      <div class="hud-chip"><span class="lbl">Cuaca</span><span class="val" id="hud-weather">Cerah</span>
        <span class="sub" id="hud-wind">angin 0.2</span></div>
      <div class="hud-chip hud-spot"><span class="lbl">Lokasi</span><span class="val" id="hud-spot">Kolam</span></div>
      <div class="hud-buttons">
        <button data-panel="shop" title="Kedai (B)">Kedai</button>
        <button data-panel="bag" title="Beg (I)">Beg</button>
        <button data-panel="book" title="Rekod (R)">Rekod</button>
        <button data-panel="travel" title="Jelajah (T)">Jelajah</button>
      </div>`;
    r.appendChild(this.topBar);

    /* --- tension column --------------------------------------------------- */
    this.tension = el('div', 'tension-panel hidden');
    this.tension.setAttribute('data-ui', '');
    this.tension.innerHTML = `
      <div class="tension-title">TEGANGAN</div>
      <div class="tension-meter">
        <div class="zone zone-slack"></div>
        <div class="zone zone-good"></div>
        <div class="zone zone-high"></div>
        <div class="zone zone-danger"></div>
        <div class="tension-fill" id="t-fill"></div>
        <div class="drag-marker" id="t-drag"><span></span></div>
        <div class="tension-needle" id="t-needle"></div>
      </div>
      <div class="tension-readout"><span id="t-newton">0 N</span><span id="t-zone">—</span></div>
      <div class="bar-row"><span>Tali</span><div class="bar"><div class="fill line" id="t-line"></div></div></div>
      <div class="bar-row"><span>Kail</span><div class="bar"><div class="fill hook" id="t-hook"></div></div></div>
      <div class="bar-row drag-row"><span>Klac</span><div class="bar"><div class="fill drag" id="t-dragbar"></div></div>
        <span class="hint">A / D</span></div>
      <div class="drag-warn hidden" id="t-dragwarn">KLAC MELEBIHI KEKUATAN TALI</div>`;
    r.appendChild(this.tension);

    /* --- fish panel ------------------------------------------------------- */
    this.fishPanel = el('div', 'fish-panel hidden');
    this.fishPanel.setAttribute('data-ui', '');
    this.fishPanel.innerHTML = `
      <div class="fish-name" id="f-name">—</div>
      <div class="bar-row"><span>Stamina</span><div class="bar"><div class="fill stamina" id="f-stam"></div></div></div>
      <div class="fish-stats">
        <span id="f-dist">0.0 m</span>
        <span id="f-state">—</span>
        <span id="f-depth">0.0 m</span>
      </div>`;
    r.appendChild(this.fishPanel);

    /* --- cast meter ------------------------------------------------------- */
    this.castMeter = el('div', 'cast-meter hidden');
    this.castMeter.setAttribute('data-ui', '');
    this.castMeter.innerHTML = `
      <div class="cast-track">
        <div class="cast-sweet"></div>
        <div class="cast-fill" id="c-fill"></div>
        <div class="cast-over" id="c-over"></div>
      </div>
      <div class="cast-label">TAHAN UNTUK BALING</div>`;
    r.appendChild(this.castMeter);

    /* --- bite prompt ------------------------------------------------------ */
    this.bitePrompt = el('div', 'bite-prompt hidden');
    this.bitePrompt.innerHTML = `
      <svg viewBox="0 0 120 120" class="bite-ring">
        <circle cx="60" cy="60" r="52" class="ring-bg"></circle>
        <circle cx="60" cy="60" r="52" class="ring-fg" id="b-ring"></circle>
      </svg>
      <div class="bite-text">SENTAP!</div>`;
    r.appendChild(this.bitePrompt);

    /* --- presentation readout --------------------------------------------- */
    this.presentation = el('div', 'presentation hidden');
    this.presentation.setAttribute('data-ui', '');
    this.presentation.innerHTML = `
      <div class="pres-title">Persembahan umpan</div>
      <div class="pres-row"><span>Umpan</span><div class="bar sm"><div class="fill" id="p-lure"></div></div></div>
      <div class="pres-row"><span>Kedalaman</span><div class="bar sm"><div class="fill" id="p-depth"></div></div></div>
      <div class="pres-row"><span>Gerakan</span><div class="bar sm"><div class="fill" id="p-action"></div></div></div>
      <div class="pres-row"><span>Senyap</span><div class="bar sm"><div class="fill" id="p-stealth"></div></div></div>
      <div class="pres-state" id="p-state">Menunggu…</div>`;
    r.appendChild(this.presentation);

    /* --- catch card ------------------------------------------------------- */
    this.catchCard = el('div', 'catch-card hidden');
    this.catchCard.setAttribute('data-ui', '');
    r.appendChild(this.catchCard);

    /* --- toasts ----------------------------------------------------------- */
    this.toastWrap = el('div', 'toast-wrap');
    r.appendChild(this.toastWrap);

    /* --- controls hint ---------------------------------------------------- */
    this.hint = el('div', 'controls-hint');
    this.hint.setAttribute('data-ui', '');
    this.hint.innerHTML = `
      <b>Tahan</b> ruang / tetikus untuk baling &nbsp;·&nbsp;
      <b>Enter / E</b> sentap &nbsp;·&nbsp;
      <b>W</b> kilas &nbsp;·&nbsp;
      <b>A / D</b> klac`;
    r.appendChild(this.hint);

    // Cache lookups once; this runs every frame.
    this.$ = (id) => document.getElementById(id);
    this.refs = {
      level: this.$('hud-level'), xp: this.$('hud-xp'), money: this.$('hud-money'),
      clock: this.$('hud-clock'), phase: this.$('hud-phase'), weather: this.$('hud-weather'),
      wind: this.$('hud-wind'), spot: this.$('hud-spot'),
      tFill: this.$('t-fill'), tNeedle: this.$('t-needle'), tDrag: this.$('t-drag'),
      tNewton: this.$('t-newton'), tZone: this.$('t-zone'), tLine: this.$('t-line'),
      tHook: this.$('t-hook'), tDragBar: this.$('t-dragbar'), tDragWarn: this.$('t-dragwarn'),
      fName: this.$('f-name'), fStam: this.$('f-stam'), fDist: this.$('f-dist'),
      fState: this.$('f-state'), fDepth: this.$('f-depth'),
      cFill: this.$('c-fill'), cOver: this.$('c-over'), bRing: this.$('b-ring'),
      pLure: this.$('p-lure'), pDepth: this.$('p-depth'), pAction: this.$('p-action'),
      pStealth: this.$('p-stealth'), pState: this.$('p-state'),
    };

    // Zone bands are laid out from the physics constants, so a tuning change to
    // the model moves the bands on screen automatically.
    const meter = this.tension.querySelector('.tension-meter');
    const bands = [
      ['.zone-slack', 0, ZONE_LIMITS.slack],
      ['.zone-good', ZONE_LIMITS.slack, ZONE_LIMITS.good],
      ['.zone-high', ZONE_LIMITS.good, ZONE_LIMITS.high],
      ['.zone-danger', ZONE_LIMITS.high, 1],
    ];
    for (const [sel, from, to] of bands) {
      const node = meter.querySelector(sel);
      node.style.bottom = `${from * 100}%`;
      node.style.height = `${(to - from) * 100}%`;
    }
  }

  _wire() {
    this.topBar.querySelectorAll('[data-panel]').forEach((btn) => {
      btn.addEventListener('click', () => this.onAction('panel', btn.dataset.panel));
    });

    this.bus.on(EV.TOAST, ({ text, kind }) => this.toast(text, kind));
    this.bus.on(EV.LANDED, (c) => this.showCatch(c));
    this.bus.on(EV.LEVEL_UP, ({ level }) => this.toast(`Naik ke Tahap ${level}!`, 'good'));
    this.bus.on(EV.RECORD, ({ species }) => this.toast(`Rekod baharu: ${species.name}`, 'good'));
    this.bus.on(EV.QUEST_DONE, ({ quest }) => this.toast(`Misi selesai: ${quest.name}`, 'good'));
    this.bus.on(EV.UNLOCK, ({ kind, spot }) => {
      if (kind === 'spot') this.toast(`Lokasi baharu dibuka: ${spot.name}`, 'good');
    });
    this.bus.on(EV.LURE_OUT, ({ item }) => this.toast(`${item.name} habis — tukar ke cacing.`, 'warn'));
    this.bus.on(EV.SNAGGED, () => this.toast('Ikan masuk reba!', 'warn'));
  }

  /* --- per-frame ----------------------------------------------------------- */

  update(dt, t, world, state) {
    const d = state.data;
    const xp = state.xpProgress();

    this.refs.level.textContent = d.level;
    this.refs.xp.style.width = `${xp.pct * 100}%`;
    this.refs.money.textContent = d.money.toLocaleString('ms-MY');
    this.refs.clock.textContent = world.clockString();
    this.refs.phase.textContent = world.phase.label;
    this.refs.weather.textContent = world.weather.label;
    this.refs.wind.textContent = `angin ${world.wind.toFixed(1)}`;
    this.refs.spot.textContent = state.spot.name;

    this._updateTension(dt, t);
    this._updateFish(t);
    this._updateCast(t);
    this._updateBite(t);
    this._updatePresentation(t);
    this._updateToasts(dt);

    if (this.catchCardTimer > 0) {
      this.catchCardTimer -= dt;
      if (this.catchCardTimer <= 0) this.catchCard.classList.add('hidden');
    }
  }

  _updateTension(dt, t) {
    const fighting = t.phase === GAME_STATE.FIGHT;
    const active = fighting || t.phase === GAME_STATE.FISHING;
    this.tension.classList.toggle('hidden', !active);
    if (!active) return;

    const r = t.rod;
    this.smoothLoad = damp(this.smoothLoad, clamp01(r.loadFrac), 18, dt);

    this.refs.tFill.style.height = `${this.smoothLoad * 100}%`;
    this.refs.tNeedle.style.bottom = `${this.smoothLoad * 100}%`;
    this.refs.tNewton.textContent = `${r.tension.toFixed(0)} N`;

    const zoneLabels = {
      [ZONE.SLACK]: 'KENDUR', [ZONE.GOOD]: 'BAIK',
      [ZONE.HIGH]: 'TINGGI', [ZONE.DANGER]: 'BAHAYA',
    };
    this.refs.tZone.textContent = zoneLabels[r.zone];
    this.tension.dataset.zone = r.zone;

    // Drag marker sits at the clutch setting expressed on the same scale.
    const dragPct = clamp01(r.dragN / Math.max(r.testN, 1));
    this.refs.tDrag.style.bottom = `${dragPct * 100}%`;
    this.refs.tDrag.classList.toggle('slipping', r.slipping);

    this.refs.tLine.style.width = `${r.lineIntegrity * 100}%`;
    this.refs.tHook.style.width = `${r.hookHold * 100}%`;
    this.refs.tDragBar.style.width = `${r.dragFrac * 100}%`;
    this.refs.tDragWarn.classList.toggle('hidden', !r.dragUnsafe);
  }

  _updateFish(t) {
    const show = t.phase === GAME_STATE.FIGHT && t.fish;
    this.fishPanel.classList.toggle('hidden', !show);
    if (!show) return;
    const f = t.fish;
    // The species is only revealed once the fish is tired enough to be seen —
    // until then it is a shape in the dark, which is most of the drama.
    const revealed = f.stamina < 0.55 || f.dist < 8;
    this.refs.fName.textContent = revealed ? f.species.name : 'Sesuatu yang besar…';
    this.refs.fName.style.color = revealed
      ? RARITY[f.species.rarity].color : '#9aa5ad';
    this.refs.fStam.style.width = `${f.stamina * 100}%`;
    this.refs.fDist.textContent = `${f.dist.toFixed(1)} m`;
    this.refs.fDepth.textContent = `${f.depth.toFixed(1)} m dalam`;
    const stateLabels = {
      run: 'MELARI', dive: 'MENYELAM', thrash: 'MENGGELUPUR', circle: 'BERPUSING',
      surge: 'MERENGGUT', rest: 'BERHENTI', jump: 'MELOMPAT', beaten: 'LETIH',
    };
    this.refs.fState.textContent = stateLabels[f.state] ?? f.state;
  }

  _updateCast(t) {
    const charging = t.phase === GAME_STATE.CHARGING;
    this.castMeter.classList.toggle('hidden', !charging);
    if (!charging) return;
    this.refs.cFill.style.width = `${t.cast.value * 100}%`;
    this.refs.cOver.style.width = `${t.cast.overload * 100}%`;
    this.castMeter.classList.toggle('sweet', t.cast.inSweetSpot);
    this.castMeter.classList.toggle('over', t.cast.overload > 0);
  }

  _updateBite(t) {
    const inWindow = t.bite.state === BITE_STATE.COMMITTED;
    this.bitePrompt.classList.toggle('hidden', !inWindow);
    if (!inWindow) return;
    // The ring drains over the hookset window: a purely visual timer for a
    // value the FSM already owns.
    const C = 2 * Math.PI * 52;
    this.refs.bRing.style.strokeDasharray = `${C}`;
    this.refs.bRing.style.strokeDashoffset = `${C * (1 - t.bite.windowPct)}`;
  }

  _updatePresentation(t) {
    const show = t.phase === GAME_STATE.FISHING;
    this.presentation.classList.toggle('hidden', !show);
    if (!show || !t.bite.score) return;
    const s = t.bite.score;
    // Lure match can exceed 1 (a fish that loves this bait); cap the bar at
    // full but let the colour show the excess.
    this.refs.pLure.style.width = `${clamp01(s.lureMatch / 2) * 100}%`;
    this.refs.pDepth.style.width = `${clamp01(s.depthMatch) * 100}%`;
    this.refs.pAction.style.width = `${clamp01(s.actionMatch) * 100}%`;
    this.refs.pStealth.style.width = `${clamp01(s.stealth) * 100}%`;

    const labels = {
      [BITE_STATE.SEARCHING]: 'Menunggu…',
      [BITE_STATE.INTEREST]: 'Ada yang datang…',
      [BITE_STATE.NIBBLING]: 'Umpan disentuh!',
      [BITE_STATE.COMMITTED]: 'SENTAP SEKARANG',
      [BITE_STATE.SPOOKED]: 'Ikan lari. Tunggu sebentar.',
      [BITE_STATE.IDLE]: '—',
    };
    this.refs.pState.textContent = labels[t.bite.state] ?? '';
    this.refs.pState.dataset.state = t.bite.state;
  }

  /* --- catch card ---------------------------------------------------------- */

  showCatch(c) {
    const rar = RARITY[c.species.rarity];
    const sprite = spriteFor(c.species, { width: 480, height: 240 });
    const src = sprite.toDataURL ? sprite.toDataURL('image/png') : null;

    this.catchCard.innerHTML = `
      <div class="catch-inner" style="--rarity:${rar.color}">
        ${c.trophy ? '<div class="trophy-flag">TROFI</div>' : ''}
        <div class="catch-rarity">${rar.label}</div>
        <div class="catch-art">${src ? `<img src="${src}" alt="">` : ''}</div>
        <div class="catch-name">${c.species.name}</div>
        <div class="catch-latin">${c.species.latin}</div>
        <div class="catch-stats">
          <div><b>${c.lengthCm}</b><span>cm</span></div>
          <div><b>${c.massKg.toFixed(2)}</b><span>kg</span></div>
          <div><b>${c.fightSeconds}</b><span>saat</span></div>
          <div><b>${c.peakTension}</b><span>N puncak</span></div>
        </div>
        <div class="catch-rewards">
          <span class="money">+${c.value}</span>
          <span class="xp">+${c.xp} XP</span>
          ${c.isRecord ? '<span class="record">REKOD PERIBADI</span>' : ''}
        </div>
        <div class="catch-class">${c.sizeClass.label}</div>
      </div>`;
    this.catchCard.classList.remove('hidden');
    this.catchCardTimer = 3.2;
  }

  /* --- toasts -------------------------------------------------------------- */

  toast(text, kind = 'info') {
    const node = el('div', `toast toast-${kind}`, text);
    this.toastWrap.appendChild(node);
    this.toasts.push({ node, life: 2.8 });
    if (this.toasts.length > 5) {
      const old = this.toasts.shift();
      old.node.remove();
    }
  }

  _updateToasts(dt) {
    for (let i = this.toasts.length - 1; i >= 0; i--) {
      const t = this.toasts[i];
      t.life -= dt;
      if (t.life <= 0) { t.node.remove(); this.toasts.splice(i, 1); }
      else if (t.life < 0.5) t.node.style.opacity = String(t.life / 0.5);
    }
  }
}

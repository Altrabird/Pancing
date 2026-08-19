/**
 * World clock and weather.
 *
 * Time and weather are not decoration: they are two of the four multipliers in
 * the catch table, and they drive the sky, water colour and wave energy. One
 * in-game day is compressed to a few real minutes so a session actually crosses
 * a dawn.
 */

import { EV } from '../core/events.js';
import { TIME_PHASES, WEATHER, WEATHER_BY_ID, phaseForHour } from '../data/spots.js';
import { clamp01, damp, lerp } from '../core/loop.js';

/** Real seconds per in-game hour. */
export const HOUR_SECONDS = 26;

export class World {
  constructor(rng, bus, spot) {
    this.rng = rng;
    this.bus = bus;
    this.spot = spot;
    // Start mid-morning: dawn is the prettiest light in the game but it is also
    // the dimmest, and a first-run player should not open into a dark screen.
    this.hour = 8.6;
    this.day = 1;
    this.phase = phaseForHour(this.hour);
    this.weather = WEATHER_BY_ID.clear;
    this.nextWeatherIn = rng.float(90, 220);
    this.weatherBlend = 1;     // 0..1 transition progress into `weather`
    this.prevWeather = this.weather;

    // Continuous drivers the renderer reads.
    this.wind = spot.windBase;
    this.chop = 0.5;
    this.light = 1;
    this.rain = 0;
    this.sunAngle = 0;
  }

  setSpot(spot) {
    this.spot = spot;
    this.wind = spot.windBase;
  }

  /** Bite activity multiplier: fish feed hardest on a falling light level. */
  activity() {
    const p = this.phase;
    const base = p.id === 'dawn' || p.id === 'dusk' ? 1.35 : p.id === 'night' ? 0.85 : 1.0;
    const w = this.weather.id === 'rain' ? 1.25 : this.weather.id === 'storm' ? 0.7 : 1.0;
    return base * w;
  }

  update(dt) {
    // --- clock -----------------------------------------------------------
    const prevPhase = this.phase.id;
    this.hour += dt / HOUR_SECONDS;
    if (this.hour >= 24) { this.hour -= 24; this.day++; }
    this.phase = phaseForHour(this.hour);
    if (this.phase.id !== prevPhase) {
      this.bus.emit(EV.TIME_PHASE, { phase: this.phase, hour: this.hour, day: this.day });
    }

    // --- weather ---------------------------------------------------------
    this.nextWeatherIn -= dt;
    if (this.nextWeatherIn <= 0) {
      const next = this.rng.weighted(WEATHER.map((w) => [w.id, w.chance]));
      if (next && next !== this.weather.id) {
        this.prevWeather = this.weather;
        this.weather = WEATHER_BY_ID[next];
        this.weatherBlend = 0;
        this.bus.emit(EV.WEATHER_CHANGE, { weather: this.weather, from: this.prevWeather });
      }
      this.nextWeatherIn = this.rng.float(110, 260);
    }
    this.weatherBlend = Math.min(1, this.weatherBlend + dt / 12);

    // --- continuous drivers ----------------------------------------------
    const w = this.weather, pw = this.prevWeather, b = this.weatherBlend;
    const windTarget = this.spot.windBase * lerp(pw.wind, w.wind, b);
    const chopTarget = lerp(pw.chop, w.chop, b);
    const rainTarget = lerp(pw.rain, w.rain, b);

    // Sun elevation: a smooth arc that peaks at noon and goes negative at night.
    const t = (this.hour - 6) / 12;                     // 0 at 06:00, 1 at 18:00
    this.sunAngle = Math.sin(t * Math.PI);
    const daylight = clamp01(this.sunAngle * 1.35 + 0.06);
    const lightTarget = daylight * lerp(pw.light, w.light, b);

    this.wind = damp(this.wind, windTarget, 0.6, dt);
    this.chop = damp(this.chop, chopTarget, 0.5, dt);
    this.rain = damp(this.rain, rainTarget, 0.8, dt);
    this.light = damp(this.light, lightTarget, 1.2, dt);
  }

  /** Surface noise the bite system uses: chop and rain both mask a clumsy cast. */
  surfaceNoise() {
    return clamp01(this.chop * 0.22 + this.rain * 0.30 + this.wind * 0.10);
  }

  clockString() {
    const h = Math.floor(this.hour);
    const m = Math.floor((this.hour - h) * 60);
    return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}`;
  }

  telemetry() {
    return {
      hour: this.hour, day: this.day, clock: this.clockString(),
      phase: this.phase, weather: this.weather,
      wind: this.wind, chop: this.chop, light: this.light, rain: this.rain,
      activity: this.activity(),
    };
  }
}

export { TIME_PHASES, WEATHER };

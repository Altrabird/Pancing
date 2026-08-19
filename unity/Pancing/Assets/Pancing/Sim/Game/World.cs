using System;
using System.Collections.Generic;

namespace Pancing.Sim
{
    /// <summary>
    /// World clock and weather — a port of web/src/game/world.js.
    ///
    /// Time and weather are not decoration: they are two of the four multipliers
    /// in the catch table, and they drive the sky, water colour and wave energy.
    /// One in-game day is compressed to a few real minutes so a session actually
    /// crosses a dawn.
    /// </summary>
    public sealed class World
    {
        /// <summary>Real seconds per in-game hour.</summary>
        public const double HourSeconds = 26;

        private readonly Rng _rng;
        private readonly EventBus _bus;
        private readonly SpotDb _db;
        private readonly List<double> _weatherWeights = new List<double>();

        public Spot Spot;
        public double Hour = 8.6;
        public int Day = 1;
        public TimePhase Phase;
        public WeatherSpec Weather;
        public WeatherSpec PrevWeather;
        public double NextWeatherIn;
        /// <summary>0..1 transition progress into `Weather`.</summary>
        public double WeatherBlend = 1;

        // Continuous drivers the renderer reads.
        public double Wind;
        public double Chop = 0.5;
        public double Light = 1;
        public double Rain;
        public double SunAngle;

        public World(Rng rng, EventBus bus, SpotDb db, Spot spot)
        {
            _rng = rng;
            _bus = bus;
            _db = db;
            Spot = spot;

            // Start mid-morning: dawn is the prettiest light in the game but it is
            // also the dimmest, and a first-run player should not open into a dark
            // screen.
            Hour = 8.6;
            Day = 1;
            Phase = db.PhaseForHour(Hour);
            Weather = db.GetWeather("clear");
            PrevWeather = Weather;
            NextWeatherIn = _rng.Float(90, 220);
            WeatherBlend = 1;

            Wind = spot.WindBase;

            foreach (var w in db.Weathers) _weatherWeights.Add(w.Chance);
        }

        public void SetSpot(Spot spot)
        {
            Spot = spot;
            Wind = spot.WindBase;
        }

        /// <summary>Bite activity multiplier: fish feed hardest on a falling light level.</summary>
        public double Activity()
        {
            string p = Phase?.Id;
            double bas = (p == "dawn" || p == "dusk") ? 1.35 : (p == "night" ? 0.85 : 1.0);
            double w = Weather?.Id == "rain" ? 1.25 : (Weather?.Id == "storm" ? 0.7 : 1.0);
            return bas * w;
        }

        public void Update(double dt)
        {
            // --- clock ---------------------------------------------------------
            string prevPhase = Phase?.Id;
            Hour += dt / HourSeconds;
            if (Hour >= 24) { Hour -= 24; Day++; }
            Phase = _db.PhaseForHour(Hour);
            if (Phase?.Id != prevPhase)
            {
                _bus?.Emit(EV.TimePhaseChanged, new PhaseChange { Phase = Phase, Hour = Hour, Day = Day });
            }

            // --- weather -------------------------------------------------------
            NextWeatherIn -= dt;
            if (NextWeatherIn <= 0)
            {
                int idx = _rng.WeightedIndex(_weatherWeights);
                if (idx >= 0)
                {
                    var next = _db.Weathers[idx];
                    if (next.Id != Weather.Id)
                    {
                        PrevWeather = Weather;
                        Weather = next;
                        WeatherBlend = 0;
                        _bus?.Emit(EV.WeatherChange, new WeatherChange { Weather = Weather, From = PrevWeather });
                    }
                }
                NextWeatherIn = _rng.Float(110, 260);
            }
            WeatherBlend = Math.Min(1, WeatherBlend + dt / 12);

            // --- continuous drivers --------------------------------------------
            var w2 = Weather; var pw = PrevWeather; double b = WeatherBlend;
            double windTarget = Spot.WindBase * MathUtil.Lerp(pw.Wind, w2.Wind, b);
            double chopTarget = MathUtil.Lerp(pw.Chop, w2.Chop, b);
            double rainTarget = MathUtil.Lerp(pw.Rain, w2.Rain, b);

            // Sun elevation: a smooth arc that peaks at noon and goes negative at
            // night.
            double t = (Hour - 6) / 12;                     // 0 at 06:00, 1 at 18:00
            SunAngle = Math.Sin(t * Math.PI);
            double daylight = MathUtil.Clamp01(SunAngle * 1.35 + 0.06);
            double lightTarget = daylight * MathUtil.Lerp(pw.Light, w2.Light, b);

            Wind = MathUtil.Damp(Wind, windTarget, 0.6, dt);
            Chop = MathUtil.Damp(Chop, chopTarget, 0.5, dt);
            Rain = MathUtil.Damp(Rain, rainTarget, 0.8, dt);
            Light = MathUtil.Damp(Light, lightTarget, 1.2, dt);
        }

        /// <summary>Surface noise the bite system uses: chop and rain both mask a clumsy cast.</summary>
        public double SurfaceNoise() => MathUtil.Clamp01(Chop * 0.22 + Rain * 0.30 + Wind * 0.10);

        public string ClockString()
        {
            int h = (int)Math.Floor(Hour);
            int m = (int)Math.Floor((Hour - h) * 60);
            return $"{h:00}:{m:00}";
        }

        public struct PhaseChange { public TimePhase Phase; public double Hour; public int Day; }
        public struct WeatherChange { public WeatherSpec Weather; public WeatherSpec From; }

        public struct Telemetry
        {
            public double Hour;
            public int Day;
            public string Clock;
            public TimePhase Phase;
            public WeatherSpec Weather;
            public double Wind, Chop, Light, Rain, Activity;
        }

        public Telemetry GetTelemetry() => new Telemetry
        {
            Hour = Hour, Day = Day, Clock = ClockString(),
            Phase = Phase, Weather = Weather,
            Wind = Wind, Chop = Chop, Light = Light, Rain = Rain,
            Activity = Activity(),
        };
    }
}

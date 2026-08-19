using System;
using System.Collections.Generic;

namespace Pancing.Sim
{
    /// <summary>A snag point. A fish that reaches one and stays there breaks you off.</summary>
    public struct SpotStructure
    {
        /// <summary>Normalised position across the castable water: U from shore, V lateral.</summary>
        public double U, V, R;
        public string Kind;
    }

    /// <summary>Colours driving the procedural environment generator.</summary>
    public sealed class SpotPalette
    {
        public string Water, Deep, Shallow, Foam, Sand, Grass, Trees;
        public string[] Sky = System.Array.Empty<string>();
    }

    public sealed class Spot
    {
        public string Id, Name, Tagline;
        public int Level;
        public double EntryFee, MaxDepth, WaterClarity, Current, WindBase, SnagDensity;
        /// <summary>
        /// Species ids available here, with a spot-local weight tweak.
        ///
        /// A LIST, not a dictionary, and the order is the order they appear in
        /// spots.json. The catch table walks this order to resolve a weighted
        /// draw, so reordering it changes which fish a given random number picks.
        /// </summary>
        public List<KeyValuePair<string, double>> Pool = new List<KeyValuePair<string, double>>();
        public SpotPalette Palette = new SpotPalette();
        public SpotStructure[] Structure = System.Array.Empty<SpotStructure>();

        /// <summary>
        /// Normalised depth 0..1 across the castable water. The renderer shapes the
        /// lake bed with this same function, so what you see is genuinely what you
        /// are fishing — the water cannot be deep where the ground is high.
        /// </summary>
        public double DepthAt(double u, double v) => SpotShapes.DepthAt(Id, u, v);
    }

    public sealed class TimePhase
    {
        public string Id, Label;
        public double From, To, Sun, Warm;
    }

    public sealed class WeatherSpec
    {
        public string Id, Label;
        public double Chance, Wind, Chop, Light, Rain;
    }

    /// <summary>
    /// Lake-bed shapes.
    ///
    /// This is the one piece of the data tables that is genuinely code rather than
    /// data — `depthAt` is a function of two variables, and JSON has no way to say
    /// "a Gaussian channel offset to one side". Rather than invent an expression
    /// language for three formulas, both engines carry the same three formulas,
    /// keyed by spot id, and shared/parity pins them against each other.
    ///
    /// The JavaScript originals live in web/src/data/spots.js. Keep them in step.
    /// </summary>
    public static class SpotShapes
    {
        public static double DepthAt(string spotId, double u, double v)
        {
            switch (spotId)
            {
                case "kolam":
                {
                    // Gentle dish: deepest in the middle of the pond, shelving to
                    // the banks.
                    double r = MathUtil.Hypot(u - 0.55, v * 0.9);
                    return MathUtil.Clamp01(1.05 - r * 1.5 + Math.Sin(v * 7.0) * 0.03);
                }
                case "sungai":
                {
                    // Channel: a scoured trough offset to one side, shallow gravel
                    // bar opposite.
                    double channel = Math.Exp(-Math.Pow((v + 0.18) / 0.34, 2));
                    return MathUtil.Clamp01(0.18 + u * 0.35 + channel * 0.62 - Math.Abs(v) * 0.15);
                }
                case "tasik":
                {
                    // Steep drop-off close to shore, then a deep flat basin.
                    double shelf = MathUtil.SmoothStep(0.06, 0.30, u);
                    return MathUtil.Clamp01(shelf * 0.92 + u * 0.10 - Math.Abs(v) * 0.06);
                }
                default:
                    // An unknown spot should still be fishable, not a flat zero that
                    // puts every lure on dry land.
                    return MathUtil.Clamp01(0.25 + u * 0.55 - Math.Abs(v) * 0.10);
            }
        }
    }

    public sealed class SpotDb
    {
        public readonly List<Spot> All = new List<Spot>();
        public readonly Dictionary<string, Spot> ById = new Dictionary<string, Spot>();
        public readonly List<TimePhase> Phases = new List<TimePhase>();
        public readonly List<WeatherSpec> Weathers = new List<WeatherSpec>();
        public readonly Dictionary<string, WeatherSpec> WeatherById = new Dictionary<string, WeatherSpec>();

        public Spot Get(string id) =>
            id != null && ById.TryGetValue(id, out var s) ? s : (All.Count > 0 ? All[0] : null);

        public WeatherSpec GetWeather(string id) =>
            id != null && WeatherById.TryGetValue(id, out var w) ? w : (Weathers.Count > 0 ? Weathers[0] : null);

        /// <summary>
        /// The phase containing this hour. Night wraps past midnight, so a phase
        /// whose `to` is less than its `from` matches the union of both ends.
        /// </summary>
        public TimePhase PhaseForHour(double hour)
        {
            double h = ((hour % 24) + 24) % 24;
            foreach (var p in Phases)
            {
                bool inside = p.From < p.To
                    ? (h >= p.From && h < p.To)
                    : (h >= p.From || h < p.To);
                if (inside) return p;
            }
            return Phases.Count > 1 ? Phases[1] : (Phases.Count > 0 ? Phases[0] : null);
        }

        public static SpotDb Load(string json)
        {
            var root = Json.Parse(json);
            var db = new SpotDb();

            foreach (var s in root["spots"].Items)
            {
                var structures = new List<SpotStructure>();
                foreach (var st in s["structure"].Items)
                {
                    structures.Add(new SpotStructure
                    {
                        U = st["u"].AsDouble(),
                        V = st["v"].AsDouble(),
                        R = st["r"].AsDouble(),
                        Kind = st["kind"].AsString("timber"),
                    });
                }

                var pal = s["palette"];
                var spot = new Spot
                {
                    Id = s["id"].AsString(),
                    Name = s["name"].AsString(),
                    Tagline = s["tagline"].AsString(""),
                    Level = s["level"].AsInt(1),
                    EntryFee = s["entryFee"].AsDouble(),
                    MaxDepth = s["maxDepth"].AsDouble(3),
                    WaterClarity = s["waterClarity"].AsDouble(0.5),
                    Current = s["current"].AsDouble(),
                    WindBase = s["windBase"].AsDouble(0.2),
                    SnagDensity = s["snagDensity"].AsDouble(0.2),
                    Pool = s["pool"].AsNumberList(),
                    Structure = structures.ToArray(),
                    Palette = new SpotPalette
                    {
                        Water = pal["water"].AsString("#3f6b5e"),
                        Deep = pal["deep"].AsString("#12302c"),
                        Shallow = pal["shallow"].AsString("#7fae94"),
                        Foam = pal["foam"].AsString("#e8f2ea"),
                        Sand = pal["sand"].AsString("#8d7f5e"),
                        Grass = pal["grass"].AsString("#5c6f43"),
                        Trees = pal["trees"].AsString("#3f5233"),
                        Sky = pal["sky"].AsStringArray(),
                    },
                };

                if (string.IsNullOrEmpty(spot.Id)) continue;
                db.All.Add(spot);
                db.ById[spot.Id] = spot;
            }

            foreach (var p in root["timePhases"].Items)
            {
                db.Phases.Add(new TimePhase
                {
                    Id = p["id"].AsString(),
                    Label = p["label"].AsString(),
                    From = p["from"].AsDouble(),
                    To = p["to"].AsDouble(),
                    Sun = p["sun"].AsDouble(),
                    Warm = p["warm"].AsDouble(),
                });
            }

            foreach (var w in root["weather"].Items)
            {
                var spec = new WeatherSpec
                {
                    Id = w["id"].AsString(),
                    Label = w["label"].AsString(),
                    Chance = w["chance"].AsDouble(),
                    Wind = w["wind"].AsDouble(1),
                    Chop = w["chop"].AsDouble(1),
                    Light = w["light"].AsDouble(1),
                    Rain = w["rain"].AsDouble(),
                };
                db.Weathers.Add(spec);
                db.WeatherById[spec.Id] = spec;
            }

            return db;
        }
    }
}

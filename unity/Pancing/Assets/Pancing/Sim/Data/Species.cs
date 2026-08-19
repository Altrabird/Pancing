using System;
using System.Collections.Generic;

namespace Pancing.Sim
{
    /// <summary>
    /// Length distribution, centimetres. A truncated normal — real length data
    /// for a species is bell-shaped and hard-bounded, not uniform.
    /// </summary>
    public struct LengthDist
    {
        public double Min, Max, Mean, Sd;
    }

    /// <summary>
    /// Standard fisheries length–weight relation, W = a·L^b. Keeping the real
    /// relation rather than a linear fudge is what gives weight its correct skew:
    /// a 10 % longer fish is roughly 33 % heavier, which is exactly why chasing a
    /// length record feels the way it does.
    /// </summary>
    public struct Allometry
    {
        public double A, B;
    }

    /// <summary>Parameters consumed by the fight AI (HookedFish).</summary>
    public struct FightSpec
    {
        public string Profile;
        public double Strength, Stamina, Aggression, Burst, HookHold, StructureSeek, JumpChance;
    }

    /// <summary>Parameters consumed by the bite FSM (BiteSystem).</summary>
    public struct BiteSpec
    {
        public double Caution;
        /// <summary>Hookset window, seconds. 0.32 for a Toman, 1.4 for a prawn.</summary>
        public double Window;
        public int NibbleMin, NibbleMax;
        public double Speed;
    }

    /// <summary>
    /// The art genome. One genome, two outputs: the same body-profile spline and
    /// palette drive both the 3D mesh and the 2D catch-card portrait, so the card
    /// is guaranteed to be a picture of the thing you actually fought.
    /// </summary>
    public struct ArtSpec
    {
        public int Seed;
        public string[] Palette;     // four hex colours
        public double[] Profile;     // body-radius spline control points
        public double Depth;
        public string Tail;
        public string Pattern;
        public double PatternAmt, Dorsal, Eye, Gloss;
    }

    public sealed class Rarity
    {
        public string Id, Label, Color;
        public int Order;
        public double TrophyBonus;
    }

    /// <summary>
    /// Behavioural archetype. The fight AI blends these biases with the individual
    /// fish's rolled stats, so two Toman never fight identically but both fight
    /// like a Toman.
    /// </summary>
    public sealed class FightProfile
    {
        public string Id;
        public double RunBias, DiveBias, ThrashBias, CircleBias, SurgeRate, RestRate, HookWear;
    }

    /// <summary>
    /// One species record. Every field is data, not code: the catch resolver, the
    /// fight AI, the bite FSM and the art pipeline all read from here. Adding a
    /// fish to the game means adding an entry to species.json and nothing else.
    /// </summary>
    public sealed class Species
    {
        public string Id, Name, Latin, RarityId;
        public double Weight;
        public int MinLevel;
        public double Value, Xp;
        public LengthDist Length;
        public Allometry Allometry;
        /// <summary>Preferred normalised depth band, [near-surface 0 .. bottom 1].</summary>
        public double DepthLo, DepthHi;
        public Dictionary<string, double> Times;
        public Dictionary<string, double> Weather;
        /// <summary>
        /// Null means "eats anything" (junk items). An empty entry means the fish
        /// ignores that lure — see SpeciesDb.LureMismatch. The two are genuinely
        /// different and collapsing them made every junk item unfishable.
        /// </summary>
        public Dictionary<string, double> Lures;
        public FightSpec Fight;
        public BiteSpec Bite;
        public ArtSpec Art;

        public double LureMultiplier(string lureId, double mismatch)
        {
            if (Lures == null) return 1.0;
            return Lures.TryGetValue(lureId, out double m) ? m : mismatch;
        }

        public double TimeMultiplier(string phaseId) =>
            Times != null && Times.TryGetValue(phaseId, out double m) ? m : 1.0;

        public double WeatherMultiplier(string weatherId) =>
            Weather != null && Weather.TryGetValue(weatherId, out double m) ? m : 1.0;
    }

    /// <summary>
    /// The loaded species table, plus the rarity and fight-profile lookups that
    /// travel with it. Built from Resources/species.json, which is generated from
    /// the JavaScript reference build by shared/tools/export-data.mjs.
    /// </summary>
    public sealed class SpeciesDb
    {
        public double LureMismatch = 0.3;
        public readonly List<Species> All = new List<Species>();
        public readonly Dictionary<string, Species> ById = new Dictionary<string, Species>();
        public readonly Dictionary<string, Rarity> Rarities = new Dictionary<string, Rarity>();
        public readonly Dictionary<string, FightProfile> Profiles = new Dictionary<string, FightProfile>();

        public Species Get(string id) => id != null && ById.TryGetValue(id, out var s) ? s : null;

        public Rarity RarityOf(Species s) =>
            s != null && Rarities.TryGetValue(s.RarityId, out var r) ? r : null;

        public FightProfile ProfileOf(Species s)
        {
            if (s != null && Profiles.TryGetValue(s.Fight.Profile, out var p)) return p;
            // A species naming a profile that no longer exists should fight like
            // something rather than throw on the hookset.
            return Profiles.TryGetValue("runner", out var fallback) ? fallback : null;
        }

        public static SpeciesDb Load(string json)
        {
            var root = Json.Parse(json);
            var db = new SpeciesDb { LureMismatch = root["lureMismatch"].AsDouble(0.3) };

            foreach (var kv in root["rarity"].Fields)
            {
                db.Rarities[kv.Key] = new Rarity
                {
                    Id = kv.Key,
                    Label = kv.Value["label"].AsString(kv.Key),
                    Color = kv.Value["color"].AsString("#ffffff"),
                    Order = kv.Value["order"].AsInt(),
                    TrophyBonus = kv.Value["trophyBonus"].AsDouble(),
                };
            }

            foreach (var kv in root["fightProfiles"].Fields)
            {
                var p = kv.Value;
                db.Profiles[kv.Key] = new FightProfile
                {
                    Id = kv.Key,
                    RunBias = p["runBias"].AsDouble(),
                    DiveBias = p["diveBias"].AsDouble(),
                    ThrashBias = p["thrashBias"].AsDouble(),
                    CircleBias = p["circleBias"].AsDouble(),
                    SurgeRate = p["surgeRate"].AsDouble(),
                    RestRate = p["restRate"].AsDouble(),
                    HookWear = p["hookWear"].AsDouble(),
                };
            }

            foreach (var s in root["species"].Items)
            {
                var depth = s["depth"].AsDoubleArray();
                var nib = s["bite"]["nibbles"].AsDoubleArray();

                var sp = new Species
                {
                    Id = s["id"].AsString(),
                    Name = s["name"].AsString(),
                    Latin = s["latin"].AsString(),
                    RarityId = s["rarity"].AsString("common"),
                    Weight = s["weight"].AsDouble(),
                    MinLevel = s["minLevel"].AsInt(1),
                    Value = s["value"].AsDouble(),
                    Xp = s["xp"].AsDouble(),
                    Length = new LengthDist
                    {
                        Min = s["length"]["min"].AsDouble(),
                        Max = s["length"]["max"].AsDouble(),
                        Mean = s["length"]["mean"].AsDouble(),
                        Sd = s["length"]["sd"].AsDouble(1),
                    },
                    Allometry = new Allometry
                    {
                        A = s["allometry"]["a"].AsDouble(),
                        B = s["allometry"]["b"].AsDouble(3),
                    },
                    DepthLo = depth.Length > 0 ? depth[0] : 0,
                    DepthHi = depth.Length > 1 ? depth[1] : 1,
                    Times = s["times"].AsNumberMap(),
                    Weather = s["weather"].AsNumberMap(),
                    // Explicit null is "eats anything"; a map is a preference list.
                    Lures = s["lures"].IsNull ? null : s["lures"].AsNumberMap(),
                    Fight = new FightSpec
                    {
                        Profile = s["fight"]["profile"].AsString("runner"),
                        Strength = s["fight"]["strength"].AsDouble(),
                        Stamina = s["fight"]["stamina"].AsDouble(),
                        Aggression = s["fight"]["aggression"].AsDouble(),
                        Burst = s["fight"]["burst"].AsDouble(),
                        HookHold = s["fight"]["hookHold"].AsDouble(),
                        StructureSeek = s["fight"]["structureSeek"].AsDouble(),
                        JumpChance = s["fight"]["jumpChance"].AsDouble(),
                    },
                    Bite = new BiteSpec
                    {
                        Caution = s["bite"]["caution"].AsDouble(),
                        Window = s["bite"]["window"].AsDouble(1),
                        NibbleMin = nib.Length > 0 ? (int)nib[0] : 1,
                        NibbleMax = nib.Length > 1 ? (int)nib[1] : 1,
                        Speed = s["bite"]["speed"].AsDouble(1),
                    },
                    Art = new ArtSpec
                    {
                        Seed = s["art"]["seed"].AsInt(),
                        Palette = s["art"]["palette"].AsStringArray(),
                        Profile = s["art"]["profile"].AsDoubleArray(),
                        Depth = s["art"]["depth"].AsDouble(0.4),
                        Tail = s["art"]["tail"].AsString("fork"),
                        Pattern = s["art"]["pattern"].AsString("plain"),
                        PatternAmt = s["art"]["patternAmt"].AsDouble(),
                        Dorsal = s["art"]["dorsal"].AsDouble(),
                        Eye = s["art"]["eye"].AsDouble(),
                        Gloss = s["art"]["gloss"].AsDouble(0.5),
                    },
                };

                if (string.IsNullOrEmpty(sp.Id)) continue;
                db.All.Add(sp);
                db.ById[sp.Id] = sp;
            }

            return db;
        }
    }
}

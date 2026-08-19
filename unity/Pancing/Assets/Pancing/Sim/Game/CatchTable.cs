using System;
using System.Collections.Generic;

namespace Pancing.Sim
{
    /// <summary>A rolled individual fish, before it is hooked.</summary>
    public sealed class FishRoll
    {
        public Species Species;
        public string SpeciesId;
        public double LengthCm;
        public double MassKg;
        /// <summary>How many standard deviations above the species mean this one is.</summary>
        public double Sigma;
        public double Condition;
        public bool Trophy;
        public string RarityId;
    }

    /// <summary>One row of the odds table, for the "what's biting" panel.</summary>
    public struct OddsRow
    {
        public string Id;
        public Species Species;
        public double Pct;
        public double ModSpot, ModTime, ModWeather, ModLure, ModDepth;
    }

    public struct SizeClass
    {
        public string Label;
        public int Tier;
    }

    /// <summary>
    /// Catch table resolution — a port of web/src/game/catchtable.js.
    ///
    /// Drawing a fish is a weighted pick where every modifier is multiplicative
    /// and transparent. The same function that picks the fish also explains the
    /// pick, so the UI can tell the player "night + rain + shrimp on the bottom is
    /// why you keep hooking Baung" instead of leaving it as folklore.
    ///
    /// Size is drawn from a truncated normal on length, then converted to mass
    /// with the species' real length–weight allometry (W = a·L^b). Weight
    /// therefore has the right skew for free.
    /// </summary>
    public static class CatchTable
    {
        /// <summary>A fish this far outside the species mean is flagged as a trophy.</summary>
        private const double TrophySigma = 1.85;

        /// <summary>Everything the table needs to weigh a swim.</summary>
        public struct Ctx
        {
            public Spot Spot;
            public TimePhase Phase;
            public WeatherSpec Weather;
            public LureSpec Lure;
            public double LureDepthNorm;
            public int Level;
            public double ActivityBonus;
            public SpeciesDb Db;
        }

        public struct Entry
        {
            public string Id;
            public Species Species;
            public double Weight;
            public double ModSpot, ModTime, ModWeather, ModLure, ModDepth;
        }

        /// <summary>
        /// Build the weighted entry list for a context, without drawing. Exposed so
        /// the harness can assert distributions and the UI can preview odds.
        ///
        /// The result preserves the spot pool's order — see the note on Spot.Pool.
        /// </summary>
        public static List<Entry> BuildTable(in Ctx ctx, out double total)
        {
            var entries = new List<Entry>();
            total = 0;
            if (ctx.Spot == null || ctx.Db == null) return entries;

            double activityBonus = ctx.ActivityBonus == 0 ? 1 : ctx.ActivityBonus;

            foreach (var kv in ctx.Spot.Pool)
            {
                var sp = ctx.Db.Get(kv.Key);
                if (sp == null) continue;

                // Level gate: unavailable rather than merely unlikely, so
                // progression reads as unlocking rather than as grinding.
                if (sp.MinLevel > ctx.Level) continue;

                double mTime = ctx.Phase != null ? sp.TimeMultiplier(ctx.Phase.Id) : 1;
                double mWeather = ctx.Weather != null ? sp.WeatherMultiplier(ctx.Weather.Id) : 1;
                double mLure = sp.LureMultiplier(ctx.Lure.Id, ctx.Db.LureMismatch);

                // Depth: a soft band, not a hard gate. Fishing 20 cm off the right
                // layer should cost you a little, not everything.
                double lo = sp.DepthLo, hi = sp.DepthHi;
                double mDepth;
                if (ctx.LureDepthNorm < lo)
                    mDepth = 1 - MathUtil.SmoothStep(0, lo + 0.22, lo - ctx.LureDepthNorm) * 0.92;
                else if (ctx.LureDepthNorm > hi)
                    mDepth = 1 - MathUtil.SmoothStep(0, (1 - hi) + 0.22, ctx.LureDepthNorm - hi) * 0.92;
                else
                    mDepth = 1;
                mDepth = MathUtil.Clamp(mDepth, 0.06, 1);

                double w = sp.Weight * kv.Value * mTime * mWeather * mLure * mDepth * activityBonus;

                if (w > 0)
                {
                    entries.Add(new Entry
                    {
                        Id = kv.Key, Species = sp, Weight = w,
                        ModSpot = kv.Value, ModTime = mTime, ModWeather = mWeather,
                        ModLure = mLure, ModDepth = mDepth,
                    });
                    total += w;
                }
            }

            return entries;
        }

        /// <summary>Odds as percentages, sorted high to low.</summary>
        public static List<OddsRow> Odds(in Ctx ctx)
        {
            var entries = BuildTable(ctx, out double total);
            var rows = new List<OddsRow>(entries.Count);
            foreach (var e in entries)
            {
                rows.Add(new OddsRow
                {
                    Id = e.Id, Species = e.Species,
                    Pct = total > 0 ? (e.Weight / total) * 100 : 0,
                    ModSpot = e.ModSpot, ModTime = e.ModTime, ModWeather = e.ModWeather,
                    ModLure = e.ModLure, ModDepth = e.ModDepth,
                });
            }
            rows.Sort((a, b) => b.Pct.CompareTo(a.Pct));
            return rows;
        }

        /// <summary>Draw a species for the current context, or null if nothing can bite here.</summary>
        public static Species DrawSpecies(Rng rng, in Ctx ctx)
        {
            var entries = BuildTable(ctx, out _);
            if (entries.Count == 0) return null;

            var pairs = new List<KeyValuePair<string, double>>(entries.Count);
            foreach (var e in entries) pairs.Add(new KeyValuePair<string, double>(e.Id, e.Weight));

            string id = rng.Weighted(pairs);
            return id != null ? ctx.Db.Get(id) : null;
        }

        /// <summary>
        /// Roll an individual fish of a species.
        /// </summary>
        /// <param name="sizeBias">Lure sizeBias plus gear bonuses; shifts the mean up.</param>
        /// <param name="luck">0..1 player luck; widens the top tail only.</param>
        public static FishRoll RollFish(Rng rng, SpeciesDb db, Species species,
                                        double sizeBias = 0, double luck = 0)
        {
            var L = species.Length;

            // Bias shifts the mean toward the top of the range rather than scaling
            // it, so a big-fish lure cannot produce an impossible fish.
            double headroom = L.Max - L.Mean;
            double mean = L.Mean + headroom * MathUtil.Clamp01(sizeBias) * 0.55;
            double sd = L.Sd * (1 + luck * 0.22);

            double lengthCm = rng.NormalClamped(mean, sd, L.Min, L.Max);

            // Trophy roll: a rare second draw that pushes the fish into the top tail.
            var rarity = db.RarityOf(species);
            double trophyChance = (rarity?.TrophyBonus ?? 0.02) + luck * 0.03;
            bool trophy = false;
            if (rng.Next() < trophyChance)
            {
                double boosted = rng.NormalClamped(L.Mean + headroom * 0.72, L.Sd * 0.85, L.Mean, L.Max);
                if (boosted > lengthCm) { lengthCm = boosted; trophy = true; }
            }

            double sigma = (lengthCm - L.Mean) / L.Sd;
            if (sigma >= TrophySigma) trophy = true;

            // W = a·L^b, with a little individual condition factor (fat / lean fish).
            double condition = rng.Float(0.90, 1.12);
            double massKg = species.Allometry.A * Math.Pow(lengthCm, species.Allometry.B) * condition;

            return new FishRoll
            {
                Species = species,
                SpeciesId = species.Id,
                LengthCm = MathUtil.RoundTo(lengthCm, 10),
                MassKg = MathUtil.RoundTo(massKg, 1000),
                Sigma = MathUtil.RoundTo(sigma, 100),
                Condition = MathUtil.RoundTo(condition, 100),
                Trophy = trophy,
                RarityId = species.RarityId,
            };
        }

        /// <summary>
        /// Sale value. Trophies and heavy fish are worth disproportionately more,
        /// which keeps a big common fish competitive with a small rare one.
        /// </summary>
        public static int ValueOf(FishRoll fish, double multiplier = 1)
        {
            double bas = fish.Species.Value * Math.Max(fish.MassKg, 0.05);
            double sizeBonus = 1 + MathUtil.Clamp(fish.Sigma, 0, 3) * 0.22;
            double trophyBonus = fish.Trophy ? 1.6 : 1;
            return (int)Math.Max(1, MathUtil.JsRound(bas * sizeBonus * trophyBonus * multiplier));
        }

        public static int XpOf(FishRoll fish, double multiplier = 1)
        {
            double sizeBonus = 1 + MathUtil.Clamp(fish.Sigma, 0, 3) * 0.30;
            return (int)Math.Max(1, MathUtil.JsRound(
                fish.Species.Xp * sizeBonus * (fish.Trophy ? 1.5 : 1) * multiplier));
        }

        /// <summary>Human-readable size class, used on the catch card.</summary>
        public static SizeClass ClassOf(FishRoll fish)
        {
            if (fish.Sigma >= 2.2) return new SizeClass { Label = "Gergasi", Tier = 4 };
            if (fish.Sigma >= 1.2) return new SizeClass { Label = "Besar", Tier = 3 };
            if (fish.Sigma >= -0.4) return new SizeClass { Label = "Sederhana", Tier = 2 };
            if (fish.Sigma >= -1.2) return new SizeClass { Label = "Kecil", Tier = 1 };
            return new SizeClass { Label = "Anak", Tier = 0 };
        }
    }
}

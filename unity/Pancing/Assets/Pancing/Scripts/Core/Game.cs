using UnityEngine;
using Pancing.Sim;

namespace Pancing.Core
{
    /// <summary>
    /// The one place the renderer looks up the simulation.
    ///
    /// A static locator rather than a singleton MonoBehaviour: the simulation is
    /// plain C# with no engine reference, so it has no natural home on a
    /// GameObject, and giving it one would tempt every view script to start
    /// calling Update on it. Everything here is written once by Bootstrap and
    /// read-only thereafter.
    /// </summary>
    public static class Game
    {
        public static SpeciesDb Species;
        public static GearDb Gear;
        public static SpotDb Spots;

        public static EventBus Bus;
        public static Rng Rng;
        public static PlayerState State;
        public static World World;
        public static FishingGame Fishing;

        /// <summary>Water surface height at a world point, for floating things.</summary>
        public static System.Func<float, float, float> SurfaceHeight = (x, z) => 0f;

        public static bool Ready => Fishing != null;

        public static void Reset()
        {
            Species = null; Gear = null; Spots = null;
            Bus = null; Rng = null; State = null; World = null; Fishing = null;
            SurfaceHeight = (x, z) => 0f;
        }

        /* --- shared world metrics --------------------------------------------
         *
         * The angler stands at the origin looking down +Z. Water runs from the
         * bank out to MaxCast, and MaxWidth either side. Every view converts
         * between metres and the simulation's normalised (u, v) through these two
         * helpers, so nothing has to re-derive the mapping and get it subtly
         * different.
         */

        public const float MaxCast = (float)FishingGame.MaxCast;
        public const float HalfWidth = (float)FishingGame.HalfWidth;

        public static void WorldToUV(float x, float z, out float u, out float v)
        {
            u = Mathf.Clamp01(z / MaxCast);
            v = Mathf.Clamp(x / HalfWidth, -1f, 1f);
        }

        /// <summary>
        /// Depth of water at a world point, metres. Zero on dry ground.
        ///
        /// This is THE bathymetry: the lake-bed mesh, the water shader's depth
        /// fade, the shoreline and the catch table's depth modifier all come from
        /// this one call, so the shallows you can see are the shallows you are
        /// fishing.
        /// </summary>
        public static float DepthAtWorld(float x, float z)
        {
            var spot = State?.Spot;
            if (spot == null) return 0f;
            WorldToUV(x, z, out float u, out float v);
            // Beyond the fishable box the ground rises out of the water; clamping
            // u and v would instead smear the last depth outward forever.
            if (z < 0f) return 0f;
            float d = (float)(spot.DepthAt(u, v) * spot.MaxDepth);
            // Fade to nothing at the far shore and the side banks so the water has
            // an edge rather than a wall.
            float edge = Mathf.Min(
                Mathf.SmoothStep(0f, 1f, z / 2.5f),
                Mathf.SmoothStep(0f, 1f, (MaxCast + 6f - z) / 5f),
                Mathf.SmoothStep(0f, 1f, (HalfWidth + 4f - Mathf.Abs(x)) / 4f));
            return Mathf.Max(0f, d * edge);
        }

        /// <summary>Ground height at a world point. Below zero is underwater.</summary>
        public static float GroundHeight(float x, float z)
        {
            float depth = DepthAtWorld(x, z);
            if (depth > 0.001f) return -depth;
            // Dry land: a low bank that climbs away from the water, with enough
            // noise not to read as a ramp.
            float away = Mathf.Max(0f, -z) * 0.10f
                       + Mathf.Max(0f, Mathf.Abs(x) - HalfWidth) * 0.08f
                       + Mathf.Max(0f, z - (MaxCast + 6f)) * 0.12f;
            float bump = ProcNoise.Fbm2(x * 0.09f, z * 0.09f, 3, 1731) - 0.5f;
            return away + bump * 0.35f + 0.04f;
        }
    }
}

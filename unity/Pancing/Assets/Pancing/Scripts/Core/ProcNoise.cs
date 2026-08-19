using System;
using UnityEngine;

namespace Pancing.Core
{
    /// <summary>
    /// Seeded procedural noise — a port of the parts of web/src/assets/noise.js
    /// the Unity build needs.
    ///
    /// There are no image files in this project. Terrain relief, skin grain, fish
    /// patterns and bank scatter are all evaluated from these functions at boot,
    /// which is why the whole game is a few hundred kilobytes of code and no
    /// megabytes of textures — and why a new species needs one JSON record rather
    /// than an artist.
    ///
    /// Not in Pancing.Sim, deliberately: none of this affects a catch, a bite or a
    /// newton of tension. It is presentation, and the simulation must stay free of
    /// it so the parity harness has less to pin.
    /// </summary>
    public static class ProcNoise
    {
        private static float Hash2(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(seed ^ (x * 374761393) ^ (y * 668265263));
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFFFFFFu) / 4294967296f;
            }
        }

        /// <summary>Quintic fade, the one Perlin settled on: zero 1st and 2nd derivatives at the ends.</summary>
        private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

        private static float Mix(float a, float b, float t) => a + (b - a) * t;

        public static float Value2(float x, float y, int seed = 0)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float xf = x - xi, yf = y - yi;
            float u = Fade(xf), v = Fade(yf);
            return Mix(
                Mix(Hash2(xi, yi, seed), Hash2(xi + 1, yi, seed), u),
                Mix(Hash2(xi, yi + 1, seed), Hash2(xi + 1, yi + 1, seed), u),
                v);
        }

        public static float Fbm2(float x, float y, int octaves = 5, int seed = 0,
                                 float lacunarity = 2.0f, float gain = 0.5f)
        {
            float amp = 1f, freq = 1f, sum = 0f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += Value2(x * freq, y * freq, seed + i * 1013) * amp;
                norm += amp;
                amp *= gain;
                freq *= lacunarity;
            }
            return norm > 0f ? sum / norm : 0f;
        }

        /// <summary>Ridged multifractal — sharp creases, for rock and bark.</summary>
        public static float Ridged2(float x, float y, int octaves = 5, int seed = 0)
        {
            float amp = 1f, freq = 1f, sum = 0f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                float n = 1f - Mathf.Abs(Value2(x * freq, y * freq, seed + i * 1013) * 2f - 1f);
                sum += n * n * amp;
                norm += amp;
                amp *= 0.5f;
                freq *= 2.05f;
            }
            return norm > 0f ? sum / norm : 0f;
        }

        /// <summary>
        /// Worley / cellular noise. Returns the distance to the nearest feature
        /// point (f1) and the gap to the second nearest (edge) — spots use f1,
        /// scale plates use the edge.
        /// </summary>
        public static void Worley2(float x, float y, int cells, int seed,
                                   out float f1, out float edge)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float best = 9e9f, second = 9e9f;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int cx = xi + dx, cy = yi + dy;
                    float px = cx + Hash2(cx, cy, seed);
                    float py = cy + Hash2(cx, cy, seed + 7919);
                    float d = Mathf.Sqrt((px - x) * (px - x) + (py - y) * (py - y));
                    if (d < best) { second = best; best = d; }
                    else if (d < second) { second = d; }
                }
            }
            f1 = best;
            edge = second - best;
        }

        /// <summary>
        /// Catmull-Rom through a control polygon, clamped at both ends. This is
        /// what turns a five-number `profile` array in species.json into a smooth
        /// fish silhouette.
        /// </summary>
        public static float Spline(double[] points, float t)
        {
            if (points == null || points.Length == 0) return 0f;
            if (points.Length == 1) return (float)points[0];

            int n = points.Length - 1;
            float scaled = Mathf.Clamp01(t) * n;
            int i = Mathf.Min(Mathf.FloorToInt(scaled), n - 1);
            float f = scaled - i;

            float p0 = (float)points[Mathf.Max(i - 1, 0)];
            float p1 = (float)points[i];
            float p2 = (float)points[Mathf.Min(i + 1, n)];
            float p3 = (float)points[Mathf.Min(i + 2, n)];

            return 0.5f * (
                2f * p1 +
                (-p0 + p2) * f +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * f * f +
                (-p0 + 3f * p1 - 3f * p2 + p3) * f * f * f);
        }

        public static float SmoothStep(float a, float b, float x)
        {
            if (Mathf.Approximately(a, b)) return x < a ? 0f : 1f;
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        /// <summary>Parse "#rrggbb" (or "#rgb"). Returns magenta on nonsense, so a
        /// bad palette entry is loud rather than invisible.</summary>
        public static Color HexToColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Color.magenta;
            string h = hex.TrimStart('#');
            if (h.Length == 3) h = $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}";
            if (h.Length != 6) return Color.magenta;
            try
            {
                int n = Convert.ToInt32(h, 16);
                return new Color(((n >> 16) & 255) / 255f, ((n >> 8) & 255) / 255f, (n & 255) / 255f);
            }
            catch { return Color.magenta; }
        }

        /// <summary>Lighten (amount &gt; 0) or darken a colour, keeping it in gamut.</summary>
        public static Color Shade(Color c, float amount)
        {
            if (amount >= 0f)
                return new Color(Mathf.Lerp(c.r, 1f, amount), Mathf.Lerp(c.g, 1f, amount), Mathf.Lerp(c.b, 1f, amount), c.a);
            float k = 1f + amount;
            return new Color(c.r * k, c.g * k, c.b * k, c.a);
        }
    }
}

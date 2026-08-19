using System.Collections.Generic;
using UnityEngine;
using Pancing.Sim;
using Pancing.Core;

namespace Pancing.Render
{
    /// <summary>
    /// Fish synthesis — one genome, two outputs. A port of web/src/assets/fishgen.js.
    ///
    /// Every species in species.json carries an `art` genome: a body-profile
    /// spline, a four-colour palette, a pattern type, fin styles and a seed. This
    /// turns that genome into BOTH the 3D mesh that swims in the lake AND the 2D
    /// portrait on the catch card, from the same three functions:
    ///
    ///   BodyRadius(u)   half-height of the fish at u along its length
    ///   PatternAt(u,v)  0..1 accent mask, in body-surface coordinates
    ///   ColourAt(u,v)   countershaded base colour plus pattern accent
    ///
    /// That shared derivation is the whole point. A hand-drawn sprite and a
    /// hand-modelled mesh drift apart the moment either is edited; here the card
    /// is guaranteed to be a portrait of the thing the player actually fought,
    /// because both are evaluated from the same body function.
    ///
    /// No textures, no UV unwrapping, no files: the colours are baked into vertex
    /// attributes and the material is a plain vertex-colour shader.
    /// </summary>
    public static class FishMeshGen
    {
        /// <summary>Cross-sections along the body. 34 is where the silhouette stops
        /// visibly faceting at the distances this game actually views a fish.</summary>
        private const int Segments = 34;
        /// <summary>Vertices around each cross-section.</summary>
        private const int Ring = 12;

        private static readonly Dictionary<string, Mesh> Cache = new Dictionary<string, Mesh>();

        /// <summary>
        /// Half-height of the body at u (0 = snout, 1 = tail root), normalised to
        /// length. The genome's `profile` array is the control polygon.
        /// </summary>
        public static float BodyRadius(in ArtSpec art, float u)
        {
            float t = Mathf.Clamp01(u);
            // Taper hard at both ends regardless of the profile, so no fish has a
            // blunt nose or a slab-ended tail root.
            float cap = Mathf.Pow(Mathf.Sin(Mathf.PI * Mathf.Pow(t, 0.78f)), 0.55f);
            return Mathf.Max(0.004f, ProcNoise.Spline(art.Profile, t) * cap);
        }

        /// <summary>Lateral half-width. Fish are compressed side to side; crustaceans are not.</summary>
        public static float BodyWidth(in ArtSpec art, float u)
        {
            bool crustacean = art.Pattern == "segments";
            float compress = crustacean ? 0.85f : 0.34f + (float)art.Gloss * 0.10f;
            return BodyRadius(art, u) * compress;
        }

        /// <summary>
        /// Pattern mask in body coordinates. u runs nose→tail, v runs belly(0) to
        /// back(1). Returns 0..1, where 1 is full accent colour.
        /// </summary>
        public static float PatternAt(in ArtSpec art, float u, float v)
        {
            float amt = (float)art.PatternAmt;
            if (amt <= 0f) return 0f;
            int s = art.Seed;
            float m;

            switch (art.Pattern)
            {
                case "bars":      // vertical banding, tilapia-style
                    m = ProcNoise.SmoothStep(0.35f, 0.65f, 0.5f + 0.5f * Mathf.Sin(u * Mathf.PI * 14f + s));
                    m *= ProcNoise.SmoothStep(0.05f, 0.45f, v);
                    break;
                case "stripe":    // one lateral line down the flank
                    m = 1f - ProcNoise.SmoothStep(0f, 0.13f, Mathf.Abs(v - 0.52f));
                    break;
                case "band":      // broad blotchy horizontal band, sebarau / toman
                    m = (1f - ProcNoise.SmoothStep(0f, 0.24f, Mathf.Abs(v - 0.5f)))
                      * ProcNoise.SmoothStep(0.25f, 0.55f, ProcNoise.Fbm2(u * 7f, v * 3f, 3, s));
                    break;
                case "chevron":   // haruan's angled flank marks
                    m = ProcNoise.SmoothStep(0.42f, 0.62f, 0.5f + 0.5f * Mathf.Sin(u * Mathf.PI * 11f - v * 4.2f + s));
                    m *= ProcNoise.SmoothStep(0.08f, 0.5f, v);
                    break;
                case "spots":
                    ProcNoise.Worley2(u * 11f, v * 5f, 11, s, out float f1, out _);
                    m = Mathf.Pow(Mathf.Clamp01(1f - f1 * 2.6f), 2.2f);
                    break;
                case "mottle":
                    m = ProcNoise.SmoothStep(0.48f, 0.72f, ProcNoise.Fbm2(u * 9f, v * 6f, 4, s));
                    break;
                case "scales":    // kelah's big reflective plates
                    ProcNoise.Worley2(u * 22f, v * 9f, 22, s, out _, out float edge);
                    m = Mathf.Pow(Mathf.Clamp01(1f - edge * 4f), 2f) * 0.8f;
                    break;
                case "segments":  // crustacean plating
                    m = ProcNoise.SmoothStep(0.30f, 0.52f, 0.5f + 0.5f * Mathf.Sin(u * Mathf.PI * 9f + s)) * 0.9f;
                    break;
                default:
                    m = 0f;
                    break;
            }
            return Mathf.Clamp01(m) * amt;
        }

        /// <summary>
        /// Countershading: dark back, mid flank, pale belly — the near-universal
        /// colouring of open-water fish, and the thing that makes a generated fish
        /// read as a fish rather than as a coloured blob.
        /// </summary>
        public static Color ColourAt(in ArtSpec art, float u, float v)
        {
            var pal = art.Palette;
            Color back = ProcNoise.HexToColor(Pick(pal, 0));
            Color flank = ProcNoise.HexToColor(Pick(pal, 1));
            Color belly = ProcNoise.HexToColor(Pick(pal, 2));
            Color accent = ProcNoise.HexToColor(Pick(pal, 3));

            // v: 0 belly → 1 back. Two-stage ramp with the transition biased low,
            // because the pale belly is usually a narrow strip.
            Color c = v < 0.45f
                ? Color.Lerp(belly, flank, ProcNoise.SmoothStep(0.10f, 0.45f, v))
                : Color.Lerp(flank, back, ProcNoise.SmoothStep(0.45f, 0.92f, v));

            // Fine skin grain so large flat flanks are not dead.
            float grain = ProcNoise.Fbm2(u * 30f, v * 14f, 3, art.Seed + 3);
            c = ProcNoise.Shade(c, (grain - 0.5f) * 0.14f);

            // Pattern accent.
            c = Color.Lerp(c, accent, PatternAt(art, u, v));

            // Gill plate and head shading.
            if (u < 0.22f) c = ProcNoise.Shade(c, 0.06f * (1f - u / 0.22f));

            return c;
        }

        private static string Pick(string[] pal, int i) =>
            pal != null && pal.Length > i ? pal[i] : "#808080";

        /// <summary>
        /// Build (or fetch) the mesh for a species. Length is baked in as 1.0 along
        /// Z so the caller scales by the individual fish's real length — two Toman
        /// of different sizes share one mesh.
        /// </summary>
        public static Mesh For(Species species)
        {
            if (species == null) return null;
            if (Cache.TryGetValue(species.Id, out var cached) && cached != null) return cached;

            var mesh = Build(species.Art, species.Id);
            Cache[species.Id] = mesh;
            return mesh;
        }

        /// <summary>Drop the cache. A very large record book otherwise keeps every
        /// species' mesh resident for the whole session.</summary>
        public static void ClearCache()
        {
            foreach (var kv in Cache) if (kv.Value != null) Object.Destroy(kv.Value);
            Cache.Clear();
        }

        private static Mesh Build(in ArtSpec art, string id)
        {
            var verts = new List<Vector3>((Segments + 1) * Ring + 8);
            var norms = new List<Vector3>(verts.Capacity);
            var cols = new List<Color>(verts.Capacity);
            var tris = new List<int>(Segments * Ring * 6 + 48);

            float depthScale = (float)art.Depth;

            // --- body: loft rings along the spine -----------------------------
            for (int s = 0; s <= Segments; s++)
            {
                float u = s / (float)Segments;
                float halfH = BodyRadius(art, u) * depthScale / Mathf.Max(depthScale, 0.02f);
                float halfW = BodyWidth(art, u);

                for (int k = 0; k < Ring; k++)
                {
                    float a = k / (float)Ring * Mathf.PI * 2f;
                    float ca = Mathf.Cos(a), sa = Mathf.Sin(a);

                    // Ellipse cross-section, plus a dorsal ridge that lifts the back.
                    float y = ca * halfH;
                    float x = sa * halfW;
                    float dorsal = (float)art.Dorsal * Mathf.Max(0f, ca) * Mathf.Sin(u * Mathf.PI) * 0.5f;
                    y += dorsal;

                    // v is belly(0) → back(1) around the ring.
                    float v = 0.5f + 0.5f * ca;

                    verts.Add(new Vector3(x, y, u));
                    norms.Add(new Vector3(sa * 0.6f, ca, 0f).normalized);
                    cols.Add(ColourAt(art, u, v));
                }
            }

            for (int s = 0; s < Segments; s++)
            {
                for (int k = 0; k < Ring; k++)
                {
                    int k2 = (k + 1) % Ring;
                    int a = s * Ring + k;
                    int b = s * Ring + k2;
                    int c = (s + 1) * Ring + k;
                    int d = (s + 1) * Ring + k2;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }

            // --- tail fin -------------------------------------------------------
            // A flat blade off the tail root, shaped by the genome's `tail` type.
            // Flat is correct: a caudal fin is a membrane, and giving it thickness
            // reads as a paddle.
            Color tailCol = ColourAt(art, 0.98f, 0.55f);
            float root = BodyRadius(art, 1f);
            float span, sweep, notch;
            switch (art.Tail)
            {
                case "fork": span = 0.19f; sweep = 0.15f; notch = 0.09f; break;
                case "round": span = 0.14f; sweep = 0.11f; notch = 0f; break;
                case "truncate": span = 0.13f; sweep = 0.09f; notch = 0.01f; break;
                case "lunate": span = 0.24f; sweep = 0.17f; notch = 0.13f; break;
                default: span = 0.16f; sweep = 0.12f; notch = 0.05f; break;
            }

            int t0 = verts.Count;
            void TV(Vector3 p) { verts.Add(p); norms.Add(Vector3.right); cols.Add(tailCol); }
            TV(new Vector3(0f, 0f, 1f));                              // root centre
            TV(new Vector3(0f, root * 0.35f, 1f));                    // root top
            TV(new Vector3(0f, span, 1f + sweep));                    // upper tip
            TV(new Vector3(0f, 0f, 1f + sweep - notch));              // notch
            TV(new Vector3(0f, -span, 1f + sweep));                   // lower tip
            TV(new Vector3(0f, -root * 0.35f, 1f));                   // root bottom

            void Tri(int a, int b, int c) { tris.Add(t0 + a); tris.Add(t0 + b); tris.Add(t0 + c); }
            Tri(0, 1, 2); Tri(0, 2, 3); Tri(0, 3, 4); Tri(0, 4, 5);
            // Backfaces, so the fin is visible from both sides without a two-sided
            // shader variant.
            Tri(2, 1, 0); Tri(3, 2, 0); Tri(4, 3, 0); Tri(5, 4, 0);

            // --- eye ------------------------------------------------------------
            if (art.Eye > 0.001f)
            {
                float eyeU = 0.13f;
                float eyeR = (float)art.Eye;
                float bodyH = BodyRadius(art, eyeU);
                float bodyW = BodyWidth(art, eyeU);
                for (int side = -1; side <= 1; side += 2)
                {
                    int e0 = verts.Count;
                    Vector3 centre = new Vector3(side * bodyW * 0.92f, bodyH * 0.35f, eyeU);
                    verts.Add(centre); norms.Add(new Vector3(side, 0, 0)); cols.Add(Color.black);
                    const int EyeRing = 8;
                    for (int k = 0; k < EyeRing; k++)
                    {
                        float a = k / (float)EyeRing * Mathf.PI * 2f;
                        verts.Add(centre + new Vector3(side * 0.004f, Mathf.Sin(a) * eyeR, Mathf.Cos(a) * eyeR));
                        norms.Add(new Vector3(side, 0, 0));
                        cols.Add(k % 4 == 0 ? Color.white * 0.8f : Color.black);
                    }
                    for (int k = 0; k < EyeRing; k++)
                    {
                        int k2 = (k + 1) % EyeRing;
                        if (side > 0) { tris.Add(e0); tris.Add(e0 + 1 + k); tris.Add(e0 + 1 + k2); }
                        else { tris.Add(e0); tris.Add(e0 + 1 + k2); tris.Add(e0 + 1 + k); }
                    }
                }
            }

            var mesh = new Mesh { name = $"fish_{id}" };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetColors(cols);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            // Normals were authored per-ring for a smooth flank; recalculating would
            // flatten the fin and the eye back into the body.
            return mesh;
        }
    }
}

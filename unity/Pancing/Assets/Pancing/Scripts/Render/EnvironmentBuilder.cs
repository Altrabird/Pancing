using System.Collections.Generic;
using UnityEngine;
using Pancing.Core;
using Pancing.Sim;

namespace Pancing.Render
{
    /// <summary>
    /// Builds the world around the water: lake bed, banks, sky dome, vegetation
    /// and the sun. Everything is generated from the spot record — there are no
    /// prefabs and no textures.
    ///
    /// The lake bed is lofted from Game.GroundHeight, which wraps the same
    /// spot.DepthAt() the catch table scores against. That is the whole reason the
    /// scene reads as coherent: the drop-off you can see, the depth the lure
    /// reports, and the depth band a Baung wants are one number, not three that
    /// have to be kept in agreement by hand.
    /// </summary>
    public sealed class EnvironmentBuilder : MonoBehaviour
    {
        private const int TerrainGrid = 96;

        private Light _sun;
        private Transform _scatter;
        private MeshRenderer _skyRenderer;
        private Material _terrainMat, _skyMat, _plantMat;
        private Mesh _terrainMesh, _skyMesh;
        private Spot _spot;

        private Color _skyTop, _skyHorizon;

        public Light Sun => _sun;

        public static EnvironmentBuilder Create(Transform parent, Spot spot)
        {
            var go = new GameObject("Environment");
            go.transform.SetParent(parent, false);
            var env = go.AddComponent<EnvironmentBuilder>();
            env.Build(spot);
            return env;
        }

        private void Build(Spot spot)
        {
            _spot = spot;

            // Vertex-colour lit surfaces everywhere. One shader for the ground and
            // the plants keeps the draw-call count near the floor, which is what
            // matters on the phones this has to run on.
            var vcShader = Shader.Find("Pancing/VertexLit") ?? Shader.Find("Legacy Shaders/Diffuse");
            _terrainMat = new Material(vcShader) { name = "TerrainMaterial" };
            _plantMat = new Material(vcShader) { name = "PlantMaterial" };

            BuildTerrain(spot);
            BuildSky(spot);
            BuildSun();
            BuildScatter(spot);
        }

        /* --- lake bed and banks ------------------------------------------------ */

        private void BuildTerrain(Spot spot)
        {
            var go = new GameObject("LakeBed");
            go.transform.SetParent(transform, false);

            float minX = -Game.HalfWidth - 14f;
            float minZ = -14f;
            float spanX = (Game.HalfWidth + 14f) * 2f;
            float spanZ = Game.MaxCast + 28f;

            int n = TerrainGrid;
            var verts = new Vector3[(n + 1) * (n + 1)];
            var cols = new Color[verts.Length];
            var tris = new int[n * n * 6];

            Color sand = ProcNoise.HexToColor(spot.Palette.Sand);
            Color grass = ProcNoise.HexToColor(spot.Palette.Grass);
            Color deep = ProcNoise.HexToColor(spot.Palette.Deep);

            for (int j = 0; j <= n; j++)
            {
                for (int i = 0; i <= n; i++)
                {
                    float fx = i / (float)n, fz = j / (float)n;
                    float x = minX + fx * spanX;
                    float z = minZ + fz * spanZ;
                    float y = Game.GroundHeight(x, z);
                    int idx = j * (n + 1) + i;
                    verts[idx] = new Vector3(x, y, z);

                    // Silt below the waterline, sand at the margin, grass above it.
                    // The two blends are narrow on purpose: a wide gradient reads as
                    // fog on the ground rather than as a beach.
                    Color c;
                    if (y < -0.9f) c = Color.Lerp(sand, deep, Mathf.Clamp01((-y - 0.9f) / 3.0f));
                    else if (y < 0.06f) c = sand;
                    else c = Color.Lerp(sand, grass, ProcNoise.SmoothStep(0.06f, 0.55f, y));

                    float grain = ProcNoise.Fbm2(x * 0.5f, z * 0.5f, 3, 4242) - 0.5f;
                    cols[idx] = ProcNoise.Shade(c, grain * 0.16f);
                }
            }

            int t = 0;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int a = j * (n + 1) + i;
                    int b = a + 1;
                    int c2 = a + (n + 1);
                    int d = c2 + 1;
                    tris[t++] = a; tris[t++] = c2; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c2; tris[t++] = d;
                }
            }

            _terrainMesh = new Mesh { name = "LakeBed", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            _terrainMesh.vertices = verts;
            _terrainMesh.colors = cols;
            _terrainMesh.triangles = tris;
            _terrainMesh.RecalculateNormals();
            _terrainMesh.RecalculateBounds();

            go.AddComponent<MeshFilter>().sharedMesh = _terrainMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _terrainMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        /* --- sky ---------------------------------------------------------------- */

        private void BuildSky(Spot spot)
        {
            var sky = spot.Palette.Sky;
            _skyHorizon = ProcNoise.HexToColor(sky != null && sky.Length > 1 ? sky[1] : "#dfe9ee");
            _skyTop = ProcNoise.HexToColor(sky != null && sky.Length > 0 ? sky[0] : "#8fb8d8");

            var go = new GameObject("SkyDome");
            go.transform.SetParent(transform, false);

            // An inverted dome rather than a skybox: the gradient has to shift with
            // the clock every frame, and pushing two colours into a vertex-lit mesh
            // is cheaper and simpler than swapping skybox materials.
            _skyMesh = BuildDome(24, 16, 400f);
            go.AddComponent<MeshFilter>().sharedMesh = _skyMesh;

            var unlit = Shader.Find("Pancing/SkyGradient") ?? Shader.Find("Unlit/Color");
            _skyMat = new Material(unlit) { name = "SkyMaterial" };
            _skyRenderer = go.AddComponent<MeshRenderer>();
            _skyRenderer.sharedMaterial = _skyMat;
            _skyRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _skyRenderer.receiveShadows = false;

            ApplySkyColors(_skyTop, _skyHorizon);
        }

        private static Mesh BuildDome(int segments, int rings, float radius)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            for (int r = 0; r <= rings; r++)
            {
                // Bias the rings toward the horizon, where all the colour change is.
                float v = r / (float)rings;
                float phi = Mathf.Pow(v, 1.6f) * Mathf.PI * 0.5f;
                float y = Mathf.Sin(phi);
                float rad = Mathf.Cos(phi);
                for (int s = 0; s <= segments; s++)
                {
                    float u = s / (float)segments;
                    float theta = u * Mathf.PI * 2f;
                    verts.Add(new Vector3(Mathf.Cos(theta) * rad, y, Mathf.Sin(theta) * rad) * radius);
                    uvs.Add(new Vector2(u, v));
                }
            }

            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = r * (segments + 1) + s;
                    int b = a + 1;
                    int c = a + (segments + 1);
                    int d = c + 1;
                    // Wound inward — the camera is inside the dome.
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(b); tris.Add(d); tris.Add(c);
                }
            }

            var mesh = new Mesh { name = "SkyDome" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);
            return mesh;
        }

        private void ApplySkyColors(Color top, Color horizon)
        {
            if (_skyMat == null) return;
            _skyMat.SetColor("_TopColor", top);
            _skyMat.SetColor("_HorizonColor", horizon);
        }

        /* --- sun ---------------------------------------------------------------- */

        private void BuildSun()
        {
            var go = new GameObject("Sun");
            go.transform.SetParent(transform, false);
            _sun = go.AddComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.shadows = LightShadows.Soft;
            _sun.shadowStrength = 0.55f;
            _sun.intensity = 1.0f;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        }

        /* --- vegetation ---------------------------------------------------------- */

        private void BuildScatter(Spot spot)
        {
            var go = new GameObject("Scatter");
            go.transform.SetParent(transform, false);
            _scatter = go.transform;

            var rng = new Rng(Rng.HashSeed($"scatter:{spot.Id}"));
            Color treeCol = ProcNoise.HexToColor(spot.Palette.Trees);
            Color grassCol = ProcNoise.HexToColor(spot.Palette.Grass);

            var verts = new List<Vector3>();
            var cols = new List<Color>();
            var tris = new List<int>();

            // Trees along the far bank and the two side banks. Cross-plane
            // billboards (two quads at right angles) rather than true billboards:
            // they hold up from any angle, need no per-frame work, and merge into a
            // single mesh — which is what keeps this to one draw call.
            for (int i = 0; i < 90; i++)
            {
                float x, z;
                if (rng.Next() < 0.55)
                {
                    x = (float)rng.Float(-Game.HalfWidth - 12, Game.HalfWidth + 12);
                    z = Game.MaxCast + (float)rng.Float(4, 22);
                }
                else
                {
                    float side = rng.Next() < 0.5 ? -1f : 1f;
                    x = side * (Game.HalfWidth + (float)rng.Float(3, 14));
                    z = (float)rng.Float(-10, Game.MaxCast + 16);
                }

                float y = Game.GroundHeight(x, z);
                if (y < 0.05f) continue;   // no trees in the lake

                float h = (float)rng.Float(4.0, 9.0);
                Color c = ProcNoise.Shade(treeCol, (float)rng.Float(-0.10, 0.28));
                AddTree(verts, cols, tris, new Vector3(x, y, z), h, c, rng);
            }

            // Reeds in the shallow margin — the fringe that makes the waterline read
            // as a bank rather than as a cut.
            for (int i = 0; i < 260; i++)
            {
                float x = (float)rng.Float(-Game.HalfWidth - 2, Game.HalfWidth + 2);
                float z = (float)rng.Float(-2, Game.MaxCast + 4);
                float depth = Game.DepthAtWorld(x, z);
                // A narrow band only. 0.75 m of depth on a gently shelving pond is
                // several metres of water, which planted reeds right across the
                // middle of the lake and made the swim look like a swamp.
                if (depth < 0.04f || depth > 0.38f) continue;

                // Reeds are blades, not cones. At h*0.18 wide they read as tan
                // pyramids in the foreground; a real stand is thin, tall and much
                // darker than the bank behind it.
                float h = (float)rng.Float(0.5, 1.25);
                float w = h * 0.055f;
                Color c = ProcNoise.Shade(grassCol, (float)rng.Float(-0.30, 0.05));
                // Two or three blades per clump, so a stand looks planted rather
                // than like one object standing on its own.
                int blades = 2 + (rng.Next() < 0.4 ? 1 : 0);
                for (int b = 0; b < blades; b++)
                {
                    float ox = (float)rng.Float(-0.16, 0.16);
                    float oz = (float)rng.Float(-0.16, 0.16);
                    float bh = h * (float)rng.Float(0.72, 1.15);
                    AddCrossPlane(verts, cols, tris, new Vector3(x + ox, -depth, z + oz),
                                  w, bh + depth, c, taper: 0.22f);
                }
            }

            if (verts.Count == 0) return;

            var mesh = new Mesh { name = "Scatter", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetColors(cols);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _plantMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        /// <summary>
        /// A tree: a thin trunk plus a rounded canopy, both as cross-planes.
        ///
        /// The first pass drew a single tapered quad per tree, which at 40 m read as
        /// a black slab standing in the water rather than as a tree. The silhouette
        /// is the entire signal at that distance — a visible trunk gap under a
        /// rounded crown is what the eye actually uses to say "tree", and it costs
        /// twelve triangles.
        /// </summary>
        private static void AddTree(List<Vector3> verts, List<Color> cols, List<int> tris,
                                    Vector3 baseP, float h, Color leaf, Rng rng)
        {
            float trunkH = h * (float)rng.Float(0.28, 0.42);
            float trunkW = h * 0.030f;
            Color bark = new Color(0.26f, 0.20f, 0.14f);

            // Trunk: one narrow cross-plane from the ground to the canopy.
            AddCrossPlane(verts, cols, tris, baseP, trunkW, trunkH, bark, taper: 0.75f);

            // Canopy: two or three overlapping crowns, offset and scaled, so the
            // outline is lumpy instead of a perfect lozenge.
            int lobes = rng.Next() < 0.45 ? 3 : 2;
            float crownH = h - trunkH;
            for (int i = 0; i < lobes; i++)
            {
                float scale = i == 0 ? 1f : (float)rng.Float(0.55, 0.82);
                float lift = i == 0 ? 0f : (float)rng.Float(0.15, 0.55) * crownH;
                float sway = i == 0 ? 0f : (float)rng.Float(-0.30, 0.30) * h * 0.16f;
                Vector3 p = baseP + new Vector3(sway, trunkH + lift, sway * 0.6f);
                Color c = ProcNoise.Shade(leaf, i * -0.06f);
                AddCanopy(verts, cols, tris, p, h * 0.20f * scale, crownH * 0.85f * scale, c);
            }
        }

        /// <summary>A crown: a six-sided rounded outline, mirrored into two planes.</summary>
        private static void AddCanopy(List<Vector3> verts, List<Color> cols, List<int> tris,
                                      Vector3 baseP, float w, float h, Color c)
        {
            // Outline in (across, up) as fractions of w and h, from the bottom
            // centre around one side to the top and back down the other.
            var outline = new[]
            {
                new Vector2(0.00f, 0.00f),
                new Vector2(0.72f, 0.16f),
                new Vector2(1.00f, 0.48f),
                new Vector2(0.68f, 0.84f),
                new Vector2(0.00f, 1.00f),
                new Vector2(-0.68f, 0.84f),
                new Vector2(-1.00f, 0.48f),
                new Vector2(-0.72f, 0.16f),
            };

            Color low = ProcNoise.Shade(c, -0.30f);
            for (int plane = 0; plane < 2; plane++)
            {
                Vector3 right = plane == 0 ? Vector3.right : Vector3.forward;
                int centre = verts.Count;
                verts.Add(baseP + Vector3.up * h * 0.5f);
                cols.Add(c);

                for (int i = 0; i < outline.Length; i++)
                {
                    verts.Add(baseP + right * outline[i].x * w + Vector3.up * outline[i].y * h);
                    cols.Add(Color.Lerp(low, c, outline[i].y));
                }
                // ONE winding only. The plant shader is Cull Off and flips the
                // normal per-face, so a second reversed copy buys nothing — and
                // costs everything, because RecalculateNormals then averages each
                // pair of opposing normals to zero and the whole tree lights black.
                for (int i = 0; i < outline.Length; i++)
                {
                    int a = centre + 1 + i;
                    int b = centre + 1 + (i + 1) % outline.Length;
                    tris.Add(centre); tris.Add(a); tris.Add(b);
                }
            }
        }

        private static void AddCrossPlane(List<Vector3> verts, List<Color> cols, List<int> tris,
                                          Vector3 baseP, float w, float h, Color c, float taper)
        {
            for (int plane = 0; plane < 2; plane++)
            {
                Vector3 right = plane == 0 ? Vector3.right : Vector3.forward;
                int v0 = verts.Count;

                // Darker at the base, lighter at the tip: a free ambient-occlusion
                // gradient that does far more for the read than any extra geometry.
                Color low = ProcNoise.Shade(c, -0.35f);

                verts.Add(baseP - right * w);
                verts.Add(baseP + right * w);
                verts.Add(baseP + right * w * taper + Vector3.up * h);
                verts.Add(baseP - right * w * taper + Vector3.up * h);
                cols.Add(low); cols.Add(low); cols.Add(c); cols.Add(c);

                // One winding; Cull Off in the shader shows the back. See AddCanopy.
                tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 1);
                tris.Add(v0); tris.Add(v0 + 3); tris.Add(v0 + 2);
            }
        }

        /* --- per-frame conditions ------------------------------------------------ */

        /// <summary>
        /// Drive the sun, ambient, fog and sky from the world clock. Called every
        /// frame by Bootstrap; everything here is a lerp, so it is cheap.
        /// </summary>
        public void ApplyConditions(World world, Camera cam)
        {
            if (_sun == null || world == null) return;

            float light = Mathf.Clamp01((float)world.Light);
            float sunAngle = (float)world.SunAngle;

            // Elevation follows the clock; azimuth swings so the light actually
            // travels rather than pivoting in place.
            float elevation = Mathf.Lerp(-8f, 68f, Mathf.InverseLerp(-1f, 1f, sunAngle));
            float azimuth = Mathf.Lerp(-60f, 60f, (float)((world.Hour - 6.0) / 12.0));
            _sun.transform.rotation = Quaternion.Euler(elevation, azimuth + 160f, 0f);

            // Warm at the ends of the day, neutral at noon — the phase record's
            // `warm` value is exactly this dial.
            float warm = world.Phase != null ? (float)world.Phase.Warm : 0.3f;
            Color warmTint = Color.Lerp(new Color(1f, 0.98f, 0.94f), new Color(1f, 0.72f, 0.45f), warm);
            _sun.color = warmTint;
            _sun.intensity = Mathf.Lerp(0.12f, 1.15f, light);

            Color amb = Color.Lerp(new Color(0.10f, 0.13f, 0.20f), new Color(0.55f, 0.60f, 0.62f), light);
            RenderSettings.ambientSkyColor = amb * 1.15f;
            RenderSettings.ambientEquatorColor = amb;
            RenderSettings.ambientGroundColor = amb * 0.6f;

            Color top = Color.Lerp(_skyTop * 0.10f, _skyTop, light);
            Color horizon = Color.Lerp(_skyHorizon * 0.16f, _skyHorizon, light);
            // Rain and storm wash the colour out of the sky before they darken it.
            float wet = Mathf.Clamp01((float)world.Rain);
            Color grey = new Color(0.55f, 0.57f, 0.58f) * Mathf.Lerp(0.25f, 1f, light);
            top = Color.Lerp(top, grey, wet * 0.7f);
            horizon = Color.Lerp(horizon, grey * 1.1f, wet * 0.7f);
            ApplySkyColors(top, horizon);

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = horizon;
            // Weather closes the view in; a storm should feel small.
            RenderSettings.fogDensity = Mathf.Lerp(0.006f, 0.028f, wet);

            if (cam != null) cam.backgroundColor = horizon;
        }

        private void OnDestroy()
        {
            if (_terrainMat != null) Destroy(_terrainMat);
            if (_skyMat != null) Destroy(_skyMat);
            if (_plantMat != null) Destroy(_plantMat);
            if (_terrainMesh != null) Destroy(_terrainMesh);
            if (_skyMesh != null) Destroy(_skyMesh);
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using Pancing.Core;
using Pancing.Sim;

namespace Pancing.Render
{
    /// <summary>
    /// Modelled props around the water — kampung house, jetty, sampan, palms,
    /// jungle trees, boulders, a suspension bridge, drowned trees, a floating
    /// house, fish cages, lily pads and limestone hills on the horizon.
    ///
    /// Models live in Resources/Models (CC0 Quaternius via Poly Pizza, plus a few
    /// built procedurally in Blender; source art/props.blend, pipeline
    /// art/bake_props.py). Colour is baked into vertex colours, so they share the
    /// plant material and every placement is merged into ONE mesh: one draw call.
    ///
    /// The spot's snag structure is made visible: a "rock" snag gets a boulder
    /// breaking the surface, "timber" a drowned tree or log, "weed" lily pads.
    /// The player can now read where the snags are instead of finding out by
    /// losing a lure.
    ///
    /// Placement uses its own RNG stream, so it never shifts the existing tree
    /// and reed layout.
    /// </summary>
    public static class PropScatter
    {
        private struct Place
        {
            public string Model;
            public float X, Z, Yaw, Scale;
            /// <summary>Ground: y = ground height - Y. Water: y = -Y (sits on the surface).
            /// Fixed: y = Y.</summary>
            public float Y;
            public Mode Mode;
        }

        private enum Mode { Ground, Water, Fixed }

        private static Place P(string m, float x, float z, float yaw, float scale = 1f, float y = 0f, Mode mode = Mode.Ground) =>
            new Place { Model = m, X = x, Z = z, Yaw = yaw, Scale = scale, Y = y, Mode = mode };

        public static Mesh Build(Transform parent, Spot spot, Material mat)
        {
            var rng = new Rng(Rng.HashSeed($"props:{spot.Id}"));
            var places = new List<Place>();

            switch (spot.Id)
            {
                case "kolam": Kolam(places, rng); break;
                case "sungai": Sungai(places, rng); break;
                case "tasik": Tasik(places, rng); break;
            }
            Structure(places, rng, spot);
            Hills(places, rng, spot.Id == "tasik" ? 0.8f : 1f);

            return Merge(parent, places, mat);
        }

        /* --- layouts ------------------------------------------------------------ */

        private static void Kolam(List<Place> p, Rng rng)
        {
            p.Add(P("rumah", -11f, 45f, 160f, 1.3f));
            p.Add(P("pondok", 19f, 31f, -110f));
            // Jetty runs from the far bank toward the angler, sampan moored alongside.
            p.Add(P("jeti", -4f, 40.5f, 0f, 1.6f, 1.0f));
            p.Add(P("sampan", 1.6f, 35.5f, 10f, 1.15f, 0.12f, Mode.Water));
            p.Add(P("kayu", -20.5f, 25f, 30f));
            p.Add(P("kayu", 14f, 41f, -20f));

            float[,] palms = { { -24, 20 }, { -21, 31 }, { -26, 8 }, { 22, 6 }, { 23, 31 }, { 8, 44 }, { -4, 48 }, { 17, 44 }, { -18, 42 } };
            for (int i = 0; i < palms.GetLength(0); i++)
                p.Add(P("palm", palms[i, 0], palms[i, 1], (float)rng.Float(0, 360), (float)rng.Float(0.8, 1.25)));

            Shore(p, rng, "semak", 22, 0f, 0.7, 1.3);
            Shore(p, rng, "batu", 10, 0.25f, 0.7, 1.3);
        }

        private static void Sungai(List<Place> p, Rng rng)
        {
            // A jambatan gantung across the far reach — the river's landmark.
            p.Add(P("jambatangantung", 0f, 38f, 0f, 1f, 0.5f, Mode.Fixed));

            // Jungle along both banks and the far side: big canopy trees, a few dead ones.
            Shore(p, rng, "pokokbesar", 14, 0f, 0.8, 1.3, far: 0.3);
            Shore(p, rng, "pokok", 12, 0f, 0.8, 1.2, far: 0.4);
            Shore(p, rng, "pokokmati", 3, 0f, 0.9, 1.2);

            // A rocky river: boulders crowd the banks and the margins.
            Shore(p, rng, "batubesar", 18, 0.3f, 0.8, 1.8, near: true);
            Shore(p, rng, "batukecil", 22, 0.2f, 0.7, 1.4, near: true);
            Shore(p, rng, "semak", 14, 0f, 0.8, 1.3);

            // Boulders breaking the surface in the current, beyond the snag structure.
            float[,] mid = { { -9, 12 }, { 12, 20 }, { -14, 27 }, { 5, 31 } };
            for (int i = 0; i < mid.GetLength(0); i++)
                p.Add(P("batubesar", mid[i, 0], mid[i, 1], (float)rng.Float(0, 360), (float)rng.Float(1.0, 1.5), 0.7f, Mode.Water));
        }

        private static void Tasik(List<Place> p, Rng rng)
        {
            // Kenyir-style reservoir: a rumah rakit and fish cages on the water,
            // drowned trees standing out of it, jungle hills all round.
            p.Add(P("rumahrakit", -13f, 31f, 15f, 1.1f, 0.3f, Mode.Water));
            p.Add(P("sangkar", 11f, 33f, -8f, 1.2f, 0.35f, Mode.Water));
            p.Add(P("sampan", -7.5f, 29f, 70f, 1.1f, 0.12f, Mode.Water));

            // Only the crowns of the drowned trees show; the trunks are metres down.
            float[,] drowned = { { -17, 18 }, { 15, 20 }, { -3, 27 }, { 17, 29 }, { 6, 36 } };
            for (int i = 0; i < drowned.GetLength(0); i++)
                p.Add(P("pokokmati", drowned[i, 0], drowned[i, 1], (float)rng.Float(0, 360), (float)rng.Float(0.9, 1.3), 2.2f, Mode.Water));

            Shore(p, rng, "pokokbesar", 18, 0f, 0.9, 1.4, far: 0.5);
            Shore(p, rng, "pokok", 10, 0f, 0.8, 1.2);
            Shore(p, rng, "semak", 16, 0f, 0.8, 1.3);
            Shore(p, rng, "batukecil", 8, 0.2f, 0.8, 1.3, near: true);
        }

        /// <summary>Make the snag structure visible.</summary>
        private static void Structure(List<Place> p, Rng rng, Spot spot)
        {
            foreach (var s in spot.Structure)
            {
                float z = (float)s.U * Game.MaxCast;
                float x = (float)s.V * Game.HalfWidth;
                float r = (float)s.R * Game.MaxCast;
                switch (s.Kind)
                {
                    case "rock":
                        p.Add(P("batubesar", x, z, (float)rng.Float(0, 360), 1.6f, 0.9f, Mode.Water));
                        for (int i = 0; i < 2; i++)
                            p.Add(P("batukecil", x + (float)rng.Float(-r, r) * 0.6f, z + (float)rng.Float(-r, r) * 0.6f,
                                    (float)rng.Float(0, 360), (float)rng.Float(0.8, 1.2), 0.75f, Mode.Water));
                        break;
                    case "timber":
                        if (spot.Id == "kolam")
                            p.Add(P("kayu", x, z, (float)rng.Float(0, 360), 1.3f, 0.35f, Mode.Water));
                        else
                            p.Add(P("pokokmati", x, z, (float)rng.Float(0, 360), 1.0f, 2.6f, Mode.Water));
                        break;
                    case "weed":
                        for (int i = 0; i < 3; i++)
                            p.Add(P("teratai", x + (float)rng.Float(-r, r) * 0.5f, z + (float)rng.Float(-r, r) * 0.5f,
                                    (float)rng.Float(0, 360), (float)rng.Float(1.0, 1.6), -0.03f, Mode.Water));
                        break;
                }
            }
        }

        /// <summary>Limestone hills on the horizon, softened by the fog.</summary>
        private static void Hills(List<Place> p, Rng rng, float distance)
        {
            for (int i = 0; i < 7; i++)
            {
                float a = Mathf.Lerp(-70f, 70f, i / 6f) + (float)rng.Float(-8, 8);
                float d = (float)rng.Float(180, 230) * distance;
                float x = Mathf.Sin(a * Mathf.Deg2Rad) * d;
                float z = Game.MaxCast * 0.5f + Mathf.Cos(a * Mathf.Deg2Rad) * d;
                p.Add(P("bukit", x, z, (float)rng.Float(0, 360), (float)rng.Float(0.9, 1.5), 3f, Mode.Fixed));
            }
        }

        /// <summary>
        /// Scatter `count` props on dry ground along the banks. `near` keeps them
        /// right at the waterline; `far` is the share placed on the far bank.
        /// </summary>
        private static void Shore(List<Place> p, Rng rng, string model, int count, float sink,
                                  double minScale, double maxScale, bool near = false, double far = 0.4)
        {
            int placed = 0;
            for (int tries = 0; placed < count && tries < count * 30; tries++)
            {
                float x, z;
                if (rng.Next() < far)
                {
                    x = (float)rng.Float(-Game.HalfWidth - 4, Game.HalfWidth + 4);
                    z = Game.MaxCast + (float)(near ? rng.Float(3, 7) : rng.Float(5, 14));
                }
                else
                {
                    x = (rng.Next() < 0.5 ? -1f : 1f) * (Game.HalfWidth + (float)(near ? rng.Float(0.5, 3.5) : rng.Float(2, 9)));
                    z = (float)rng.Float(6, Game.MaxCast + 4);
                }
                if (Game.DepthAtWorld(x, z) > 0f) continue;   // keep them out of the water
                p.Add(P(model, x, z, (float)rng.Float(0, 360), (float)rng.Float(minScale, maxScale), sink));
                placed++;
            }
        }

        /* --- merge --------------------------------------------------------------- */

        private static Mesh Merge(Transform parent, List<Place> places, Material mat)
        {
            var cache = new Dictionary<string, Mesh>();
            var parts = new List<CombineInstance>();
            foreach (var p in places)
            {
                if (!cache.TryGetValue(p.Model, out var mesh))
                {
                    var asset = Resources.Load<GameObject>("Models/" + p.Model);
                    mesh = asset != null ? asset.GetComponentInChildren<MeshFilter>()?.sharedMesh : null;
                    if (mesh == null) Debug.LogWarning($"PropScatter: missing model {p.Model}");
                    cache[p.Model] = mesh;
                }
                if (mesh == null) continue;

                float y = p.Mode == Mode.Ground ? Game.GroundHeight(p.X, p.Z) - p.Y
                        : p.Mode == Mode.Water ? -p.Y
                        : p.Y;
                parts.Add(new CombineInstance
                {
                    mesh = mesh,
                    transform = Matrix4x4.TRS(new Vector3(p.X, y, p.Z),
                                              Quaternion.Euler(0f, p.Yaw, 0f), Vector3.one * p.Scale),
                });
            }
            if (parts.Count == 0) return null;

            var merged = new Mesh { name = "Props", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            merged.CombineMeshes(parts.ToArray(), true, true);

            var go = new GameObject("Props");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = merged;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            return merged;
        }
    }
}

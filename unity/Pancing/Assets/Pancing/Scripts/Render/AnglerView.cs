using System.Collections.Generic;
using UnityEngine;
using Pancing.Core;
using Pancing.Sim;

namespace Pancing.Render
{
    /// <summary>
    /// The angler: a low-poly figure who actually holds the rod.
    ///
    /// Generated like everything else in this project — no model file, no rig, no
    /// textures. A handful of tapered boxes and a cone, vertex-coloured, articulated
    /// by transforms.
    ///
    /// It is not decoration. The rod butt used to hang in empty space, and more
    /// importantly the player had no read on effort except a needle in the corner:
    /// the figure leans back against a running fish, coils on a charging cast and
    /// snaps forward on release, so how hard you are working is visible in the
    /// middle of the screen where the eye already is. Same reasoning as making the
    /// float dip — physics you can see beats physics you have to look up.
    /// </summary>
    public sealed class AnglerView : MonoBehaviour
    {
        private Transform _root;      // yaw, follows the aim
        private Transform _hips;
        private Transform _torso;     // lean
        private Transform _armRig;    // both arms, aimed down the rod
        private Transform _head;

        private Material _mat;
        private readonly List<Mesh> _meshes = new List<Mesh>();

        private float _lean;
        private float _coil;
        private float _pump;
        private float _sway;

        /// <summary>Where the hands are. The rod is drawn from here.</summary>
        public Vector3 GripPoint { get; private set; }

        // A kampung angler seen from behind: straw hat, faded shirt, dark trousers.
        private static readonly Color Skin = new Color(0.72f, 0.52f, 0.36f);
        private static readonly Color Shirt = new Color(0.32f, 0.47f, 0.55f);
        private static readonly Color ShirtDark = new Color(0.22f, 0.34f, 0.41f);
        private static readonly Color Trousers = new Color(0.24f, 0.25f, 0.28f);
        private static readonly Color Straw = new Color(0.83f, 0.72f, 0.44f);
        private static readonly Color StrawDark = new Color(0.62f, 0.52f, 0.30f);

        public static AnglerView Create(Transform parent)
        {
            var go = new GameObject("Angler");
            go.transform.SetParent(parent, false);
            var a = go.AddComponent<AnglerView>();
            a.Build();
            return a;
        }

        private void Build()
        {
            var shader = Shader.Find("Pancing/VertexLit") ?? Shader.Find("Legacy Shaders/Diffuse");
            _mat = new Material(shader) { name = "AnglerMaterial" };

            _root = new GameObject("Rig").transform;
            _root.SetParent(transform, false);

            _hips = New("Hips", _root, new Vector3(0f, 0.92f, 0f));

            // Legs hang from the hips. Static — the angler is standing on a bank,
            // not walking.
            Part("LegL", _hips, new Vector3(-0.11f, 0f, 0f), Limb(0.16f, 0.92f, 0.17f, Trousers, ProcNoise.Shade(Trousers, -0.25f)));
            Part("LegR", _hips, new Vector3(0.11f, 0f, 0f), Limb(0.16f, 0.92f, 0.17f, Trousers, ProcNoise.Shade(Trousers, -0.25f)));

            _torso = New("Torso", _hips, Vector3.zero);
            // Chest tapers up and is slightly wider than deep, which is enough to
            // read as a back rather than a crate.
            Part("Chest", _torso, new Vector3(0f, 0.30f, 0f),
                 Tapered(0.40f, 0.24f, 0.60f, 0.34f, 0.21f, Shirt, ShirtDark));

            _head = New("Head", _torso, new Vector3(0f, 0.70f, 0f));
            Part("Neck", _head, new Vector3(0f, -0.04f, 0f), Limb(0.09f, 0.07f, 0.09f, Skin, Skin));
            Part("Skull", _head, new Vector3(0f, 0.10f, 0f),
                 Tapered(0.20f, 0.21f, 0.22f, 0.19f, 0.20f, Skin, ProcNoise.Shade(Skin, -0.18f)));
            // The topi. Wide, conical, and the single most recognisable silhouette
            // for this from behind.
            Part("Hat", _head, new Vector3(0f, 0.20f, 0f), Cone(0.36f, 0.17f, 14, Straw, StrawDark));

            // Both arms live on one rig that points down the rod. Real IK would buy
            // nothing at this camera distance, and this way the hands cannot drift
            // off the grip.
            _armRig = New("ArmRig", _torso, new Vector3(0f, 0.56f, 0.02f));
            Part("ArmL", _armRig, new Vector3(-0.20f, 0f, 0f), Limb(0.115f, 0.62f, 0.115f, Shirt, Skin));
            Part("ArmR", _armRig, new Vector3(0.16f, 0f, 0f), Limb(0.115f, 0.50f, 0.115f, Shirt, Skin));

            GripPoint = new Vector3(0f, 1.05f, 0.15f);
        }

        private static Transform New(string name, Transform parent, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go.transform;
        }

        private void Part(string name, Transform parent, Vector3 localPos, Mesh mesh)
        {
            var t = New(name, parent, localPos);
            t.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = t.gameObject.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshes.Add(mesh);
        }

        /* --- mesh builders ------------------------------------------------------ */

        /// <summary>A box whose pivot is the TOP face centre, so it hangs from a joint.</summary>
        private static Mesh Limb(float w, float len, float d, Color top, Color bottom) =>
            Tapered(w, d, len, w * 0.82f, d * 0.82f, top, bottom, pivotTop: true);

        /// <summary>
        /// A box that may taper from bottom to top. Flat-shaded: every face gets its
        /// own four vertices so the normals stay hard, which is what makes the
        /// low-poly read deliberate instead of smudged.
        /// </summary>
        private static Mesh Tapered(float w0, float d0, float h, float w1, float d1,
                                    Color lower, Color upper, bool pivotTop = false)
        {
            float yb = pivotTop ? -h : -h * 0.5f;
            float yt = pivotTop ? 0f : h * 0.5f;

            var b = new[]
            {
                new Vector3(-w0 * 0.5f, yb, -d0 * 0.5f), new Vector3(w0 * 0.5f, yb, -d0 * 0.5f),
                new Vector3(w0 * 0.5f, yb, d0 * 0.5f),   new Vector3(-w0 * 0.5f, yb, d0 * 0.5f),
            };
            var t = new[]
            {
                new Vector3(-w1 * 0.5f, yt, -d1 * 0.5f), new Vector3(w1 * 0.5f, yt, -d1 * 0.5f),
                new Vector3(w1 * 0.5f, yt, d1 * 0.5f),   new Vector3(-w1 * 0.5f, yt, d1 * 0.5f),
            };

            var verts = new List<Vector3>();
            var cols = new List<Color>();
            var tris = new List<int>();

            void Quad(Vector3 a, Vector3 bb, Vector3 c, Vector3 d, Color ca, Color cc)
            {
                int i = verts.Count;
                verts.Add(a); verts.Add(bb); verts.Add(c); verts.Add(d);
                cols.Add(ca); cols.Add(ca); cols.Add(cc); cols.Add(cc);
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 1);
                tris.Add(i); tris.Add(i + 3); tris.Add(i + 2);
            }

            Quad(b[0], b[1], t[1], t[0], lower, upper);   // back
            Quad(b[1], b[2], t[2], t[1], lower, upper);   // right
            Quad(b[2], b[3], t[3], t[2], lower, upper);   // front
            Quad(b[3], b[0], t[0], t[3], lower, upper);   // left
            Quad(t[0], t[1], t[2], t[3], upper, upper);   // top
            Quad(b[3], b[2], b[1], b[0], lower, lower);   // bottom

            return Finish("part", verts, cols, tris);
        }

        /// <summary>The conical hat, pivoted at its brim.</summary>
        private static Mesh Cone(float radius, float height, int segments, Color brim, Color peak)
        {
            var verts = new List<Vector3> { new Vector3(0f, height, 0f) };
            var cols = new List<Color> { peak };
            var tris = new List<int>();

            for (int i = 0; i < segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                verts.Add(new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius));
                cols.Add(brim);
            }
            for (int i = 0; i < segments; i++)
            {
                int n = 1 + (i + 1) % segments;
                tris.Add(0); tris.Add(1 + i); tris.Add(n);
                // Underside, so it is not a hole when seen from below.
                tris.Add(1 + i); tris.Add(0); tris.Add(n);
            }
            return Finish("hat", verts, cols, tris);
        }

        private static Mesh Finish(string name, List<Vector3> v, List<Color> c, List<int> t)
        {
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /* --- per-frame ---------------------------------------------------------- */

        /// <param name="rodDir">Unit vector the rod blank points along.</param>
        public void Apply(in FishingGame.Telemetry tm, float aimYaw, Vector3 rodDir, float dt)
        {
            // Face where the cast is aimed.
            _root.rotation = Quaternion.Euler(0f, aimYaw * Mathf.Rad2Deg, 0f);

            // Lean back against the fish. Driven by rod bend, which is the tension
            // already solved through the blank — so the body and the meter can never
            // disagree.
            float targetLean = -(float)tm.Rod.Bend * 26f;

            // Coil on the charge, then snap through it on release.
            float targetCoil = tm.Cast.Charging
                ? Mathf.Lerp(0f, 34f, (float)tm.Cast.Value + (float)tm.Cast.Overload)
                : 0f;

            // Winding pulls the shoulders in rhythm — the visible half of "pump and
            // wind", and it scales with the analog retrieve rather than blinking on.
            float reel = Mathf.Clamp01((float)tm.ReelInput);
            float targetPump = tm.Fish.HasValue ? reel * 7f * Mathf.Sin(Time.time * 5.5f) : 0f;

            float k = 1f - Mathf.Exp(-9f * dt);
            _lean = Mathf.Lerp(_lean, targetLean, k);
            _coil = Mathf.Lerp(_coil, targetCoil, tm.Cast.Charging ? k : 1f - Mathf.Exp(-22f * dt));
            _pump = Mathf.Lerp(_pump, targetPump, k);
            // A little idle life, so a waiting angler is not a statue.
            _sway = Mathf.Sin(Time.time * 0.7f) * 1.4f;

            _torso.localRotation = Quaternion.Euler(_lean + _coil + _pump, _sway * 0.5f, _sway * 0.35f);
            _head.localRotation = Quaternion.Euler(-_lean * 0.35f, -_sway * 0.6f, 0f);

            // Point both arms down the rod, so the hands land on the grip whatever
            // the rod is doing.
            Vector3 localRod = _torso.InverseTransformDirection(rodDir.normalized);
            _armRig.localRotation = Quaternion.FromToRotation(Vector3.down, -localRod);

            GripPoint = _armRig.position;
        }

        /// <summary>Hide the angler when the camera is inside them.</summary>
        public void SetVisible(bool visible)
        {
            if (_root != null && _root.gameObject.activeSelf != visible)
                _root.gameObject.SetActive(visible);
        }

        private void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
            foreach (var m in _meshes) if (m != null) Destroy(m);
        }
    }
}

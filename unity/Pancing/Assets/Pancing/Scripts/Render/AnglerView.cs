using System.Collections.Generic;
using UnityEngine;
using Pancing.Core;
using Pancing.Sim;

namespace Pancing.Render
{
    /// <summary>
    /// The angler: a skinned low-poly kampung fisherman who actually holds the rod.
    ///
    /// The mesh is Resources/Models/angler.fbx (CC0 Quaternius "Farmer", re-dressed
    /// in Blender: cowboy hat removed, kampung shirt and trousers baked into vertex
    /// colours — see art/props.blend). The terendak on his head is still generated
    /// here, parented to the head bone.
    ///
    /// There are no animation clips. Every bone is posed from code each frame,
    /// starting from the bind pose:
    ///   - the spine leans back against a running fish (driven by rod bend, so the
    ///     body and the tension meter can never disagree), coils and twists on a
    ///     charging cast and snaps through on release, and pumps with the retrieve;
    ///   - both arms are solved with two-bone IK onto the rod — left hand on the
    ///     butt, right hand up at the reel seat — so the hands cannot drift off it
    ///     whatever the aim or the bend.
    ///
    /// It is not decoration: how hard you are working is visible in the middle of
    /// the screen, where the eye already is.
    /// </summary>
    public sealed class AnglerView : MonoBehaviour
    {
        private Transform _root;      // yaw, follows the aim
        private Transform _model;

        private Transform _abdomen, _torso, _chest, _neck, _head;
        private Transform _upperL, _lowerL, _handL, _upperR, _lowerR, _handR;
        private Transform _thighL, _thighR;

        private readonly Dictionary<Transform, Quaternion> _bind = new Dictionary<Transform, Quaternion>();
        private Material _mat;
        private Mesh _hatMesh;

        private float _lean, _coil, _pump, _sway;

        private Vector3? _hold;
        /// <summary>While set, the right hand leaves the rod to hold up a landed fish here.</summary>
        public void SetHold(Vector3? at) => _hold = at;

        /// <summary>Where the rod butt sits. Matches TackleView's butt.</summary>
        public Vector3 GripPoint { get; private set; } = new Vector3(0f, 1.05f, 0.15f);

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

            var prefab = Resources.Load<GameObject>("Models/angler");
            if (prefab == null) { Debug.LogError("AnglerView: Models/angler missing"); return; }
            _model = Instantiate(prefab, _root, false).transform;
            _model.name = "Model";
            // Stand a little behind the rod butt so the arms have room to reach it.
            _model.localPosition = new Vector3(0f, 0f, -0.16f);
            // The FBX faces the camera (Blender's -Y front); turn him to face the water.
            _model.localRotation = Quaternion.Euler(0f, 180f, 0f);

            // No clips and no controller: the Animator the Generic import adds would
            // only fight the code-driven pose.
            foreach (var anim in _model.GetComponentsInChildren<Animator>()) Destroy(anim);

            foreach (var smr in _model.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                smr.sharedMaterial = _mat;
                smr.updateWhenOffscreen = true;
                smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            _abdomen = Bone("Abdomen");
            _torso = Bone("Torso");
            _chest = Bone("Chest");
            _neck = Bone("Neck");
            _head = Bone("Head");
            _upperL = Bone("UpperArm.L"); _lowerL = Bone("LowerArm.L"); _handL = Bone("Wrist.L");
            _upperR = Bone("UpperArm.R"); _lowerR = Bone("LowerArm.R"); _handR = Bone("Wrist.R");
            _thighL = Bone("UpperLeg.L"); _thighR = Bone("UpperLeg.R");

            foreach (var t in _model.GetComponentsInChildren<Transform>()) _bind[t] = t.localRotation;

            BuildHat();
        }

        private Transform Bone(string name)
        {
            foreach (var t in _model.GetComponentsInChildren<Transform>())
                if (t.name == name) return t;
            Debug.LogWarning($"AnglerView: bone {name} not found");
            return null;
        }

        /// <summary>The terendak: wide, conical, the most recognisable silhouette from behind.</summary>
        private void BuildHat()
        {
            if (_head == null) return;
            _hatMesh = Cone(0.36f, 0.17f, 16, Straw, StrawDark);
            var hat = new GameObject("Terendak");
            hat.transform.position = _head.position + Vector3.up * 0.135f;
            hat.transform.rotation = Quaternion.identity;
            hat.transform.SetParent(_head, true);
            hat.AddComponent<MeshFilter>().sharedMesh = _hatMesh;
            hat.AddComponent<MeshRenderer>().sharedMaterial = _mat;
        }

        private static Mesh Cone(float radius, float height, int segments, Color brim, Color peak)
        {
            var verts = new List<Vector3> { new Vector3(0f, height, 0f) };
            var cols = new List<Color> { peak };
            var tris = new List<int>();
            for (int i = 0; i < segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                verts.Add(new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius));
                // Alternate straw tones: the woven panels of a real terendak.
                cols.Add(i % 2 == 0 ? brim : Color.Lerp(brim, peak, 0.35f));
            }
            // One winding only; the shader is Cull Off and flips back-face normals.
            for (int i = 0; i < segments; i++)
            {
                int n = 1 + (i + 1) % segments;
                tris.Add(0); tris.Add(n); tris.Add(1 + i);
            }
            var m = new Mesh { name = "terendak" };
            m.SetVertices(verts);
            m.SetColors(cols);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            return m;
        }

        /* --- per-frame ---------------------------------------------------------- */

        /// <param name="rodDir">Unit vector the rod blank points along.</param>
        public void Apply(in FishingGame.Telemetry tm, float aimYaw, Vector3 rodDir, float dt)
        {
            if (_model == null) return;

            _root.rotation = Quaternion.Euler(0f, aimYaw * Mathf.Rad2Deg, 0f);

            float targetLean = -(float)tm.Rod.Bend * 22f;
            float targetCoil = tm.Cast.Charging
                ? Mathf.Lerp(0f, 30f, (float)tm.Cast.Value + (float)tm.Cast.Overload)
                : 0f;
            float reel = Mathf.Clamp01((float)tm.ReelInput);
            float targetPump = tm.Fish.HasValue ? reel * 6f * Mathf.Sin(Time.time * 5.5f) : 0f;

            float k = 1f - Mathf.Exp(-9f * dt);
            _lean = Mathf.Lerp(_lean, targetLean, k);
            _coil = Mathf.Lerp(_coil, targetCoil, tm.Cast.Charging ? k : 1f - Mathf.Exp(-22f * dt));
            _pump = Mathf.Lerp(_pump, targetPump, k);
            _sway = Mathf.Sin(Time.time * 0.7f) * 1.2f;

            // Reset to the bind pose; everything below is layered on top.
            foreach (var kv in _bind) kv.Key.localRotation = kv.Value;

            Vector3 right = _root.right, up = Vector3.up;

            // Stance: feet a little apart.
            Rotate(_thighL, _root.forward, -4f);
            Rotate(_thighR, _root.forward, 4f);

            // Spine: lean back against the fish, coil back and twist on the charge,
            // pump with the reel. Spread over three bones so it bends, not hinges.
            float pitch = _lean - _coil * 0.55f + _pump + 4f;
            float twist = _coil * 0.6f + _sway * 0.4f;
            foreach (var b in new[] { _abdomen, _torso, _chest })
            {
                Rotate(b, right, pitch / 3f);
                Rotate(b, up, twist / 3f);
            }
            // The head keeps watching the water: counter most of the lean.
            Rotate(_neck, right, -pitch * 0.5f + 6f);
            Rotate(_head, up, -twist * 0.5f - _sway * 0.3f);

            // Hands onto the rod. TackleView anchors the butt here.
            Vector3 butt = GripPoint;
            Vector3 rod = rodDir.sqrMagnitude > 1e-4f ? rodDir.normalized : _root.forward;
            Vector3 lowerHand = butt + rod * 0.04f;
            Vector3 upperHand = butt + rod * 0.36f;
            // Elbows drop and flare out, as when actually holding a rod.
            if (_upperL != null) Solve(_upperL, _lowerL, _handL, lowerHand, _upperL.position - right * 0.4f - up * 0.6f);
            if (_upperR != null)
                Solve(_upperR, _lowerR, _handR, _hold ?? upperHand,
                      _upperR.position + right * 0.4f - up * (_hold.HasValue ? 0.2f : 0.6f));
        }

        private static void Rotate(Transform t, Vector3 axis, float degrees)
        {
            if (t != null) t.rotation = Quaternion.AngleAxis(degrees, axis) * t.rotation;
        }

        /// <summary>Analytic two-bone IK: aims upper/lower so `end` reaches `target`,
        /// with the elbow bending toward `pole`.</summary>
        private static void Solve(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 pole)
        {
            if (upper == null || lower == null || end == null) return;
            Vector3 a = upper.position, b = lower.position, c = end.position;
            float lab = Vector3.Distance(a, b), lbc = Vector3.Distance(b, c);
            Vector3 toT = target - a;
            float dist = Mathf.Clamp(toT.magnitude, 0.01f, lab + lbc - 0.001f);
            Vector3 dir = toT.normalized;

            float cosA = Mathf.Clamp((lab * lab + dist * dist - lbc * lbc) / (2f * lab * dist), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);
            Vector3 poleDir = Vector3.ProjectOnPlane(pole - a, dir);
            if (poleDir.sqrMagnitude < 1e-6f) poleDir = Vector3.down;
            poleDir.Normalize();
            Vector3 elbow = a + dir * (cosA * lab) + poleDir * (sinA * lab);

            upper.rotation = Quaternion.FromToRotation(b - a, elbow - a) * upper.rotation;
            b = lower.position;
            c = end.position;
            lower.rotation = Quaternion.FromToRotation(c - b, a + dir * dist - b) * lower.rotation;
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
            if (_hatMesh != null) Destroy(_hatMesh);
        }
    }
}

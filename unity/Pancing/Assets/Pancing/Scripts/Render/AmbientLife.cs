using System.Collections.Generic;
using UnityEngine;
using Pancing.Audio;
using Pancing.Core;
using Pancing.Sim;

namespace Pancing.Render
{
    /// <summary>
    /// Things that move without being asked to — the difference between a scene
    /// and a diorama:
    ///   - layang-layang (swiftlets) wheeling over the tree line by day;
    ///   - bangau (egrets), white and slow, crossing the far bank;
    ///   - fish breaking the surface now and then, with a ring and a splash —
    ///     which also tells the player the water is alive;
    ///   - clouds drifting with the wind;
    ///   - kelip-kelip (fireflies) blinking along the banks after dark;
    ///   - rain: streaks round the camera and drops ringing the water, scaled by
    ///     the world's rain.
    /// All of it is cosmetic and reads the world; nothing writes back to the sim.
    /// </summary>
    public sealed class AmbientLife : MonoBehaviour
    {
        private sealed class Bird
        {
            public Transform Root, WingL, WingR;
            public Vector3 Centre;
            public float Radius, Speed, Phase, Height, FlapHz, FlapAmp;
            public bool Egret;
        }

        private readonly List<Bird> _birds = new List<Bird>();
        private readonly List<Transform> _clouds = new List<Transform>();
        private readonly List<Mesh> _meshes = new List<Mesh>();
        private Material _mat, _cloudMat, _fishMat, _glowMat;
        private Texture2D _glowTex;

        /// <summary>A soft round dot, so a firefly is a glow rather than a square.</summary>
        private static Texture2D SoftDot(int n)
        {
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = new Vector2(x + 0.5f - n * 0.5f, y + 0.5f - n * 0.5f).magnitude / (n * 0.5f);
                float a = Mathf.Clamp01(1f - d);
                px[y * n + x] = new Color32(255, 255, 255, (byte)(a * a * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        private WaterSurface _water;
        private AudioService _audio;
        private Transform _cam;

        private Transform _jumper;
        private MeshFilter _jumperFilter;
        private float _jumpT = -1f, _nextJump = 4f;
        private Vector3 _jumpFrom, _jumpTo;

        private ParticleSystem _fireflies, _rain;
        private float _rainRippleAcc;

        public static AmbientLife Create(Transform parent, WaterSurface water, AudioService audio, Camera cam)
        {
            var go = new GameObject("AmbientLife");
            go.transform.SetParent(parent, false);
            var a = go.AddComponent<AmbientLife>();
            a._water = water;
            a._audio = audio;
            a._cam = cam != null ? cam.transform : null;
            a.Build();
            return a;
        }

        private void Build()
        {
            var vl = Shader.Find("Pancing/VertexLit");
            _mat = new Material(vl) { name = "LifeMaterial" };
            // Clouds are lit mostly by the sky, not the sun: high ambient keeps
            // their bellies from going charcoal when the sun is low.
            _cloudMat = new Material(vl) { name = "CloudMaterial" };
            _cloudMat.SetFloat("_Ambient", 0.75f);
            _fishMat = new Material(Shader.Find("Pancing/Fish") ?? vl) { name = "JumperMaterial" };
            _fishMat.SetFloat("_SwimAmp", 0.08f);
            _fishMat.SetFloat("_SwimFreq", 16f);
            _glowMat = new Material(Shader.Find("Sprites/Default")) { name = "GlowMaterial" };
            _glowMat.mainTexture = _glowTex = SoftDot(32);

            // Swiftlets: small, dark, quick, in loose wheeling loops.
            for (int i = 0; i < 9; i++)
            {
                _birds.Add(MakeBird(false,
                    new Vector3(Random.Range(-18f, 18f), 0f, Random.Range(24f, 42f)),
                    Random.Range(5f, 12f), Random.Range(0.45f, 0.8f), Random.Range(5f, 11f)));
            }
            // Egrets: two big white birds on long slow passes.
            for (int i = 0; i < 2; i++)
            {
                _birds.Add(MakeBird(true,
                    new Vector3(Random.Range(-10f, 10f), 0f, Random.Range(45f, 70f)),
                    Random.Range(30f, 45f), Random.Range(0.08f, 0.12f), Random.Range(7f, 12f)));
            }

            BuildClouds();

            _jumper = new GameObject("Jumper").transform;
            _jumper.SetParent(transform, false);
            _jumperFilter = _jumper.gameObject.AddComponent<MeshFilter>();
            _jumper.gameObject.AddComponent<MeshRenderer>().sharedMaterial = _fishMat;
            _jumper.gameObject.SetActive(false);

            _fireflies = MakeParticles("Fireflies", new Color(0.75f, 1f, 0.45f), 0.16f);
            var fm = _fireflies.main;
            fm.startLifetime = new ParticleSystem.MinMaxCurve(2f, 4.5f);
            fm.startSpeed = 0.15f;
            fm.maxParticles = 120;
            var fs = _fireflies.shape;
            fs.shapeType = ParticleSystemShapeType.Box;
            fs.scale = new Vector3(48f, 2.5f, 50f);
            _fireflies.transform.position = new Vector3(0f, 1.6f, 20f);
            var fcol = _fireflies.colorOverLifetime;
            fcol.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 0.3f),
                              new GradientAlphaKey(1f, 0.55f), new GradientAlphaKey(0f, 0.7f), new GradientAlphaKey(0.8f, 0.85f),
                              new GradientAlphaKey(0f, 1f) });
            fcol.color = g;
            var fn = _fireflies.noise;
            fn.enabled = true;
            fn.strength = 0.35f;
            fn.frequency = 0.4f;

            _rain = MakeParticles("Rain", new Color(0.80f, 0.86f, 0.92f, 0.28f), 0.025f);
            var rm = _rain.main;
            rm.startLifetime = 0.9f;
            rm.startSpeed = 16f;
            rm.maxParticles = 1600;
            var rs = _rain.shape;
            rs.shapeType = ParticleSystemShapeType.Box;
            rs.scale = new Vector3(30f, 0.5f, 30f);
            rs.rotation = new Vector3(90f, 0f, 0f);   // emit downward
            var rr = _rain.GetComponent<ParticleSystemRenderer>();
            rr.renderMode = ParticleSystemRenderMode.Stretch;
            rr.lengthScale = 4f;
            rr.velocityScale = 0.03f;
        }

        private ParticleSystem MakeParticles(string name, Color c, float size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.startColor = c;
            main.startSize = size;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            var em = ps.emission;
            em.rateOverTime = 0f;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = _glowMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            ps.Play();
            return ps;
        }

        /* --- birds --------------------------------------------------------------- */

        private Bird MakeBird(bool egret, Vector3 centre, float radius, float speed, float height)
        {
            var b = new Bird
            {
                Egret = egret, Centre = centre, Radius = radius, Speed = speed * (Random.value < 0.5f ? -1f : 1f),
                Phase = Random.Range(0f, 6.283f), Height = height,
                FlapHz = egret ? Random.Range(1.6f, 2.1f) : Random.Range(7f, 10f),
                FlapAmp = egret ? 28f : 38f,
            };
            b.Root = new GameObject(egret ? "Bangau" : "Layang").transform;
            b.Root.SetParent(transform, false);

            Color body = egret ? new Color(0.96f, 0.96f, 0.93f) : new Color(0.13f, 0.14f, 0.18f);
            float span = egret ? 0.85f : 0.22f;
            float len = egret ? 0.75f : 0.16f;
            Part(b.Root, Body(len, egret ? 0.09f : 0.035f, body, egret));
            b.WingL = new GameObject("WingL").transform; b.WingL.SetParent(b.Root, false);
            b.WingR = new GameObject("WingR").transform; b.WingR.SetParent(b.Root, false);
            Part(b.WingL, Wing(-span, len * 0.45f, body, egret));
            Part(b.WingR, Wing(span, len * 0.45f, body, egret));
            return b;
        }

        private void Part(Transform t, Mesh m)
        {
            t.gameObject.AddComponent<MeshFilter>().sharedMesh = m;
            var r = t.gameObject.AddComponent<MeshRenderer>();
            r.sharedMaterial = _mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshes.Add(m);
        }

        /// <summary>A spindle body along +z; the egret gets a yellow bill and trailing black legs.</summary>
        private static Mesh Body(float len, float r, Color c, bool egret)
        {
            var v = new List<Vector3>(); var col = new List<Color>(); var tri = new List<int>();
            void Tri(Vector3 a, Vector3 b, Vector3 d, Color cc) { int i = v.Count; v.Add(a); v.Add(b); v.Add(d); col.Add(cc); col.Add(cc); col.Add(cc); tri.Add(i); tri.Add(i + 1); tri.Add(i + 2); }
            Vector3 nose = new Vector3(0, 0, len * 0.5f), tail = new Vector3(0, 0, -len * 0.5f);
            const int n = 5;
            for (int k = 0; k < n; k++)
            {
                float a0 = k / (float)n * Mathf.PI * 2f, a1 = (k + 1) / (float)n * Mathf.PI * 2f;
                Vector3 p0 = new Vector3(Mathf.Cos(a0) * r, Mathf.Sin(a0) * r, len * 0.1f);
                Vector3 p1 = new Vector3(Mathf.Cos(a1) * r, Mathf.Sin(a1) * r, len * 0.1f);
                Tri(nose, p1, p0, c);
                Tri(tail, p0, p1, ProcNoise.Shade(c, -0.15f));
            }
            if (egret)
            {
                Color bill = new Color(0.95f, 0.78f, 0.25f), leg = new Color(0.08f, 0.08f, 0.08f);
                Vector3 head = nose + new Vector3(0, 0.03f, 0.02f);
                Tri(head + new Vector3(-0.012f, 0, 0), head + new Vector3(0.012f, 0, 0), head + new Vector3(0, -0.005f, 0.14f), bill);
                Tri(tail + new Vector3(-0.03f, -0.02f, 0), tail + new Vector3(0.03f, -0.02f, 0), tail + new Vector3(0, -0.03f, -0.35f), leg);
            }
            var m = new Mesh { name = "birdbody" };
            m.SetVertices(v); m.SetColors(col); m.SetTriangles(tri, 0); m.RecalculateNormals();
            return m;
        }

        /// <summary>One wing: a swept quad from the body out to `span` (sign = side).</summary>
        private static Mesh Wing(float span, float chord, Color c, bool egret)
        {
            Color tip = egret ? c : ProcNoise.Shade(c, -0.2f);
            var v = new List<Vector3>
            {
                new Vector3(0, 0, chord * 0.5f), new Vector3(0, 0, -chord * 0.5f),
                new Vector3(span, 0, -chord * (egret ? 0.4f : 1.1f)), new Vector3(span * 0.55f, 0, chord * 0.35f),
            };
            var col = new List<Color> { c, c, tip, tip };
            var tri = span > 0 ? new List<int> { 0, 3, 1, 1, 3, 2 } : new List<int> { 0, 1, 3, 1, 2, 3 };
            var m = new Mesh { name = "wing" };
            m.SetVertices(v); m.SetColors(col); m.SetTriangles(tri, 0); m.RecalculateNormals();
            return m;
        }

        /* --- clouds -------------------------------------------------------------- */

        private void BuildClouds()
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var src = sphere.GetComponent<MeshFilter>().sharedMesh;
            Destroy(sphere);
            for (int c = 0; c < 9; c++)
            {
                var parts = new List<CombineInstance>();
                int puffs = Random.Range(4, 8);
                for (int i = 0; i < puffs; i++)
                {
                    float s = Random.Range(9f, 18f);
                    parts.Add(new CombineInstance
                    {
                        mesh = src,
                        transform = Matrix4x4.TRS(new Vector3(i * 9f - puffs * 4.5f, Random.Range(-1f, 3f), Random.Range(-5f, 5f)),
                                                  Quaternion.identity, new Vector3(s * 1.4f, s * 0.55f, s)),
                    });
                }
                var m = new Mesh { name = "cloud" };
                m.CombineMeshes(parts.ToArray(), true, true);
                // Flat white tops, slightly grey bellies.
                var verts = m.vertices;
                var cols = new Color[verts.Length];
                for (int i = 0; i < verts.Length; i++)
                    cols[i] = Color.Lerp(new Color(0.78f, 0.80f, 0.84f), Color.white, Mathf.InverseLerp(-6f, 6f, verts[i].y));
                m.colors = cols;
                _meshes.Add(m);

                var go = new GameObject("Cloud");
                go.transform.SetParent(transform, false);
                go.transform.position = new Vector3(Random.Range(-260f, 260f), Random.Range(70f, 110f), Random.Range(120f, 320f));
                go.AddComponent<MeshFilter>().sharedMesh = m;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = _cloudMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                _clouds.Add(go.transform);
            }
        }

        /* --- per-frame ------------------------------------------------------------ */

        public void Apply(World world, Spot spot, float dt)
        {
            if (world == null) return;
            float light = Mathf.Clamp01((float)world.Light);
            float wet = Mathf.Clamp01((float)world.Rain);
            float wind = (float)world.Wind;
            float t = Time.time;

            // Birds roost at night and in heavy rain.
            bool flying = light > 0.35f && wet < 0.7f;
            foreach (var b in _birds)
            {
                if (b.Root.gameObject.activeSelf != flying) b.Root.gameObject.SetActive(flying);
                if (!flying) continue;
                float a = b.Phase + t * b.Speed;
                // Egrets fly long ellipses; swiftlets add a quick weave.
                float weave = b.Egret ? 0f : Mathf.Sin(t * 2.3f + b.Phase) * 2.5f;
                var pos = b.Centre + new Vector3(Mathf.Cos(a) * b.Radius, b.Height + Mathf.Sin(t * 0.7f + b.Phase) * 1.5f,
                                                 Mathf.Sin(a) * b.Radius * (b.Egret ? 0.35f : 0.6f) + weave);
                var vel = pos - b.Root.position;
                b.Root.position = pos;
                if (vel.sqrMagnitude > 1e-5f) b.Root.rotation = Quaternion.LookRotation(vel, Vector3.up) * Quaternion.Euler(0, 0, -Mathf.Sign(b.Speed) * 18f);
                // Swiftlets flap in bursts then glide; egrets beat slowly and steadily.
                float burst = b.Egret ? 1f : Mathf.Clamp01(Mathf.Sin(t * 0.9f + b.Phase * 3f) * 3f);
                float flap = Mathf.Sin(t * b.FlapHz * 6.283f) * b.FlapAmp * burst;
                b.WingL.localRotation = Quaternion.Euler(0, 0, -flap);
                b.WingR.localRotation = Quaternion.Euler(0, 0, flap);
            }

            // Clouds drift with the wind and darken with the rain.
            foreach (var c in _clouds)
            {
                c.position += new Vector3(1f, 0f, 0.15f) * (1.2f + wind * 3f) * dt;
                if (c.position.x > 300f) c.position -= new Vector3(600f, 0f, 0f);
            }
            // Rain greys them; night takes them down to a moonlit slate, or they
            // glow against a black sky.
            Color day = Color.Lerp(Color.white, new Color(0.62f, 0.64f, 0.68f), wet);
            // (Mathf.SmoothStep interpolates between its first two arguments; it is
            // not the shader smoothstep, so ramp by hand.)
            float k = Mathf.InverseLerp(0.05f, 0.6f, light);
            _cloudMat.color = Color.Lerp(new Color(0.42f, 0.45f, 0.52f), day, k * k * (3f - 2f * k));

            UpdateJumper(dt, light);

            // Fireflies after dusk, mostly along the banks.
            var fe = _fireflies.emission;
            fe.rateOverTime = light < 0.25f ? 22f : 0f;

            // Rain follows the camera; drops ring the water near it.
            if (_cam != null) _rain.transform.position = _cam.position + _cam.forward * 8f + Vector3.up * 12f;
            var re = _rain.emission;
            re.rateOverTime = wet * 1100f;
            if (_water != null && wet > 0.05f)
            {
                _rainRippleAcc += wet * 14f * dt;
                while (_rainRippleAcc >= 1f)
                {
                    _rainRippleAcc -= 1f;
                    var p = new Vector3(Random.Range(-12f, 12f), 0f, Random.Range(3f, 26f));
                    if (Game.DepthAtWorld(p.x, p.z) > 0.1f) _water.Ripple(p, 0.08f);
                }
            }
        }

        private void UpdateJumper(float dt, float light)
        {
            if (_jumpT < 0f)
            {
                _nextJump -= dt;
                if (_nextJump > 0f || _water == null || Game.Species == null) return;
                _nextJump = Random.Range(7f, 18f) * (light < 0.3f ? 0.6f : 1f);   // fish rise more at dusk

                // A small fish somewhere out in the open water.
                Vector3 p = new Vector3(Random.Range(-14f, 14f), 0f, Random.Range(8f, 30f));
                if (Game.DepthAtWorld(p.x, p.z) < 0.6f) return;
                var pool = new[] { "tilapia", "lampam", "jelawat", "sebarau" };
                var sp = Game.Species.Get(pool[Random.Range(0, pool.Length)]);
                if (sp == null) return;
                _jumperFilter.sharedMesh = FishMeshGen.For(sp);
                _jumper.localScale = Vector3.one * Random.Range(0.18f, 0.32f);
                float dir = Random.Range(0f, 360f);
                Vector3 step = Quaternion.Euler(0, dir, 0) * Vector3.forward * Random.Range(0.8f, 1.4f);
                _jumpFrom = p - step * 0.5f;
                _jumpTo = p + step * 0.5f;
                _jumpT = 0f;
                _jumper.gameObject.SetActive(true);
                _water.Ripple(_jumpFrom, 0.35f);
                PlayAt("splash", _jumpFrom, 0.35f);
            }
            else
            {
                const float Dur = 0.55f;
                _jumpT += dt / Dur;
                float k = Mathf.Clamp01(_jumpT);
                Vector3 pos = Vector3.Lerp(_jumpFrom, _jumpTo, k) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * 0.7f);
                // Nose leads: tilt up on the way out, down on the way in. The mesh's
                // tail is +z, so it looks back along the path.
                Vector3 along = (_jumpTo - _jumpFrom).normalized;
                Vector3 vel = along + Vector3.up * Mathf.Cos(k * Mathf.PI) * 1.6f;
                _jumper.position = pos;
                _jumper.rotation = Quaternion.LookRotation(-vel.normalized, Vector3.up);
                if (_jumpT >= 1f)
                {
                    _jumper.gameObject.SetActive(false);
                    _water.Ripple(_jumpTo, 0.45f);
                    PlayAt("splash", _jumpTo, 0.45f);
                    _jumpT = -1f;
                }
            }
        }

        private void PlayAt(string clip, Vector3 at, float vol)
        {
            if (_audio == null) return;
            float d = _cam != null ? Vector3.Distance(_cam.position, at) : 20f;
            _audio.Play(clip, vol * Mathf.Clamp01(12f / Mathf.Max(d, 1f)), 0.2f);
        }

        private void OnDestroy()
        {
            foreach (var m in _meshes) if (m != null) Destroy(m);
            if (_mat != null) Destroy(_mat);
            if (_cloudMat != null) Destroy(_cloudMat);
            if (_fishMat != null) Destroy(_fishMat);
            if (_glowMat != null) Destroy(_glowMat);
            if (_glowTex != null) Destroy(_glowTex);
        }
    }
}

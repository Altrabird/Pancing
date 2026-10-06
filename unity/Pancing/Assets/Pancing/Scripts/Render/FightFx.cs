using UnityEngine;
using Pancing.Audio;
using Pancing.Core;
using Pancing.Sim;

namespace Pancing.Render
{
    /// <summary>
    /// The drama of the fight, layered on top of what the simulation already
    /// decides — nothing here feeds back into it:
    ///   - spray and rings where a near-surface fish thrashes, surges or runs;
    ///   - spray off the line where it cuts the water while the fish takes line;
    ///   - a jump: a burst of water, a zoom punch, and a beat of slow motion;
    ///   - the landing: the fish is hauled out of the water in an arc to the
    ///     angler, who holds it up, kicking, while the camera swings in close.
    /// </summary>
    public sealed class FightFx : MonoBehaviour
    {
        private TackleView _tackle;
        private WaterSurface _water;
        private CameraRig _camera;
        private AudioService _audio;
        private AnglerView _angler;
        private ParticleSystem _spray;
        private Material _sprayMat, _haulMat;
        private Texture2D _dot;

        private float _ringTimer;
        private float _slowUntil = -1f;

        // last-known fish, kept so the landing can start from where it was
        private bool _hadFish;
        private Species _lastSpecies;
        private float _lastLengthM;
        private Vector3 _lastFishPos;

        private Transform _haul;
        private MeshFilter _haulFilter;
        private float _haulT = -1f;
        private Vector3 _haulFrom;
        private const float HaulDur = 0.75f, HoldDur = 2.9f;
        private static readonly Vector3 HoldPos = new Vector3(0.42f, 1.52f, 0.12f);   // within arm's reach

        public static FightFx Create(Transform parent, TackleView tackle, WaterSurface water, CameraRig cam,
                                     AudioService audio, AnglerView angler)
        {
            var go = new GameObject("FightFx");
            go.transform.SetParent(parent, false);
            var fx = go.AddComponent<FightFx>();
            fx._tackle = tackle; fx._water = water; fx._camera = cam; fx._audio = audio; fx._angler = angler;
            fx.Build();
            return fx;
        }

        private void Build()
        {
            _dot = SoftDot(32);
            _sprayMat = new Material(Shader.Find("Sprites/Default")) { name = "Spray", mainTexture = _dot };

            var go = new GameObject("Spray");
            go.transform.SetParent(transform, false);
            _spray = go.AddComponent<ParticleSystem>();
            _spray.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = _spray.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 1.0f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 4.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.16f);
            main.startColor = new Color(0.92f, 0.96f, 1f, 0.85f);
            main.gravityModifier = 1.3f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 900;
            main.playOnAwake = false;
            var em = _spray.emission;
            em.rateOverTime = 0f;
            var shape = _spray.shape;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = 0.25f;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            var col = _spray.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = _sprayMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _spray.Play();

            _haulMat = new Material(Shader.Find("Pancing/Fish") ?? Shader.Find("Pancing/VertexLit")) { name = "Haul" };
            _haul = new GameObject("HauledFish").transform;
            _haul.SetParent(transform, false);
            _haulFilter = _haul.gameObject.AddComponent<MeshFilter>();
            _haul.gameObject.AddComponent<MeshRenderer>().sharedMaterial = _haulMat;
            _haul.gameObject.SetActive(false);

            var bus = Game.Bus;
            bus.On(EV.FishJump, _ => OnJump());
            bus.On<CatchCard>(EV.Landed, card => { if (card != null && !card.Lost) BeginHaul(); });
        }

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

        private void Burst(Vector3 at, int count, float speedMul = 1f)
        {
            var p = new ParticleSystem.EmitParams
            {
                position = new Vector3(at.x, Mathf.Max(at.y, 0f) + 0.05f, at.z),
                applyShapeToPosition = true,
            };
            for (int i = 0; i < count; i++)
            {
                Vector3 dir = Random.insideUnitSphere;
                dir.y = Mathf.Abs(dir.y) * 1.8f + 0.6f;
                p.velocity = dir.normalized * Random.Range(1.5f, 4.5f) * speedMul;
                _spray.Emit(p, 1);
            }
        }

        /* --- events --------------------------------------------------------------- */

        private void OnJump()
        {
            Vector3 at = _tackle != null ? _tackle.FishPos : Vector3.zero;
            Burst(at, 70, 1.3f);
            _water?.Ripple(at, 0.8f);
            _camera?.Kick(9f);
            // A beat of slow motion. The sim slows with it (it runs on scaled dt),
            // so the player gets the same extra half-second to react, not less.
            _slowUntil = Time.unscaledTime + 0.38f;
            Time.timeScale = 0.4f;
        }

        private void BeginHaul()
        {
            if (!_hadFish || _lastSpecies == null) return;
            _haulFilter.sharedMesh = FishMeshGen.For(_lastSpecies);
            _haul.localScale = Vector3.one * Mathf.Clamp(_lastLengthM, 0.12f, 1.2f);
            _haulFrom = new Vector3(_lastFishPos.x, 0f, _lastFishPos.z);
            _haulT = 0f;
            _haul.gameObject.SetActive(true);
            _haulMat.SetFloat("_SwimAmp", FishMeshGen.Swims(_lastSpecies) ? 0.12f : 0f);
            _haulMat.SetFloat("_SwimFreq", 20f);
            Burst(_haulFrom, 90, 1.1f);
            _water?.Ripple(_haulFrom, 1f);
            _audio?.Play("splash", 1f, 0.05f);
            _camera?.CloseUp(HoldPos, HaulDur + HoldDur - 0.4f);
            _camera?.Kick(6f);
        }

        /* --- per frame ------------------------------------------------------------ */

        public void Apply(in FishingGame.Telemetry tm, float dt)
        {
            if (_slowUntil > 0f && Time.unscaledTime > _slowUntil)
            {
                _slowUntil = -1f;
                Time.timeScale = 1f;
            }

            if (tm.Fish.HasValue)
            {
                var f = tm.Fish.Value;
                _hadFish = true;
                _lastSpecies = f.Species;
                _lastLengthM = (float)f.LengthCm / 100f;
                _lastFishPos = _tackle != null ? _tackle.FishPos : _lastFishPos;
                FightSpray(f, tm, dt);
            }
            else if (tm.Phase != GameState.Fight)
            {
                // keep the last fish until the haul has used it
                if (_haulT < 0f) _hadFish = false;
            }

            UpdateHaul();
        }

        private void FightSpray(in HookedFish.Telemetry f, in FishingGame.Telemetry tm, float dt)
        {
            Vector3 at = _lastFishPos;
            float shallow = Mathf.Clamp01(1f - (float)f.Depth / 0.9f);
            float effort = f.State == FightState.Thrash ? 1f
                         : f.State == FightState.Surge ? 0.8f
                         : f.State == FightState.Run ? 0.45f
                         : f.State == FightState.Beaten ? 0.05f : 0.2f;
            float size = Mathf.Clamp((float)f.LengthCm / 40f, 0.5f, 2.5f);

            // White water where it fights at the top.
            float rate = shallow * effort * 60f * size;
            int n = Mathf.FloorToInt(rate * dt + Random.value);
            if (n > 0) Burst(at, n, 0.55f + effort * 0.5f);

            _ringTimer -= dt;
            if (_ringTimer <= 0f && shallow > 0.2f && effort > 0.3f)
            {
                _ringTimer = Mathf.Lerp(0.5f, 0.15f, effort);
                _water?.Ripple(at, 0.15f + 0.35f * effort * size * 0.5f);
            }

            // Line ripping out: spray where the line enters the water.
            if (f.VelAway > 0.6 && _tackle != null)
            {
                Vector3 tip = _tackle.RodTip;
                float t = tip.y / Mathf.Max(tip.y - Mathf.Min(at.y, 0f), 0.01f);
                Vector3 entry = Vector3.Lerp(tip, at, Mathf.Clamp01(t));
                if (Random.value < Mathf.Clamp01((float)f.VelAway - 0.6f) * 0.8f) Burst(entry, 2, 0.4f);
            }
        }

        private void UpdateHaul()
        {
            if (_haulT < 0f) return;
            _haulT += Time.unscaledDeltaTime;
            float tw = Time.unscaledTime;
            if (_haulT < HaulDur)
            {
                // Out of the water in an arc, nose first, toward the angler's hand.
                float k = _haulT / HaulDur;
                float e = 1f - (1f - k) * (1f - k);
                Vector3 p = Vector3.Lerp(_haulFrom, HoldPos, e) + Vector3.up * Mathf.Sin(k * Mathf.PI) * 1.4f;
                Vector3 vel = (HoldPos - _haulFrom).normalized + Vector3.up * Mathf.Cos(k * Mathf.PI) * 1.2f;
                _haul.position = p;
                _haul.rotation = Quaternion.LookRotation(-vel.normalized, Vector3.up) * Quaternion.Euler(0, 0, Mathf.Sin(tw * 25f) * 25f);
                if (Random.value < 0.5f) Burst(p, 1, 0.3f);   // drips off it
            }
            else if (_haulT < HaulDur + HoldDur)
            {
                // Held up by the mouth, side-on to the camera, kicking now and then.
                _angler?.SetHold(HoldPos + Vector3.down * 0.03f);
                float kick = Mathf.Max(0f, Mathf.Sin(tw * 3.1f)) * Mathf.Sin(tw * 28f) * 18f;
                _haul.position = HoldPos + Vector3.up * Mathf.Sin(tw * 2f) * 0.02f;
                // snout up at the hand, body hanging below; tail (+z) points down
                _haul.rotation = Quaternion.LookRotation(Vector3.down, Vector3.right) * Quaternion.Euler(0, kick, 0);
                if (Random.value < 0.15f) Burst(_haul.position + Vector3.down * _haul.localScale.x * 0.8f, 1, 0.2f);
            }
            else
            {
                _haul.gameObject.SetActive(false);
                _angler?.SetHold(null);
                _haulT = -1f;
                _hadFish = false;
            }
        }

        private void OnDestroy()
        {
            if (Time.timeScale != 1f) Time.timeScale = 1f;
            if (_sprayMat != null) Destroy(_sprayMat);
            if (_haulMat != null) Destroy(_haulMat);
            if (_dot != null) Destroy(_dot);
        }
    }
}

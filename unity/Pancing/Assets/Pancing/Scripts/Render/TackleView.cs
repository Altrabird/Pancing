using UnityEngine;
using Pancing.Core;
using Pancing.Sim;

namespace Pancing.Render
{
    /// <summary>
    /// Everything the player's tackle looks like: the rod and its bend, the line
    /// and its sag, the lure on the surface, and the hooked fish.
    ///
    /// The rod bend and the line sag are not animations. They are read straight
    /// off the solver — bend is the tension solved through the blank's taper, sag
    /// is what the tension looks like. That is deliberate: the player should be
    /// able to read the physics off the screen before they read the meter, and a
    /// decorative wobble would actively lie to them at the moment it matters most.
    /// </summary>
    public sealed class TackleView : MonoBehaviour
    {
        private const int RodSegments = 12;
        private const int LineSegments = 20;

        private LineRenderer _rod;
        private LineRenderer _line;
        private Transform _lure;
        private Transform _fishRoot;
        private MeshFilter _fishFilter;
        private MeshRenderer _fishRenderer;
        private Material _fishMat;
        private Material _lineMat, _rodMat;

        private Species _fishSpecies;
        private Material _lureMat;
        private Transform _cam;
        private float _floatDip;
        private readonly Vector3[] _rodPoints = new Vector3[RodSegments + 1];
        private readonly Vector3[] _linePoints = new Vector3[LineSegments + 1];

        /// <summary>Where the line leaves the rod. Read by the camera and the FX.</summary>
        public Vector3 RodTip { get; private set; }
        public Vector3 LurePos { get; private set; }

        public static TackleView Create(Transform parent)
        {
            var go = new GameObject("Tackle");
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TackleView>();
            t.Build();
            return t;
        }

        private void Build()
        {
            var vcShader = Shader.Find("Pancing/VertexLit") ?? Shader.Find("Legacy Shaders/Diffuse");
            var unlit = Shader.Find("Unlit/Color") ?? vcShader;

            _rodMat = new Material(vcShader) { name = "RodMaterial" };
            _lineMat = new Material(unlit) { name = "LineMaterial" };
            _lineMat.color = new Color(0.92f, 0.94f, 0.96f, 1f);
            _fishMat = new Material(vcShader) { name = "FishMaterial" };

            _rod = MakeLine("Rod", _rodMat, RodSegments + 1, 0.035f, 0.010f);
            _line = MakeLine("Line", _lineMat, LineSegments + 1, 0.014f, 0.014f);

            // The lure is a simple bright marker — the player has to be able to find
            // it at 30 m against moving water, and a detailed model at that distance
            // is two pixels of nothing.
            _lure = GameObject.CreatePrimitive(PrimitiveType.Sphere).transform;
            _lure.name = "Lure";
            _lure.SetParent(transform, false);
            _lure.localScale = Vector3.one * 0.22f;
            var lureRenderer = _lure.GetComponent<MeshRenderer>();
            _lureMat = new Material(unlit) { color = new Color(1f, 0.45f, 0.12f) };
            lureRenderer.sharedMaterial = _lureMat;
            lureRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Destroy(_lure.GetComponent<Collider>());

            _fishRoot = new GameObject("HookedFish").transform;
            _fishRoot.SetParent(transform, false);
            _fishFilter = _fishRoot.gameObject.AddComponent<MeshFilter>();
            _fishRenderer = _fishRoot.gameObject.AddComponent<MeshRenderer>();
            _fishRenderer.sharedMaterial = _fishMat;
            _fishRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _fishRoot.gameObject.SetActive(false);
        }

        private LineRenderer MakeLine(string name, Material mat, int count, float startW, float endW)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = count;
            lr.startWidth = startW;
            lr.endWidth = endW;
            lr.numCapVertices = 2;
            lr.sharedMaterial = mat;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.alignment = LineAlignment.View;
            return lr;
        }

        /// <summary>
        /// Update everything from one telemetry snapshot. Pure consumer: nothing in
        /// here writes back into the simulation.
        /// </summary>
        public void Apply(in FishingGame.Telemetry tm, FishingGame game, WaterSurface water)
        {
            var gear = tm.Gear;
            float rodLen = (float)gear.Rod.Length;
            float bend = (float)tm.Rod.Bend;

            // --- rod --------------------------------------------------------
            // The blank is anchored at the angler's hands and aimed where the cast
            // is aimed, so the whole rig swings with the aim instead of the line
            // detaching from a rod that never moved.
            float yaw = (float)game.Cast.AimYaw;
            Vector3 butt = new Vector3(0f, 1.05f, 0.15f);
            Quaternion aim = Quaternion.Euler(0f, yaw * Mathf.Rad2Deg, 0f);
            Vector3 forward = aim * Vector3.forward;
            Vector3 up = Vector3.up;

            // Deflection concentrated toward the tip — the classic fast-action curve.
            float k = bend * 1.35f;
            for (int i = 0; i <= RodSegments; i++)
            {
                float t = i / (float)RodSegments;
                float droop = k * Mathf.Pow(t, 2.35f);
                float along = rodLen * t * (1f - 0.10f * droop * droop);
                float drop = droop * rodLen * 0.30f;
                // The rod is held up at about 40 degrees, and bends down from there.
                Vector3 dir = Quaternion.AngleAxis(-42f, aim * Vector3.right) * forward;
                _rodPoints[i] = butt + dir * along - up * drop;
            }
            _rod.SetPositions(_rodPoints);
            RodTip = _rodPoints[RodSegments];

            // Rod colour from the equipped blank, darkening as it loads up — a
            // free readout of how hard the rod is working.
            Color rodCol = ProcNoise.HexToColor(Game.Gear?.Get(gear.Rod.Id)?.TipColor ?? "#4c5560");
            _rodMat.color = ProcNoise.Shade(rodCol, -0.25f * bend);

            // --- lure and line ----------------------------------------------
            bool inWater = tm.Phase == GameState.Fishing || tm.Phase == GameState.Fight
                        || tm.Phase == GameState.Sinking;
            bool flying = tm.Phase == GameState.Flying;

            Vector3 lurePos;
            if (flying)
            {
                lurePos = new Vector3((float)game.Cast.Pos.X, (float)game.Cast.Pos.Y, (float)game.Cast.Pos.Z);
            }
            else if (inWater)
            {
                float x = (float)game.Cast.Pos.X;
                float z = (float)game.Cast.Pos.Z;
                // Sit the float on the real surface, not on y = 0, or it hovers over
                // every trough.
                float surface = water != null ? water.HeightAt(x, z) : 0f;

                // THE FLOAT IS THE TELL.
                //
                // In real fishing you watch the float, not a readout. The first
                // version left it completely inert and announced the hookset window
                // as text in the top corner — the most time-critical event in the
                // game, signalled where nobody is looking, with no sound. So the
                // float now twitches on every nibble and is pulled under when the
                // window opens, which is both the natural cue and the thing that
                // teaches the difference between the two.
                float dip = 0f;
                if (tm.Bite.State == BiteState.Committed)
                {
                    dip = 0.40f + Mathf.Sin(Time.time * 20f) * 0.05f;   // under and held
                }
                else if (tm.Bite.Tapping)
                {
                    dip = 0.15f;                                        // a knock
                }
                // Snappy going down, so a 320 ms window still reads.
                _floatDip = Mathf.Lerp(_floatDip, dip, 1f - Mathf.Exp(-20f * Time.deltaTime));

                lurePos = new Vector3(x, surface - (float)tm.LureDepth * 0.12f - _floatDip, z);
            }
            else
            {
                lurePos = RodTip;
            }

            LurePos = lurePos;
            _lure.position = lurePos;
            _lure.gameObject.SetActive(flying || inWater);

            // A 22 cm ball 30 m away is two pixels. Scale it with camera distance so
            // the float stays a thing you can actually read at the far end of a cast.
            if (_cam == null && Camera.main != null) _cam = Camera.main.transform;
            if (_cam != null)
            {
                float d = Vector3.Distance(_cam.position, lurePos);
                _lure.localScale = Vector3.one * Mathf.Clamp(0.20f * d / 7f, 0.20f, 0.70f);
            }

            // Orange while it waits, hot amber the moment the window is open —
            // a second cue on the same object, for anyone who reads colour faster
            // than motion.
            if (_lureMat != null)
            {
                bool open = tm.Bite.State == BiteState.Committed;
                _lureMat.color = open
                    ? Color.Lerp(new Color(1f, 0.80f, 0.15f), Color.white,
                                 Mathf.PingPong(Time.time * 8f, 1f) * 0.5f)
                    : new Color(1f, 0.45f, 0.12f);
            }

            bool showLine = flying || inWater;
            _line.enabled = showLine;
            if (showLine)
            {
                BuildCatenary(RodTip, lurePos, (float)tm.Rod.Tension);
                _line.SetPositions(_linePoints);
                // Line goes taut and pale under load, slack and dim when it is not
                // doing anything.
                float load = Mathf.Clamp01((float)tm.Rod.LoadFrac);
                _lineMat.color = Color.Lerp(new Color(0.75f, 0.79f, 0.82f), Color.white, load);
            }

            // --- hooked fish --------------------------------------------------
            if (tm.Fish.HasValue)
            {
                ShowFish(tm.Fish.Value, water);
            }
            else if (_fishRoot.gameObject.activeSelf)
            {
                _fishRoot.gameObject.SetActive(false);
                _fishSpecies = null;
            }
        }

        /// <summary>
        /// Sag from tension. A taut line is a straight line; a slack one hangs.
        /// Rather than simulating a rope we solve the sag depth from the tension
        /// directly — sag is what tension *looks like*.
        /// </summary>
        private void BuildCatenary(Vector3 from, Vector3 to, float tension)
        {
            Vector3 d = to - from;
            float span = Mathf.Max(new Vector2(d.x, d.z).magnitude, 0.0001f);
            // Falls off as 1/T, capped so a dead-slack line does not fall to infinity.
            float sag = Mathf.Min(span * 0.42f, (span * span) / (8f * Mathf.Max(tension, 0.55f)));

            for (int i = 0; i <= LineSegments; i++)
            {
                float t = i / (float)LineSegments;
                float droop = 4f * sag * t * (1f - t);   // parabolic approximation of cosh
                _linePoints[i] = from + d * t - Vector3.up * droop;
            }
        }

        private void ShowFish(in HookedFish.Telemetry fish, WaterSurface water)
        {
            if (fish.Species == null) return;

            if (_fishSpecies != fish.Species)
            {
                _fishSpecies = fish.Species;
                _fishFilter.sharedMesh = FishMeshGen.For(fish.Species);
            }
            if (!_fishRoot.gameObject.activeSelf) _fishRoot.gameObject.SetActive(true);

            // The mesh is authored one unit long, so the individual's real length in
            // centimetres is the scale. A 78 cm Kelah is genuinely five times the
            // Tilapia beside it in the record book.
            float lengthM = (float)fish.LengthCm / 100f;
            _fishRoot.localScale = Vector3.one * lengthM;

            float x = (float)fish.Lateral;
            float z = (float)fish.Dist;
            float surface = water != null ? water.HeightAt(x, z) : 0f;
            float airborne = (float)fish.Airborne;
            // Depth pulls it under; Airborne throws it clear during a jump.
            float y = surface - (float)fish.Depth * 0.55f + airborne * 1.4f;

            _fishRoot.position = new Vector3(x, y, z);

            // Face away from the angler while it is running, and roll onto its side
            // once it is beaten — the visual tell that the fight is over.
            Vector3 lookDir = new Vector3(x, 0f, z).normalized;
            if (lookDir.sqrMagnitude < 0.01f) lookDir = Vector3.forward;
            float beaten = fish.State == FightState.Beaten ? 1f : 0f;
            float thrash = fish.State == FightState.Thrash
                ? Mathf.Sin(Time.time * 17f) * 22f
                : Mathf.Sin(Time.time * 3.2f) * 5f;

            var look = Quaternion.LookRotation(lookDir, Vector3.up);
            _fishRoot.rotation = look
                * Quaternion.Euler(airborne * -35f, thrash, Mathf.Lerp(0f, 78f, beaten));
        }

        private void OnDestroy()
        {
            if (_rodMat != null) Destroy(_rodMat);
            if (_lineMat != null) Destroy(_lineMat);
            if (_fishMat != null) Destroy(_fishMat);
            if (_lureMat != null) Destroy(_lureMat);
        }
    }
}

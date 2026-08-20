using UnityEngine;
using Pancing.Core;
using Pancing.Sim;

namespace Pancing.Render
{
    /// <summary>
    /// The aiming arrow: where the cast is pointed and how far it will go.
    ///
    /// Drawn on the water rather than as a HUD widget, because "where will it land"
    /// is a question about a place in the world and the answer belongs in that
    /// place. It hugs the surface, so it rides the waves and reads as being on the
    /// lake instead of floating above it.
    ///
    /// The length is not a guess. CastSystem.PredictDistance integrates the same
    /// ballistic step the real flight uses, so the arrowhead sits exactly where a
    /// clean release would splash down. What it deliberately does NOT show is the
    /// accuracy spread — release badly or overload the cast and the real lure lands
    /// somewhere around this line, not on it. Drawing the risk away would remove
    /// the decision the charge meter exists to pose.
    /// </summary>
    public sealed class AimArrow : MonoBehaviour
    {
        private const int ShaftPoints = 26;
        private const int RingPoints = 26;
        /// <summary>Where the shaft starts, so it does not sprout from the angler's shins.</summary>
        private const float NearOffset = 2.2f;

        private LineRenderer _shaft;
        private LineRenderer _head;
        private LineRenderer _ring;
        private Material _mat;

        private readonly Vector3[] _shaftPts = new Vector3[ShaftPoints];
        private readonly Vector3[] _headPts = new Vector3[3];
        private readonly Vector3[] _ringPts = new Vector3[RingPoints];

        private static readonly Color Idle = new Color(0.60f, 0.80f, 0.90f, 0.55f);
        private static readonly Color Charging = new Color(0.65f, 0.88f, 0.95f, 0.90f);
        private static readonly Color Sweet = new Color(0.45f, 0.98f, 0.55f, 1f);
        private static readonly Color Over = new Color(0.98f, 0.38f, 0.26f, 1f);

        public static AimArrow Create(Transform parent)
        {
            var go = new GameObject("AimArrow");
            go.transform.SetParent(parent, false);
            var a = go.AddComponent<AimArrow>();
            a.Build();
            return a;
        }

        private void Build()
        {
            var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            _mat = new Material(shader) { name = "AimArrowMaterial" };

            _shaft = MakeLine("Shaft", ShaftPoints, 0.16f, false);
            _head = MakeLine("Head", 3, 0.34f, false);
            _ring = MakeLine("Ring", RingPoints, 0.10f, true);
        }

        private LineRenderer MakeLine(string name, int count, float width, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = count;
            lr.widthMultiplier = width;
            lr.loop = loop;
            lr.numCapVertices = 2;
            lr.sharedMaterial = _mat;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.alignment = LineAlignment.View;
            return lr;
        }

        public void Apply(in FishingGame.Telemetry tm, FishingGame game, float aimYaw, WaterSurface water)
        {
            // Only while you can still act on it: lining up, or winding up.
            bool show = tm.Phase == GameState.Ready || tm.Phase == GameState.Charging;
            if (_shaft.enabled != show)
            {
                _shaft.enabled = show; _head.enabled = show; _ring.enabled = show;
            }
            if (!show) return;

            bool charging = tm.Cast.Charging;
            double charge = charging ? tm.Cast.Value : 1.0;
            double overload = charging ? tm.Cast.Overload : 0.0;

            float distance = (float)CastSystem.PredictDistance(
                charge, overload, tm.Gear, game.Cast.AimPitch, game.TipPos.Y);

            // At rest, show a short dim stub: enough to aim with, without claiming a
            // distance you have not committed to yet.
            if (!charging) distance = Mathf.Min(distance * 0.34f, 11f);
            distance = Mathf.Max(distance, NearOffset + 1.2f);

            Vector3 dir = new Vector3(Mathf.Sin(aimYaw), 0f, Mathf.Cos(aimYaw));
            Vector3 side = new Vector3(dir.z, 0f, -dir.x);

            // --- shaft, riding the surface ---------------------------------
            for (int i = 0; i < ShaftPoints; i++)
            {
                float t = i / (float)(ShaftPoints - 1);
                float d = Mathf.Lerp(NearOffset, distance, t);
                _shaftPts[i] = OnWater(dir * d, water);
            }
            _shaft.SetPositions(_shaftPts);

            // --- arrowhead --------------------------------------------------
            float barb = Mathf.Clamp(distance * 0.055f, 0.6f, 1.5f);
            Vector3 tip = OnWater(dir * distance, water);
            _headPts[0] = OnWater(dir * (distance - barb) + side * barb * 0.75f, water);
            _headPts[1] = tip;
            _headPts[2] = OnWater(dir * (distance - barb) - side * barb * 0.75f, water);
            _head.SetPositions(_headPts);

            // --- landing ring ------------------------------------------------
            // Its radius is the accuracy spread, so a clean release draws a tight
            // ring and an overloaded one draws a wide, obviously untrustworthy
            // circle. The uncertainty is the point.
            float spread = 0.030f + (1f - (float)tm.Cast.Value) * 0.085f + (float)overload * 0.10f;
            float radius = Mathf.Clamp(distance * spread * 1.6f, 0.5f, 6f);
            for (int i = 0; i < RingPoints; i++)
            {
                float a = i / (float)RingPoints * Mathf.PI * 2f;
                Vector3 p = dir * distance + side * Mathf.Cos(a) * radius + dir * Mathf.Sin(a) * radius;
                _ringPts[i] = OnWater(p, water);
            }
            _ring.SetPositions(_ringPts);

            // --- colour -------------------------------------------------------
            Color col;
            if (!charging) col = Idle;
            else if (overload > 0.001) col = Color.Lerp(Over, Color.white, Mathf.PingPong(Time.time * 6f, 1f) * 0.35f);
            else if (tm.Cast.InSweetSpot) col = Sweet;
            else col = Charging;

            _mat.color = col;
        }

        /// <summary>Lift a point onto the water surface, a hair above it so the line
        /// does not z-fight with the waves it is lying on.</summary>
        private static Vector3 OnWater(Vector3 flat, WaterSurface water)
        {
            float y = water != null ? water.HeightAt(flat.x, flat.z) : 0f;
            return new Vector3(flat.x, y + 0.06f, flat.z);
        }

        private void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
        }
    }
}

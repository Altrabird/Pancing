using UnityEngine;
using Pancing.Sim;

namespace Pancing.Render
{
    /// <summary>
    /// The camera. Over the angler's shoulder at rest, swinging with the aim, and
    /// pulling in toward the fight when there is one.
    ///
    /// Everything is damped toward a target rather than snapped, and the damping
    /// is frame-rate independent, because the one thing a fishing camera must never
    /// do is jitter while the player is reading a tension needle.
    /// </summary>
    public sealed class CameraRig : MonoBehaviour
    {
        private Camera _cam;
        private Vector3 _pos;
        private Quaternion _rot;
        private float _shake;

        public Camera Camera => _cam;

        public static CameraRig Create(Transform parent)
        {
            var go = new GameObject("MainCamera");
            go.tag = "MainCamera";
            go.transform.SetParent(parent, false);
            var rig = go.AddComponent<CameraRig>();
            rig.Setup();
            return rig;
        }

        private void Setup()
        {
            _cam = gameObject.AddComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.55f, 0.70f, 0.82f);
            _cam.fieldOfView = 58f;
            _cam.nearClipPlane = 0.08f;
            _cam.farClipPlane = 600f;
            gameObject.AddComponent<AudioListener>();

            _pos = new Vector3(0f, 3.0f, -3.6f);
            _rot = Quaternion.Euler(11f, 0f, 0f);
            transform.SetPositionAndRotation(_pos, _rot);
        }

        /// <summary>Kick the camera — a snapped line, a fish breaking the surface.</summary>
        public void Shake(float amount) => _shake = Mathf.Max(_shake, Mathf.Clamp01(amount));

        public void Apply(in FishingGame.Telemetry tm, float aimYaw, Vector3 lurePos, float dt)
        {
            // The camera lives BEHIND THE ANGLER, who stands at the origin — never
            // out in the lake. Anchoring it to the focus point instead put it four
            // metres offshore looking down at its own feet, which filled the frame
            // with water and cut the horizon off entirely.
            Vector3 forward = new Vector3(Mathf.Sin(aimYaw), 0f, Mathf.Cos(aimYaw));

            // What the camera looks AT is separate from where it stands: whatever
            // currently matters, biased far enough out to keep sky in the frame.
            Vector3 focus;
            float distance, height, aimHeight;

            if (tm.Fish.HasValue)
            {
                var fish = tm.Fish.Value;
                focus = new Vector3((float)fish.Lateral, 0.3f, (float)fish.Dist);
                // Close in as the fish comes in, so landing it feels like an arrival.
                float t = Mathf.InverseLerp(1.5f, 22f, (float)fish.Dist);
                distance = Mathf.Lerp(3.6f, 5.2f, t);
                height = Mathf.Lerp(2.4f, 3.0f, t);
                aimHeight = 1.1f;
            }
            else if (tm.Phase == GameState.Flying || tm.Phase == GameState.Sinking
                  || tm.Phase == GameState.Fishing)
            {
                focus = new Vector3(lurePos.x, 0.3f, Mathf.Max(lurePos.z, 8f));
                distance = 4.8f;
                height = 2.9f;
                aimHeight = 1.3f;
            }
            else
            {
                // At rest, look far out along the aim so the player can see where the
                // cast is pointed — and so the shot is mostly sky and far bank
                // rather than the water immediately in front of them.
                focus = forward * 24f + Vector3.up * 0.3f;
                distance = 4.6f;
                height = 2.8f;
                aimHeight = 1.6f;
            }

            // Charging pulls the camera back and drops it, which reads as winding up.
            if (tm.Phase == GameState.Charging)
            {
                float charge = (float)tm.Cast.Value + (float)tm.Cast.Overload;
                distance += charge * 1.1f;
                height -= charge * 0.25f;
            }

            Vector3 targetPos = -forward * distance + Vector3.up * height;
            // Never drop the camera under the water or into the bank.
            targetPos.y = Mathf.Max(targetPos.y, 1.6f);

            // Aim at a point lifted off the surface. Looking straight at the water
            // tips the horizon off the top of the screen; lifting the target keeps
            // roughly a third of the frame as sky, which is what makes it read as a
            // lake rather than as a texture.
            Vector3 lookTarget = focus + Vector3.up * aimHeight;
            Quaternion targetRot = Quaternion.LookRotation(lookTarget - targetPos, Vector3.up);

            // Frame-rate independent approach. The fight tracks faster, because a
            // fish that has just run 4 m should not leave frame while the camera
            // makes up its mind.
            float rate = tm.Fish.HasValue ? 5.5f : 3.0f;
            float k = 1f - Mathf.Exp(-rate * dt);
            _pos = Vector3.Lerp(_pos, targetPos, k);
            _rot = Quaternion.Slerp(_rot, targetRot, k);

            Vector3 shakeOffset = Vector3.zero;
            if (_shake > 0.001f)
            {
                shakeOffset = new Vector3(
                    (Mathf.PerlinNoise(Time.time * 34f, 0f) - 0.5f),
                    (Mathf.PerlinNoise(0f, Time.time * 31f) - 0.5f),
                    0f) * _shake * 0.35f;
                _shake = Mathf.Max(0f, _shake - dt * 2.2f);
            }

            transform.SetPositionAndRotation(_pos + _rot * shakeOffset, _rot);
        }
    }
}

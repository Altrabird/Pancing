using UnityEngine;
using Pancing.Sim;

namespace Pancing.Controls
{
    /// <summary>
    /// One input surface for three very different devices.
    ///
    /// PC and laptop get keyboard and mouse; phones and tablets get on-screen
    /// controls that the HUD pushes into the same fields. Nothing downstream knows
    /// which was used — the game reads CastHeld, Reel, DragAxis and StrikePressed
    /// and that is the whole contract.
    ///
    /// Uses the legacy Input Manager on purpose: it needs no package, no generated
    /// asset and no per-platform setup, which matters when the deliverable is an
    /// .exe and an .apk a teacher has to be able to rebuild.
    /// </summary>
    public sealed class InputService : MonoBehaviour
    {
        /// <summary>Held to charge a cast; released to throw.</summary>
        public bool CastHeld { get; private set; }
        /// <summary>0..1 retrieve.</summary>
        public float Reel { get; private set; }
        /// <summary>-1..1 clutch adjustment.</summary>
        public float DragAxis { get; private set; }
        /// <summary>True for exactly one frame.</summary>
        public bool StrikePressed { get; private set; }
        /// <summary>Radians. Where the cast will go.</summary>
        public float AimYaw { get; private set; }

        /* --- what the on-screen controls write into ------------------------ */

        public bool TouchCastHeld;
        public bool TouchReelHeld;
        public bool TouchStrike;
        public float TouchDragAxis;
        /// <summary>Set true by the HUD while a panel is open, so the world does not
        /// react to taps that were meant for a button.</summary>
        public bool Blocked;

        private const float MaxAim = 0.62f;      // radians either side of straight out
        private const float AimSpeed = 1.35f;    // radians per second on the keyboard
        /// <summary>Seconds of holding to reach full winching power.</summary>
        private const float ReelRampTime = 0.55f;
        /// <summary>Seconds to drop back to nothing. Faster than the ramp, on purpose.</summary>
        private const float ReelReleaseTime = 0.22f;

        private float _reelRamp;

        private bool _prevStrike;
        private int _aimPointerId = -1;
        private float _aimAnchorX;
        private float _aimAnchorYaw;

        public static InputService Create(Transform parent)
        {
            var go = new GameObject("Input");
            go.transform.SetParent(parent, false);
            return go.AddComponent<InputService>();
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            /* --- cast ------------------------------------------------------ */
            bool keyCast = UnityEngine.Input.GetKey(KeyCode.Space);
            // The left mouse button charges a cast, but only when the pointer is not
            // being used to aim by dragging — see below.
            bool mouseCast = UnityEngine.Input.GetMouseButton(0) && !Blocked && _aimPointerId < 0;
            CastHeld = keyCast || mouseCast || TouchCastHeld;

            /* --- strike ---------------------------------------------------- */
            bool strike = UnityEngine.Input.GetKey(KeyCode.Return)
                       || UnityEngine.Input.GetKey(KeyCode.E)
                       || TouchStrike;
            StrikePressed = strike && !_prevStrike;
            _prevStrike = strike;
            TouchStrike = false;         // consumed; the HUD re-raises it each tap

            /* --- reel ------------------------------------------------------ */
            //
            // The retrieve is ANALOG, and how long you hold the key is how hard you
            // pull. A tap is a gentle lift, a long hold winds up to full winching
            // power over about half a second.
            //
            // It used to be a straight 0 or 1, which meant the only way to apply
            // less force was to stop entirely — and "pump and wind", the technique
            // the whole fight model is built around, was not expressible. Ramping
            // makes the difference between nursing a fish and hauling on it
            // something the player performs rather than something they toggle.
            bool keyReel = UnityEngine.Input.GetKey(KeyCode.W)
                        || UnityEngine.Input.GetKey(KeyCode.UpArrow)
                        || TouchReelHeld;

            // Winding up is deliberately slower than letting go: you should be able
            // to dump the pressure the instant a fish surges.
            float rampUp = dt / ReelRampTime;
            float rampDown = dt / ReelReleaseTime;
            _reelRamp = keyReel
                ? Mathf.MoveTowards(_reelRamp, 1f, rampUp)
                : Mathf.MoveTowards(_reelRamp, 0f, rampDown);

            // Holding shift is "give it everything" — an override for anglers who
            // already know they want maximum and do not want to wait for the ramp.
            if (keyReel && (UnityEngine.Input.GetKey(KeyCode.LeftShift)
                         || UnityEngine.Input.GetKey(KeyCode.RightShift))) _reelRamp = 1f;

            Reel = _reelRamp;

            // A mouse wheel roll is a short burst, which is how most people expect
            // to "wind" without holding a key.
            if (UnityEngine.Input.mouseScrollDelta.y > 0f) Reel = Mathf.Max(Reel, 0.7f);

            /* --- drag clutch ------------------------------------------------ */
            float keyDrag = 0f;
            if (UnityEngine.Input.GetKey(KeyCode.D) || UnityEngine.Input.GetKey(KeyCode.RightArrow)) keyDrag += 1f;
            if (UnityEngine.Input.GetKey(KeyCode.A) || UnityEngine.Input.GetKey(KeyCode.LeftArrow)) keyDrag -= 1f;
            DragAxis = Mathf.Clamp(keyDrag + TouchDragAxis, -1f, 1f);

            /* --- aim -------------------------------------------------------- */
            if (UnityEngine.Input.GetKey(KeyCode.Q)) AimYaw -= AimSpeed * dt;
            if (UnityEngine.Input.GetKey(KeyCode.R)) AimYaw += AimSpeed * dt;
            UpdateTouchAim();
            AimYaw = Mathf.Clamp(AimYaw, -MaxAim, MaxAim);
        }

        /// <summary>
        /// Aim by dragging anywhere on the lower half of the screen.
        ///
        /// The lower half only, and with a movement threshold before it takes over,
        /// because the same finger also charges the cast. Without both guards every
        /// cast would drift the aim by however much the thumb wobbled while holding.
        /// </summary>
        private void UpdateTouchAim()
        {
            if (Blocked) { _aimPointerId = -1; return; }

            if (UnityEngine.Input.touchCount > 0)
            {
                for (int i = 0; i < UnityEngine.Input.touchCount; i++)
                {
                    var t = UnityEngine.Input.GetTouch(i);
                    if (t.position.y > Screen.height * 0.55f) continue;

                    if (_aimPointerId < 0 && t.phase == TouchPhase.Moved
                        && Mathf.Abs(t.position.x - t.rawPosition.x) > Screen.width * 0.04f)
                    {
                        _aimPointerId = t.fingerId;
                        _aimAnchorX = t.position.x;
                        _aimAnchorYaw = AimYaw;
                    }
                    if (t.fingerId == _aimPointerId)
                    {
                        if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                        {
                            _aimPointerId = -1;
                        }
                        else
                        {
                            float dx = (t.position.x - _aimAnchorX) / Screen.width;
                            AimYaw = _aimAnchorYaw + dx * MaxAim * 2.2f;
                        }
                    }
                }
                return;
            }

            // Desktop: right-drag aims, so the left button stays free for casting.
            if (UnityEngine.Input.GetMouseButton(1))
            {
                if (_aimPointerId != -2)
                {
                    _aimPointerId = -2;
                    _aimAnchorX = UnityEngine.Input.mousePosition.x;
                    _aimAnchorYaw = AimYaw;
                }
                float dx = (UnityEngine.Input.mousePosition.x - _aimAnchorX) / Screen.width;
                AimYaw = _aimAnchorYaw + dx * MaxAim * 2.2f;
            }
            else if (_aimPointerId == -2)
            {
                _aimPointerId = -1;
            }
        }

        /// <summary>Pack this frame's input into what the simulation expects.</summary>
        public GameInput ToGameInput() => new GameInput
        {
            ReelAxis = Reel,
            DragAxis = DragAxis,
        };
    }
}

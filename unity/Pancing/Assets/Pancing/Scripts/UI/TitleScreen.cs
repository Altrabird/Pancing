using UnityEngine;
using UnityEngine.UI;
using Pancing.Controls;
using Pancing.Core;

namespace Pancing.UI
{
    /// <summary>
    /// The front door: the game's name over the live scene, one big button, and
    /// for a returning angler where they left off. The world is already built
    /// and breathing behind it — birds, water, sound — so the first thing a
    /// player sees is the place, not a loading screen.
    ///
    /// While it is up the simulation is paused and world input is blocked, the
    /// same contract as the shop window.
    /// </summary>
    public sealed class TitleScreen : MonoBehaviour
    {
        private InputService _input;
        private CanvasGroup _group;
        private bool _leaving;
        private RectTransform _button;

        public bool IsOpen { get; private set; }

        public static TitleScreen Create(Transform parent, InputService input)
        {
            var go = new GameObject("Title");
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TitleScreen>();
            t._input = input;
            t.Build();
            return t;
        }

        private void Build()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 40;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();
            _group = gameObject.AddComponent<CanvasGroup>();

            // A vignette from the bottom so the type sits on something.
            var shade = UiKit.Box("Shade", transform, new Color(0.02f, 0.05f, 0.06f, 0.55f),
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            shade.raycastTarget = true;   // swallow clicks meant for the world
            UiKit.Box("ShadeLow", transform, new Color(0.02f, 0.05f, 0.06f, 0.45f),
                Vector2.zero, new Vector2(1, 0.45f), Vector2.zero, Vector2.zero);

            var title = UiKit.Label("Name", transform, "PANCING", 104, TextAnchor.MiddleCenter,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-500, 60), new Vector2(500, 200));
            title.fontStyle = FontStyle.Bold;
            title.color = UiKit.Gold;
            var outline = title.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0.25f, 0.14f, 0.02f, 0.9f);
            outline.effectDistance = new Vector2(3f, -3f);

            var tag = UiKit.Label("Tagline", transform, "Joran, umpan dan kesabaran — memancing di air tawar Malaysia",
                20, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(-500, 18), new Vector2(500, 56));
            tag.color = UiKit.Ink;

            var credit = UiKit.Label("Credit", transform, "Developed by Harsidi Junick", 16, TextAnchor.MiddleCenter,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-400, -4), new Vector2(400, 18));
            credit.color = UiKit.Gold;
            credit.fontStyle = FontStyle.Italic;

            var st = Game.State;
            bool returning = st != null && (st.Stats.Landed > 0 || st.Level > 1);
            string label = returning ? "SAMBUNG MEMANCING" : "MULA MEMANCING";
            var btn = UiKit.Button("Play", transform, label, 24,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-180, -82), new Vector2(180, -18),
                Begin, out _);
            btn.color = new Color(0.16f, 0.46f, 0.40f, 0.96f);
            UiKit.Rounded(btn, 32f);
            _button = (RectTransform)btn.transform;

            if (returning)
            {
                var spot = st.Spot?.Name ?? "";
                UiKit.Label("Resume", transform, $"Tahap {st.Level}  ·  RM {st.Money:0}  ·  {st.Stats.Landed} ikan  ·  {spot}",
                    16, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(-400, -122), new Vector2(400, -94)).color = UiKit.InkDim;
            }

            UiKit.Label("Keys", transform,
                "Tahan SPACE / klik: lontar   ·   E: sentap   ·   W: karau (Shift = penuh)   ·   A / D: klac   ·   Q / R: bidik   ·   M: bunyi",
                14, TextAnchor.MiddleCenter, new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                new Vector2(-560, 22), new Vector2(560, 44)).color = UiKit.InkDim;

            IsOpen = true;
            if (_input != null) _input.Blocked = true;
        }

        private void Update()
        {
            if (!IsOpen) return;
            if (_leaving)
            {
                _group.alpha = Mathf.MoveTowards(_group.alpha, 0f, Time.unscaledDeltaTime * 2.5f);
                if (_group.alpha <= 0f)
                {
                    IsOpen = false;
                    gameObject.SetActive(false);
                }
                return;
            }
            // A slow breath on the button so it reads as the thing to press.
            float s = 1f + Mathf.Sin(Time.unscaledTime * 2.4f) * 0.025f;
            _button.localScale = new Vector3(s, s, 1f);
            // Not Space: that is the cast key, and the press would carry straight
            // into charging a cast the moment the title lets go of the input.
            if (UnityEngine.Input.GetKeyDown(KeyCode.Return)) Begin();
        }

        private void Begin()
        {
            if (_leaving) return;
            _leaving = true;
            _group.blocksRaycasts = false;
            if (_input != null) _input.Blocked = false;
        }
    }
}

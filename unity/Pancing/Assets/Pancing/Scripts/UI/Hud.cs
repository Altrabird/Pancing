using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Pancing.Core;
using Pancing.Controls;
using Pancing.Sim;

namespace Pancing.UI
{
    /// <summary>
    /// The whole heads-up display, built from code — no prefabs, no scene setup.
    ///
    /// Two things here are the actual interface to the game and everything else is
    /// decoration:
    ///
    ///   The tension meter, because every decision in a fight is "is this too much
    ///   line load", and the meter has to answer that faster than the player can
    ///   think about it. Hence colour zones rather than a number, and a drag marker
    ///   sitting on the same scale so the clutch setting is legible against the
    ///   line's strength rather than in abstract newtons.
    ///
    ///   The four presentation factors, because a bite that never comes has to be
    ///   diagnosable. Without them "nothing is biting" is folklore; with them the
    ///   player can see that their lure is right, their depth is right, and their
    ///   line is too visible in clear water for a fish this cautious.
    /// </summary>
    public sealed class Hud : MonoBehaviour
    {
        private InputService _input;
        private Font _font;
        private Canvas _canvas;

        // tension
        private Image _tensionFill, _tensionBack, _dragMarker;
        private Image _integrityFill, _hookFill;
        private Text _tensionLabel, _dragLabel;

        // cast
        private RectTransform _castRow;
        private Image _castFill, _castSweet, _castOverload;

        // bite
        private RectTransform _biteRow;
        private Image _attractionFill, _windowFill;
        private Text _biteState, _windowLabel;
        private readonly Image[] _factorFills = new Image[4];
        private readonly Text[] _factorLabels = new Text[4];
        private static readonly string[] FactorNames = { "Umpan", "Dalam", "Gerak", "Senyap" };

        // top bar
        private Text _clockText, _weatherText, _moneyText, _levelText, _spotText;
        private Image _xpFill;

        // messages
        private Text _toastText;
        private CanvasGroup _toastGroup;
        private float _toastTimer;

        // catch card
        private CanvasGroup _cardGroup;
        private Text _cardTitle, _cardStats, _cardReward;
        private RawImage _cardPortrait;
        private Texture2D _cardTexture;

        // touch
        private GameObject _touchRoot;

        private static readonly Color ZoneSlack = new Color(0.45f, 0.52f, 0.58f);
        private static readonly Color ZoneGood = new Color(0.38f, 0.78f, 0.44f);
        private static readonly Color ZoneHigh = new Color(0.95f, 0.72f, 0.20f);
        private static readonly Color ZoneDanger = new Color(0.90f, 0.28f, 0.24f);
        private static readonly Color Panel = new Color(0.05f, 0.08f, 0.09f, 0.62f);
        private static readonly Color Ink = new Color(0.93f, 0.96f, 0.95f);

        public static Hud Create(Transform parent, InputService input)
        {
            var go = new GameObject("HUD");
            go.transform.SetParent(parent, false);
            var hud = go.AddComponent<Hud>();
            hud._input = input;
            hud.Build();
            return hud;
        }

        /* --- construction ------------------------------------------------------ */

        private void Build()
        {
            _font = UiKit.Resolve();
            UiKit.Font = _font;

            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            // Halfway between width- and height-matching, so the HUD survives both a
            // 21:9 laptop and a tall phone held upright.
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();

            if (FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                es.transform.SetParent(transform, false);
            }

            BuildTopBar();
            BuildTensionPanel();
            BuildCastMeter();
            BuildBitePanel();
            BuildToast();
            BuildCatchCard();
            BuildTouchControls();
        }

        /* --- widget helpers ---------------------------------------------------- */

        // These forward to UiKit so the HUD and the shop/bag panels are built from
        // one widget library rather than two that slowly diverge.

        private RectTransform Rect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
                                   Vector2 offsetMin, Vector2 offsetMax)
            => UiKit.Rect(name, parent, anchorMin, anchorMax, offsetMin, offsetMax);

        private Image Box(string name, Transform parent, Color color,
                          Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
            => UiKit.Box(name, parent, color, anchorMin, anchorMax, offsetMin, offsetMax);

        private Text Label(string name, Transform parent, string text, int size, TextAnchor align,
                           Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
            => UiKit.Label(name, parent, text, size, align, anchorMin, anchorMax, offsetMin, offsetMax);

        private Image Bar(string name, Transform parent, Color trackColor, Color fillColor,
                          Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax,
                          out Image track)
            => UiKit.Bar(name, parent, trackColor, fillColor, anchorMin, anchorMax,
                         offsetMin, offsetMax, out track);

        /* --- panels -------------------------------------------------------------- */

        private void BuildTopBar()
        {
            var bar = Box("TopBar", transform, Panel, new Vector2(0, 1), new Vector2(1, 1),
                          new Vector2(0, -46), new Vector2(0, 0));

            _clockText = Label("Clock", bar.transform, "08:36", 20, TextAnchor.MiddleLeft,
                new Vector2(0, 0), new Vector2(0, 1), new Vector2(16, 0), new Vector2(140, 0));
            _weatherText = Label("Weather", bar.transform, "Cerah", 16, TextAnchor.MiddleLeft,
                new Vector2(0, 0), new Vector2(0, 1), new Vector2(150, 0), new Vector2(340, 0));
            _spotText = Label("Spot", bar.transform, "", 16, TextAnchor.MiddleCenter,
                new Vector2(0.5f, 0), new Vector2(0.5f, 1), new Vector2(-160, 0), new Vector2(160, 0));
            _levelText = Label("Level", bar.transform, "Tahap 1", 16, TextAnchor.MiddleRight,
                new Vector2(1, 0), new Vector2(1, 1), new Vector2(-330, 0), new Vector2(-180, 0));
            _moneyText = Label("Money", bar.transform, "RM 120", 20, TextAnchor.MiddleRight,
                new Vector2(1, 0), new Vector2(1, 1), new Vector2(-170, 0), new Vector2(-16, 0));

            _xpFill = Bar("Xp", bar.transform, new Color(1, 1, 1, 0.12f), new Color(0.42f, 0.72f, 0.95f),
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, -4), new Vector2(0, 0), out _);
        }

        private void BuildTensionPanel()
        {
            // Bottom-left, big. This is the thing the player stares at.
            var panel = Box("TensionPanel", transform, Panel, new Vector2(0, 0), new Vector2(0, 0),
                            new Vector2(16, 16), new Vector2(430, 150));

            Label("TensionCap", panel.transform, "TEGANGAN", 13, TextAnchor.MiddleLeft,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(14, -26), new Vector2(-14, -6));

            _tensionFill = Bar("Tension", panel.transform, new Color(0, 0, 0, 0.45f), ZoneGood,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(14, -62), new Vector2(-14, -30), out _tensionBack);

            // The drag marker rides the same scale as the tension bar, so "my clutch
            // is set above what this line can take" is a thing you can SEE rather
            // than a number you have to convert.
            _dragMarker = Box("DragMarker", _tensionBack.transform, new Color(1f, 0.95f, 0.55f),
                new Vector2(0, 0), new Vector2(0, 1), new Vector2(-2, -3), new Vector2(2, 3));

            _tensionLabel = Label("TensionVal", panel.transform, "0 N", 15, TextAnchor.MiddleLeft,
                new Vector2(0, 1), new Vector2(0.5f, 1), new Vector2(14, -84), new Vector2(0, -64));
            _dragLabel = Label("DragVal", panel.transform, "Klac 55%", 15, TextAnchor.MiddleRight,
                new Vector2(0.5f, 1), new Vector2(1, 1), new Vector2(0, -84), new Vector2(-14, -64));

            Label("IntegrityCap", panel.transform, "Tali", 12, TextAnchor.MiddleLeft,
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(14, -106), new Vector2(60, -88));
            _integrityFill = Bar("Integrity", panel.transform, new Color(0, 0, 0, 0.45f),
                new Color(0.55f, 0.85f, 0.95f),
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(62, -104), new Vector2(-14, -90), out _);

            Label("HookCap", panel.transform, "Kail", 12, TextAnchor.MiddleLeft,
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(14, -130), new Vector2(60, -112));
            _hookFill = Bar("Hook", panel.transform, new Color(0, 0, 0, 0.45f),
                new Color(0.95f, 0.72f, 0.42f),
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(62, -128), new Vector2(-14, -114), out _);
        }

        private void BuildCastMeter()
        {
            _castRow = Rect("CastMeter", transform, new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                            new Vector2(-220, 22), new Vector2(220, 62));
            var track = _castRow.gameObject.AddComponent<Image>();
            track.color = new Color(0, 0, 0, 0.55f);
            track.raycastTarget = false;

            _castFill = Box("CastFill", _castRow, new Color(0.55f, 0.82f, 0.62f),
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            _castFill.type = Image.Type.Filled;
            _castFill.fillMethod = Image.FillMethod.Horizontal;
            _castFill.fillAmount = 0f;

            // The sweet spot is drawn as a band on the track, not as a number: the
            // release is a reflex, and a reflex needs a target you can see.
            _castSweet = Box("Sweet", _castRow, new Color(1f, 1f, 1f, 0.30f),
                new Vector2(1f - (float)CastSystem.PerfectBand / 1.0f, 0), new Vector2(1, 1),
                Vector2.zero, Vector2.zero);
            _castOverload = Box("Overload", _castRow, new Color(0.92f, 0.35f, 0.25f, 0.85f),
                new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0), new Vector2(0, 0));

            Label("CastHint", _castRow, "TAHAN untuk lontar", 13, TextAnchor.MiddleCenter,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 2), new Vector2(0, 22));

            _castRow.gameObject.SetActive(false);
        }

        private void BuildBitePanel()
        {
            _biteRow = Rect("BitePanel", transform, new Vector2(1, 0), new Vector2(1, 0),
                            new Vector2(-330, 16), new Vector2(-16, 176));
            var bg = _biteRow.gameObject.AddComponent<Image>();
            bg.color = Panel;
            bg.raycastTarget = false;

            _biteState = Label("BiteState", _biteRow, "Menunggu", 15, TextAnchor.MiddleLeft,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(14, -28), new Vector2(-14, -6));

            _attractionFill = Bar("Attraction", _biteRow, new Color(0, 0, 0, 0.45f),
                new Color(0.62f, 0.85f, 0.45f),
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(14, -50), new Vector2(-14, -32), out _);

            // The four presentation factors, stacked. Each one is a multiplier, so a
            // single short bar is enough to kill a bite outright — which is exactly
            // what the display should make obvious.
            for (int i = 0; i < 4; i++)
            {
                float top = -58 - i * 22;
                _factorLabels[i] = Label($"Factor{i}Cap", _biteRow, FactorNames[i], 12, TextAnchor.MiddleLeft,
                    new Vector2(0, 1), new Vector2(0, 1), new Vector2(14, top - 18), new Vector2(76, top));
                _factorFills[i] = Bar($"Factor{i}", _biteRow, new Color(0, 0, 0, 0.4f), Color.white,
                    new Vector2(0, 1), new Vector2(1, 1),
                    new Vector2(78, top - 16), new Vector2(-14, top - 2), out _);
            }

            // The hookset window: big, central, and impossible to miss, because a
            // Toman gives you 320 milliseconds.
            var windowRow = Rect("Window", transform, new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                                 new Vector2(-190, -150), new Vector2(190, -70));
            var wbg = windowRow.gameObject.AddComponent<Image>();
            wbg.color = new Color(0.06f, 0.09f, 0.10f, 0.80f);
            wbg.raycastTarget = false;
            _windowLabel = Label("WindowLabel", windowRow, "SENTAP!", 34, TextAnchor.MiddleCenter,
                new Vector2(0, 0.35f), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            _windowLabel.color = new Color(1f, 0.86f, 0.35f);
            _windowFill = Bar("WindowBar", windowRow, new Color(0, 0, 0, 0.5f), new Color(1f, 0.72f, 0.25f),
                new Vector2(0, 0), new Vector2(1, 0.32f), new Vector2(12, 10), new Vector2(-12, -4), out _);
            windowRow.gameObject.SetActive(false);
            _windowRow = windowRow;
        }

        private RectTransform _windowRow;

        private void BuildToast()
        {
            var rt = Rect("Toast", transform, new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                          new Vector2(-260, -108), new Vector2(260, -58));
            _toastGroup = rt.gameObject.AddComponent<CanvasGroup>();
            _toastGroup.alpha = 0f;
            _toastGroup.blocksRaycasts = false;
            var bg = rt.gameObject.AddComponent<Image>();
            bg.color = new Color(0.06f, 0.09f, 0.10f, 0.85f);
            bg.raycastTarget = false;
            _toastText = Label("ToastText", rt, "", 18, TextAnchor.MiddleCenter,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        }

        private void BuildCatchCard()
        {
            var rt = Rect("CatchCard", transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                          new Vector2(-230, -170), new Vector2(230, 170));
            _cardGroup = rt.gameObject.AddComponent<CanvasGroup>();
            _cardGroup.alpha = 0f;
            _cardGroup.blocksRaycasts = false;
            var bg = rt.gameObject.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.08f, 0.09f, 0.94f);
            bg.raycastTarget = false;

            _cardTitle = Label("CardTitle", rt, "", 26, TextAnchor.MiddleCenter,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(12, -52), new Vector2(-12, -10));

            var portrait = Rect("Portrait", rt, new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                                new Vector2(-150, -232), new Vector2(150, -60));
            _cardPortrait = portrait.gameObject.AddComponent<RawImage>();
            _cardPortrait.raycastTarget = false;

            _cardStats = Label("CardStats", rt, "", 17, TextAnchor.UpperCenter,
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(12, 76), new Vector2(-12, 128));
            _cardReward = Label("CardReward", rt, "", 17, TextAnchor.LowerCenter,
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(12, 12), new Vector2(-12, 70));
        }

        /* --- touch controls ------------------------------------------------------ */

        private void BuildTouchControls()
        {
            _touchRoot = new GameObject("TouchControls", typeof(RectTransform));
            _touchRoot.transform.SetParent(transform, false);
            var root = (RectTransform)_touchRoot.transform;
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;

            HoldButton("LONTAR", root, new Vector2(1, 0), new Vector2(-190, 190), new Vector2(150, 150),
                held => _input.TouchCastHeld = held);
            HoldButton("KARAU", root, new Vector2(1, 0), new Vector2(-190, 355), new Vector2(150, 110),
                held => _input.TouchReelHeld = held);
            TapButton("SENTAP", root, new Vector2(0, 0), new Vector2(190, 250), new Vector2(150, 130),
                () => _input.TouchStrike = true);
            HoldButton("KLAC −", root, new Vector2(0, 0), new Vector2(120, 400), new Vector2(110, 80),
                held => _input.TouchDragAxis = held ? -1f : 0f);
            HoldButton("KLAC +", root, new Vector2(0, 0), new Vector2(255, 400), new Vector2(110, 80),
                held => _input.TouchDragAxis = held ? 1f : 0f);

            // Only on the devices that need it. On a desktop these would just be
            // 800 px of dead pixels covering the lake.
            bool touchDevice = Application.isMobilePlatform || UnityEngine.Input.touchSupported;
            _touchRoot.SetActive(touchDevice);
        }

        private Image MakeButton(string label, RectTransform parent, Vector2 anchor,
                                 Vector2 offset, Vector2 size)
        {
            var rt = Rect(label, parent, anchor, anchor,
                          new Vector2(offset.x - size.x * 0.5f, offset.y - size.y * 0.5f),
                          new Vector2(offset.x + size.x * 0.5f, offset.y + size.y * 0.5f));
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(0.10f, 0.16f, 0.18f, 0.72f);
            var t = Label(label + "Text", rt, label, 20, TextAnchor.MiddleCenter,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            t.raycastTarget = false;
            return img;
        }

        private void HoldButton(string label, RectTransform parent, Vector2 anchor,
                                Vector2 offset, Vector2 size, System.Action<bool> onHold)
        {
            var img = MakeButton(label, parent, anchor, offset, size);
            var trigger = img.gameObject.AddComponent<EventTrigger>();
            AddTrigger(trigger, EventTriggerType.PointerDown, () => { onHold(true); img.color = new Color(0.22f, 0.42f, 0.36f, 0.9f); });
            AddTrigger(trigger, EventTriggerType.PointerUp, () => { onHold(false); img.color = new Color(0.10f, 0.16f, 0.18f, 0.72f); });
            // A finger that slides off the button must release it, or the cast
            // charges forever and auto-fires.
            AddTrigger(trigger, EventTriggerType.PointerExit, () => { onHold(false); img.color = new Color(0.10f, 0.16f, 0.18f, 0.72f); });
        }

        private void TapButton(string label, RectTransform parent, Vector2 anchor,
                               Vector2 offset, Vector2 size, System.Action onTap)
        {
            var img = MakeButton(label, parent, anchor, offset, size);
            img.color = new Color(0.28f, 0.20f, 0.10f, 0.75f);
            var trigger = img.gameObject.AddComponent<EventTrigger>();
            AddTrigger(trigger, EventTriggerType.PointerDown, onTap);
        }

        private static void AddTrigger(EventTrigger trigger, EventTriggerType type, System.Action fn)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(_ => fn());
            trigger.triggers.Add(entry);
        }

        /* --- per-frame ------------------------------------------------------------ */

        public void Apply(in FishingGame.Telemetry tm, World world, PlayerState state, float dt)
        {
            ApplyTopBar(world, state);
            ApplyTension(tm);
            ApplyCast(tm);
            ApplyBite(tm);
            ApplyToast(dt);
            ApplyCard(tm, dt);
        }

        private void ApplyTopBar(World world, PlayerState state)
        {
            if (world != null)
            {
                _clockText.text = world.ClockString();
                _weatherText.text = $"{world.Phase?.Label ?? ""} · {world.Weather?.Label ?? ""}";
            }
            if (state != null)
            {
                _moneyText.text = $"RM {state.Money:0}";
                _levelText.text = $"Tahap {state.Level}";
                _spotText.text = state.Spot?.Name ?? "";
                _xpFill.fillAmount = (float)state.GetXpProgress().Pct;
            }
        }

        private void ApplyTension(in FishingGame.Telemetry tm)
        {
            var rod = tm.Rod;
            float load = Mathf.Clamp01((float)rod.LoadFrac);
            _tensionFill.fillAmount = load;

            Color zone = rod.Zone switch
            {
                TensionZone.Slack => ZoneSlack,
                TensionZone.Good => ZoneGood,
                TensionZone.High => ZoneHigh,
                _ => ZoneDanger,
            };
            // Flash in the danger band. A steady red is something you stop seeing
            // after ten seconds; a pulse is not.
            if (rod.Zone == TensionZone.Danger)
                zone = Color.Lerp(zone, Color.white, Mathf.PingPong(Time.time * 6f, 1f) * 0.45f);
            _tensionFill.color = zone;

            // Position the clutch marker as a fraction of the line's breaking force.
            float dragVsLine = Mathf.Clamp01((float)rod.DragVsLine);
            var mrt = (RectTransform)_dragMarker.transform;
            mrt.anchorMin = new Vector2(dragVsLine, 0f);
            mrt.anchorMax = new Vector2(dragVsLine, 1f);
            _dragMarker.color = rod.DragUnsafe ? new Color(1f, 0.35f, 0.30f) : new Color(1f, 0.95f, 0.55f);

            _tensionLabel.text = $"{rod.Tension:0} N / {rod.TestN:0} N";
            _dragLabel.text = rod.DragUnsafe
                ? $"Klac {rod.DragFrac * 100:0}% ⚠"
                : $"Klac {rod.DragFrac * 100:0}%";
            _dragLabel.color = rod.DragUnsafe ? new Color(1f, 0.55f, 0.45f) : Ink;

            _integrityFill.fillAmount = (float)rod.LineIntegrity;
            _integrityFill.color = Color.Lerp(ZoneDanger, new Color(0.55f, 0.85f, 0.95f), (float)rod.LineIntegrity);
            _hookFill.fillAmount = (float)rod.HookHold;
            _hookFill.color = Color.Lerp(ZoneDanger, new Color(0.95f, 0.72f, 0.42f), (float)rod.HookHold);
        }

        private void ApplyCast(in FishingGame.Telemetry tm)
        {
            bool charging = tm.Cast.Charging;
            if (_castRow.gameObject.activeSelf != charging) _castRow.gameObject.SetActive(charging);
            if (!charging) return;

            float v = (float)tm.Cast.Value;
            float over = (float)tm.Cast.Overload;
            _castFill.fillAmount = v;
            _castFill.color = tm.Cast.InSweetSpot
                ? new Color(0.45f, 0.95f, 0.55f)
                : new Color(0.55f, 0.75f, 0.85f);

            // The overload band grows out of the right-hand edge as it fills, so the
            // "let go NOW" moment is visible in peripheral vision.
            var ort = (RectTransform)_castOverload.transform;
            ort.anchorMin = new Vector2(1f - over, 0f);
            ort.anchorMax = new Vector2(1f, 1f);
            _castOverload.enabled = over > 0.001f;
        }

        private void ApplyBite(in FishingGame.Telemetry tm)
        {
            var bite = tm.Bite;
            bool fishing = tm.Phase == GameState.Fishing;
            if (_biteRow.gameObject.activeSelf != fishing) _biteRow.gameObject.SetActive(fishing);

            bool windowOpen = bite.State == BiteState.Committed;
            if (_windowRow.gameObject.activeSelf != windowOpen) _windowRow.gameObject.SetActive(windowOpen);
            if (windowOpen)
            {
                _windowFill.fillAmount = (float)bite.WindowPct;
                _windowLabel.text = bite.Candidate != null ? $"SENTAP! {bite.Candidate.Name}" : "SENTAP!";
            }

            if (!fishing) return;

            _biteState.text = bite.State switch
            {
                BiteState.Searching => "Mencari…",
                BiteState.Interest => bite.Candidate != null ? $"{bite.Candidate.Name} menyiasat" : "Sesuatu menyiasat",
                BiteState.Nibbling => "Ikan mengait — tunggu!",
                BiteState.Committed => "SENTAP SEKARANG",
                BiteState.Spooked => $"Ikan lari ({bite.Cooldown:0.0} s)",
                _ => "Menunggu",
            };

            _attractionFill.fillAmount = (float)bite.AttractionPct;
            _attractionFill.color = bite.State == BiteState.Spooked
                ? new Color(0.55f, 0.35f, 0.32f)
                : new Color(0.62f, 0.85f, 0.45f);

            var s = bite.Score;
            SetFactor(0, (float)s.LureMatch / 2.3f);
            SetFactor(1, (float)s.DepthMatch);
            SetFactor(2, (float)s.ActionMatch);
            SetFactor(3, (float)s.Stealth);
        }

        private void SetFactor(int i, float value01)
        {
            float v = Mathf.Clamp01(value01);
            _factorFills[i].fillAmount = v;
            // Red below a third: that is roughly where a single factor starts being
            // the reason nothing is biting.
            _factorFills[i].color = v < 0.34f ? ZoneDanger
                                  : v < 0.66f ? ZoneHigh
                                  : ZoneGood;
        }

        /* --- messages ------------------------------------------------------------- */

        public void Toast(string text, string kind)
        {
            _toastText.text = text;
            _toastText.color = kind switch
            {
                "fail" => new Color(1f, 0.55f, 0.48f),
                "miss" => new Color(1f, 0.80f, 0.45f),
                "warn" => new Color(1f, 0.88f, 0.55f),
                _ => Ink,
            };
            _toastTimer = 2.6f;
            _toastGroup.alpha = 1f;
        }

        private void ApplyToast(float dt)
        {
            if (_toastTimer <= 0f) return;
            _toastTimer -= dt;
            _toastGroup.alpha = Mathf.Clamp01(_toastTimer / 0.7f);
        }

        /* --- catch card ------------------------------------------------------------ */

        private float _cardTimer;

        public void ShowCatch(CatchCard card)
        {
            if (card == null || card.Lost) return;

            var sp = card.Species;
            var rarity = Game.Species?.RarityOf(sp);
            _cardTitle.text = card.IsRecord ? $"REKOD BARU — {sp.Name}" : sp.Name;
            _cardTitle.color = ProcNoise.HexToColor(rarity?.Color ?? "#ffffff");

            _cardStats.text =
                $"{card.Fish.LengthCm:0.0} cm · {card.Fish.MassKg:0.000} kg · {card.SizeClass.Label}\n" +
                $"{card.FightSeconds:0.0} s lawan · puncak {card.PeakTension:0} N" +
                (card.Fish.Trophy ? "\n★ TROFI" : "");

            string reward = $"+RM {card.Value:0}   +{card.Xp:0} XP";
            if (card.Levels > 0) reward += $"\nNaik ke Tahap {Game.State.Level}!";
            if (card.QuestRewards != null && card.QuestRewards.Count > 0)
            {
                foreach (var q in card.QuestRewards) reward += $"\nMisi selesai: {q.Name}";
            }
            _cardReward.text = reward;

            RenderPortrait(sp);
            _cardTimer = 2.6f;
            _cardGroup.alpha = 1f;
        }

        /// <summary>
        /// Rasterise the species' portrait from the SAME body functions the 3D mesh
        /// is lofted through. That is the point of the shared genome: this is not an
        /// illustration of the fish, it is the fish, drawn flat.
        /// </summary>
        private void RenderPortrait(Species sp)
        {
            const int W = 300, H = 172;
            if (_cardTexture == null)
            {
                _cardTexture = new Texture2D(W, H, TextureFormat.RGBA32, false)
                { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            }

            var art = sp.Art;
            var pixels = new Color32[W * H];
            Color bg = new Color(0.07f, 0.11f, 0.12f, 1f);

            for (int py = 0; py < H; py++)
            {
                for (int px = 0; px < W; px++)
                {
                    // u along the body with a margin, v measured from the centreline.
                    float u = (px / (float)W - 0.06f) / 0.88f;
                    float centred = (py / (float)H - 0.5f) * -2f;   // +1 top, -1 bottom

                    Color c = bg;
                    if (u >= 0f && u <= 1f)
                    {
                        float halfH = Render.FishMeshGen.BodyRadius(art, u) / 0.55f;
                        if (halfH > 0.001f && Mathf.Abs(centred) <= halfH)
                        {
                            float v = 0.5f + 0.5f * (centred / halfH);
                            c = Render.FishMeshGen.ColourAt(art, u, v);
                        }
                    }
                    pixels[py * W + px] = c;
                }
            }

            _cardTexture.SetPixels32(pixels);
            _cardTexture.Apply(false, false);
            _cardPortrait.texture = _cardTexture;
        }

        private void ApplyCard(in FishingGame.Telemetry tm, float dt)
        {
            if (_cardTimer <= 0f) return;
            _cardTimer -= dt;
            _cardGroup.alpha = Mathf.Clamp01(_cardTimer / 0.5f);
        }

        private void OnDestroy()
        {
            if (_cardTexture != null) Destroy(_cardTexture);
        }
    }
}

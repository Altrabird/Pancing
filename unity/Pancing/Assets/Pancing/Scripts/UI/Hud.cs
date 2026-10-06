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
        private RenderTexture _stageRT;
        private Camera _stageCam;
        private Transform _stageFish;
        private MeshFilter _stageFilter;
        private Material _stageMat;
        private float _stageSpin;

        // touch
        private GameObject _touchRoot;

        private static readonly Color ZoneSlack = new Color(0.45f, 0.52f, 0.58f);
        private static readonly Color ZoneGood = new Color(0.38f, 0.78f, 0.44f);
        private static readonly Color ZoneHigh = new Color(0.95f, 0.72f, 0.20f);
        private static readonly Color ZoneDanger = new Color(0.90f, 0.28f, 0.24f);
        private static readonly Color Panel = UiKit.Panel;
        private static readonly Color Ink = UiKit.Ink;
        private Image _cardEdge;

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
                          Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax,
                          float radius = 0f)
            => UiKit.Box(name, parent, color, anchorMin, anchorMax, offsetMin, offsetMax, radius);

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
            // Three floating pills rather than a full-width strip, so the sky and
            // the far bank stay visible. The shop/bag/travel buttons sit under the
            // right-hand pill (PanelSystem).
            var left = UiKit.Card("TimePill", transform, Panel, new Vector2(0, 1), new Vector2(0, 1),
                                  new Vector2(14, -52), new Vector2(330, -10), 21f);
            _clockText = Label("Clock", left.transform, "08:36", 22, TextAnchor.MiddleLeft,
                new Vector2(0, 0), new Vector2(0, 1), new Vector2(18, 0), new Vector2(100, 0));
            _clockText.fontStyle = FontStyle.Bold;
            _weatherText = Label("Weather", left.transform, "", 15, TextAnchor.MiddleLeft,
                new Vector2(0, 0), new Vector2(1, 1), new Vector2(98, 0), new Vector2(-14, 0));
            _weatherText.color = UiKit.InkDim;

            var mid = UiKit.Card("SpotPill", transform, Panel, new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                                 new Vector2(-160, -52), new Vector2(160, -10), 21f);
            _spotText = Label("Spot", mid.transform, "", 19, TextAnchor.MiddleCenter,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            _spotText.fontStyle = FontStyle.Bold;

            var right = UiKit.Card("PursePill", transform, Panel, new Vector2(1, 1), new Vector2(1, 1),
                                   new Vector2(-344, -50), new Vector2(-16, -10), 20f);
            _levelText = Label("Level", right.transform, "Tahap 1", 15, TextAnchor.MiddleLeft,
                new Vector2(0, 0), new Vector2(0, 1), new Vector2(16, 0), new Vector2(96, 0));
            _levelText.fontStyle = FontStyle.Bold;
            _xpFill = Bar("Xp", right.transform, UiKit.Track, new Color(0.42f, 0.72f, 0.95f),
                new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(96, -5), new Vector2(-122, 5), out _);
            _moneyText = Label("Money", right.transform, "RM 120", 20, TextAnchor.MiddleRight,
                new Vector2(1, 0), new Vector2(1, 1), new Vector2(-118, 0), new Vector2(-16, 0));
            _moneyText.color = UiKit.Gold;
            _moneyText.fontStyle = FontStyle.Bold;
        }

        private void BuildTensionPanel()
        {
            // Bottom-left, big. This is the thing the player stares at.
            var panel = UiKit.Card("TensionPanel", transform, Panel, new Vector2(0, 0), new Vector2(0, 0),
                                   new Vector2(16, 16), new Vector2(404, 150), 16f);

            var cap = Label("TensionCap", panel.transform, "TEGANGAN", 13, TextAnchor.MiddleLeft,
                new Vector2(0, 1), new Vector2(0.5f, 1), new Vector2(16, -28), new Vector2(0, -8));
            cap.color = UiKit.InkDim;
            cap.fontStyle = FontStyle.Bold;
            _tensionLabel = Label("TensionVal", panel.transform, "0 N", 15, TextAnchor.MiddleRight,
                new Vector2(0.5f, 1), new Vector2(1, 1), new Vector2(0, -28), new Vector2(-16, -8));
            _tensionLabel.fontStyle = FontStyle.Bold;

            _tensionFill = Bar("Tension", panel.transform, UiKit.Track, ZoneGood,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -62), new Vector2(-16, -32), out _tensionBack);

            // The zones painted faintly on the track itself (RodSystem's thresholds),
            // so "how close to red" is visible before the fill gets there.
            Zone(_tensionBack.transform, 0f, (float)RodSystem.ZoneSlack, ZoneSlack);
            Zone(_tensionBack.transform, (float)RodSystem.ZoneSlack, (float)RodSystem.ZoneGood, ZoneGood);
            Zone(_tensionBack.transform, (float)RodSystem.ZoneGood, (float)RodSystem.ZoneHigh, ZoneHigh);
            Zone(_tensionBack.transform, (float)RodSystem.ZoneHigh, 1f, ZoneDanger);
            _tensionFill.transform.SetAsLastSibling();

            // The drag marker rides the same scale as the tension bar, so "my clutch
            // is set above what this line can take" is a thing you can SEE rather
            // than a number you have to convert.
            _dragMarker = Box("DragMarker", _tensionBack.transform, new Color(1f, 0.95f, 0.55f),
                new Vector2(0, 0), new Vector2(0, 1), new Vector2(-2, 0), new Vector2(2, 0));

            _dragLabel = Label("DragVal", panel.transform, "Klac 55%", 14, TextAnchor.MiddleRight,
                new Vector2(0.5f, 1), new Vector2(1, 1), new Vector2(0, -84), new Vector2(-16, -66));
            var dragCap = Label("DragCap", panel.transform, "Penanda kuning = klac", 12, TextAnchor.MiddleLeft,
                new Vector2(0, 1), new Vector2(0.5f, 1), new Vector2(16, -84), new Vector2(0, -66));
            dragCap.color = UiKit.InkDim;

            Label("IntegrityCap", panel.transform, "Tali", 13, TextAnchor.MiddleLeft,
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(16, -108), new Vector2(62, -90));
            _integrityFill = Bar("Integrity", panel.transform, UiKit.Track,
                new Color(0.55f, 0.85f, 0.95f),
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(62, -105), new Vector2(-16, -93), out _);

            Label("HookCap", panel.transform, "Kail", 13, TextAnchor.MiddleLeft,
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(16, -128), new Vector2(62, -110));
            _hookFill = Bar("Hook", panel.transform, UiKit.Track,
                new Color(0.95f, 0.72f, 0.42f),
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(62, -125), new Vector2(-16, -113), out _);
        }

        private void Zone(Transform track, float from, float to, Color c)
        {
            Box("Zone", track, new Color(c.r, c.g, c.b, 0.22f),
                new Vector2(from, 0), new Vector2(to, 1), Vector2.zero, Vector2.zero);
        }

        private void BuildCastMeter()
        {
            _castRow = Rect("CastMeter", transform, new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                            new Vector2(-220, 22), new Vector2(220, 62));
            var track = _castRow.gameObject.AddComponent<Image>();
            track.color = new Color(0, 0, 0, 0.55f);
            track.raycastTarget = false;
            UiKit.Rounded(track, 20f);
            _castRow.gameObject.AddComponent<Mask>().showMaskGraphic = true;

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

            // Outside the masked track, or the mask would clip it away.
            var hint = Label("CastHint", transform, "LEPAS di jalur putih", 14, TextAnchor.MiddleCenter,
                new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(-220, 64), new Vector2(220, 86));
            hint.fontStyle = FontStyle.Bold;
            _castHint = hint.gameObject;
            _castHint.SetActive(false);

            _castRow.gameObject.SetActive(false);
        }

        private void BuildBitePanel()
        {
            _biteRow = (RectTransform)UiKit.Card("BitePanel", transform, Panel, new Vector2(1, 0), new Vector2(1, 0),
                            new Vector2(-344, 16), new Vector2(-16, 176), 16f).transform;

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
            var windowRow = (RectTransform)UiKit.Card("Window", transform, new Color(0.10f, 0.07f, 0.02f, 0.86f),
                                 new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                                 new Vector2(-210, -262), new Vector2(210, -172), 18f).transform;
            windowRow.Find("WindowEdge").GetComponent<Image>().color = new Color(1f, 0.78f, 0.30f, 0.65f);
            _windowLabel = Label("WindowLabel", windowRow, "SENTAP!", 34, TextAnchor.MiddleCenter,
                new Vector2(0, 0.35f), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            _windowLabel.color = new Color(1f, 0.86f, 0.35f);
            _windowLabel.fontStyle = FontStyle.Bold;
            _windowFill = Bar("WindowBar", windowRow, new Color(0, 0, 0, 0.5f), new Color(1f, 0.72f, 0.25f),
                new Vector2(0, 0), new Vector2(1, 0.32f), new Vector2(12, 10), new Vector2(-12, -4), out _);
            windowRow.gameObject.SetActive(false);
            _windowRow = windowRow;

            BuildEdgeFlash();
            BuildFightBar();
        }

        private RectTransform _windowRow;
        private GameObject _castHint;
        private CanvasGroup _edgeFlash, _dangerFlash;

        // fight bar
        private RectTransform _fightRow, _tugMarker;
        private Image _tugTrack, _reelAsked, _reelGot;
        private Text _fishForce, _yourForce, _tugVerdict, _reelPct;

        /// <summary>
        /// Four glowing screen edges that pulse while the hookset window is open.
        ///
        /// The window can be 320 ms wide and the player is looking at the float in
        /// the middle of the screen, not at a caption in the top corner. Peripheral
        /// vision is very good at catching a brightness change and very bad at
        /// reading text, so the alert has to be light at the edge of the frame
        /// rather than words anywhere. With no audio in the game, this is the only
        /// channel left.
        /// </summary>
        private void BuildEdgeFlash()
        {
            var root = Rect("EdgeFlash", transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            _edgeFlash = root.gameObject.AddComponent<CanvasGroup>();
            _edgeFlash.alpha = 0f;
            _edgeFlash.blocksRaycasts = false;

            Color glow = new Color(1f, 0.74f, 0.20f);
            const float t = 12f;
            Box("EdgeTop", root, glow, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -t), new Vector2(0, 0));
            Box("EdgeBottom", root, glow, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 0), new Vector2(0, t));
            Box("EdgeLeft", root, glow, new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0), new Vector2(t, 0));
            Box("EdgeRight", root, glow, new Vector2(1, 0), new Vector2(1, 1), new Vector2(-t, 0), new Vector2(0, 0));

            // The same idea in red for a line about to part: a heartbeat at the
            // edge of vision while the tension sits in the danger band.
            var droot = Rect("DangerFlash", transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            _dangerFlash = droot.gameObject.AddComponent<CanvasGroup>();
            _dangerFlash.alpha = 0f;
            _dangerFlash.blocksRaycasts = false;
            Color red = new Color(0.95f, 0.12f, 0.08f, 0.85f);
            for (int i = 0; i < 3; i++)
            {
                float d = 8f + i * 10f;
                Color c = new Color(red.r, red.g, red.b, red.a * (1f - i * 0.33f));
                Box("DTop" + i, droot, c, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -d), new Vector2(0, -d + 9f));
                Box("DBottom" + i, droot, c, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, d - 9f), new Vector2(0, d));
                Box("DLeft" + i, droot, c, new Vector2(0, 0), new Vector2(0, 1), new Vector2(d - 9f, 0), new Vector2(d, 0));
                Box("DRight" + i, droot, c, new Vector2(1, 0), new Vector2(1, 1), new Vector2(-d, 0), new Vector2(-d + 9f, 0));
            }
        }

        /// <summary>
        /// The tug of war, made visible.
        ///
        /// The simulation has always computed this — the fish moves by
        /// `pull - tension` against water drag — but the player could only infer it
        /// from whether the fish was slowly getting closer. Showing it turns the
        /// fight from "hold the needle in the green and wait" into a contest you can
        /// see yourself winning or losing, second by second.
        ///
        /// The reel bar underneath shows what you ASKED for against what the reel
        /// actually DELIVERED. The gap is the gearbox losing to the fish, and when
        /// the clutch slips it drops to nothing — which is the clearest possible
        /// explanation of why winding harder sometimes achieves exactly zero.
        /// </summary>
        private void BuildFightBar()
        {
            // Sits ABOVE the tension panel, which occupies the bottom-left corner
            // out to x = 430 and up to y = 150. The first placement put the fight
            // bar at y 84..188 and the two drew straight through each other.
            // Bottom-right, in the bite panel's place (the two never show together),
            // so nothing sits over the angler in the middle of the screen.
            _fightRow = (RectTransform)UiKit.Card("FightBar", transform, Panel, new Vector2(1, 0), new Vector2(1, 0),
                             new Vector2(-424, 16), new Vector2(-16, 150), 16f).transform;

            _fishForce = Label("FishForce", _fightRow, "IKAN", 15, TextAnchor.MiddleLeft,
                new Vector2(0, 1), new Vector2(0.5f, 1), new Vector2(16, -28), new Vector2(0, -8));
            _fishForce.fontStyle = FontStyle.Bold;
            _fishForce.color = new Color(0.95f, 0.55f, 0.45f);

            _yourForce = Label("YourForce", _fightRow, "ANDA", 15, TextAnchor.MiddleRight,
                new Vector2(0.5f, 1), new Vector2(1, 1), new Vector2(0, -28), new Vector2(-16, -8));
            _yourForce.fontStyle = FontStyle.Bold;
            _yourForce.color = new Color(0.55f, 0.85f, 0.70f);

            // The tug track. Left = the fish is taking line, right = you are gaining.
            _tugTrack = Box("TugTrack", _fightRow, new Color(0, 0, 0, 0.5f),
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -60), new Vector2(-16, -32), 10f);
            // Red half = the fish is winning, green half = you are.
            Box("TugFish", _tugTrack.transform, new Color(0.95f, 0.45f, 0.38f, 0.16f),
                new Vector2(0, 0), new Vector2(0.5f, 1), Vector2.zero, Vector2.zero, 10f);
            Box("TugYou", _tugTrack.transform, new Color(0.45f, 0.90f, 0.62f, 0.16f),
                new Vector2(0.5f, 0), new Vector2(1, 1), Vector2.zero, Vector2.zero, 10f);

            // A centre tick, so "level" is a place rather than a guess.
            Box("TugCentre", _tugTrack.transform, new Color(1, 1, 1, 0.30f),
                new Vector2(0.5f, 0), new Vector2(0.5f, 1), new Vector2(-1, 2), new Vector2(1, -2));

            _tugMarker = (RectTransform)Box("TugMarker", _tugTrack.transform, Color.white,
                new Vector2(0.5f, 0), new Vector2(0.5f, 1), new Vector2(-6, 2), new Vector2(6, -2), 6f).transform;

            _tugVerdict = Label("TugVerdict", _fightRow, "", 14, TextAnchor.MiddleCenter,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -82), new Vector2(-16, -62));
            _tugVerdict.fontStyle = FontStyle.Bold;

            // Reel: asked vs delivered, in one track.
            Label("ReelCap", _fightRow, "KARAU", 13, TextAnchor.MiddleLeft,
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(16, -114), new Vector2(76, -94));

            _reelAsked = Bar("ReelAsked", _fightRow, new Color(0, 0, 0, 0.5f), new Color(0.42f, 0.55f, 0.60f),
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(78, -111), new Vector2(-150, -97), out var reelTrack);
            _reelGot = Box("ReelGot", reelTrack.transform, new Color(0.55f, 0.88f, 0.72f),
                new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, 3), new Vector2(0, -3));
            _reelGot.type = Image.Type.Filled;
            _reelGot.fillMethod = Image.FillMethod.Horizontal;
            _reelGot.fillAmount = 0f;

            // Wide enough for "KLAC TERGELINCIR" — it used to overrun the bar.
            _reelPct = Label("ReelPct", _fightRow, "", 13, TextAnchor.MiddleRight,
                new Vector2(1, 1), new Vector2(1, 1), new Vector2(-146, -114), new Vector2(-16, -94));
            _reelPct.fontStyle = FontStyle.Bold;

            _fightRow.gameObject.SetActive(false);
        }

        private void ApplyFightBar(in FishingGame.Telemetry tm)
        {
            bool fighting = tm.Fish.HasValue;
            if (_fightRow.gameObject.activeSelf != fighting) _fightRow.gameObject.SetActive(fighting);
            if (!fighting) return;

            var fish = tm.Fish.Value;

            _fishForce.text = $"IKAN  {fish.Pull:0} N";
            _yourForce.text = $"{tm.Rod.Tension:0} N  ANDA";

            // VelAway is metres per second of line leaving. Negative means it is
            // coming to you, which is the only thing that actually wins a fight.
            float vel = (float)fish.VelAway;
            float frac = 0.5f - Mathf.Clamp(vel, -2.5f, 2.5f) / 5f;
            _tugMarker.anchorMin = new Vector2(frac, 0f);
            _tugMarker.anchorMax = new Vector2(frac, 1f);

            // Deadband matches the one decimal place the speed is printed at, so the
            // verdict can never read "Ikan lari — 0.0 m/s keluar" and contradict
            // its own number.
            bool gaining = vel < -0.05f;
            bool losing = vel > 0.05f;
            var col = gaining ? new Color(0.45f, 0.90f, 0.62f)
                    : losing ? new Color(0.95f, 0.45f, 0.38f)
                    : new Color(0.85f, 0.85f, 0.85f);
            _tugMarker.GetComponent<Image>().color = col;

            _tugVerdict.text = gaining ? $"Menang — {-vel:0.0} m/s masuk"
                             : losing ? $"Ikan lari — {vel:0.0} m/s keluar"
                             : "Seri";
            _tugVerdict.color = col;

            // Asked vs delivered. When the clutch slips, delivered is zero however
            // hard the handle is turning.
            float asked = Mathf.Clamp01((float)tm.ReelInput);
            float maxRetrieve = Mathf.Max((float)tm.Gear.Reel.Retrieve, 0.01f);
            float got = Mathf.Clamp01((float)tm.ReelGain / maxRetrieve);
            _reelAsked.fillAmount = asked;
            _reelGot.fillAmount = got;

            if (tm.Rod.Slipping)
            {
                _reelPct.text = "KLAC TERGELINCIR";
                _reelPct.color = new Color(1f, 0.62f, 0.30f);
            }
            else
            {
                _reelPct.text = asked > 0.01f ? $"{got / Mathf.Max(asked, 0.01f) * 100f:0}%" : "—";
                _reelPct.color = UiKit.InkDim;
            }
        }

        private void BuildToast()
        {
            var rt = (RectTransform)UiKit.Card("Toast", transform, new Color(0.04f, 0.08f, 0.10f, 0.88f),
                          new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                          new Vector2(-260, -158), new Vector2(260, -112), 23f).transform;
            _toastGroup = rt.gameObject.AddComponent<CanvasGroup>();
            _toastGroup.alpha = 0f;
            _toastGroup.blocksRaycasts = false;
            _toastText = Label("ToastText", rt, "", 18, TextAnchor.MiddleCenter,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        }

        private void BuildCatchCard()
        {
            var card = UiKit.Card("CatchCard", transform, new Color(0.04f, 0.08f, 0.10f, 0.95f),
                          new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-536, -170), new Vector2(-36, 210), 22f);
            var rt = (RectTransform)card.transform;
            _cardEdge = rt.Find("CatchCardEdge").GetComponent<Image>();
            _cardGroup = rt.gameObject.AddComponent<CanvasGroup>();
            _cardGroup.alpha = 0f;
            _cardGroup.blocksRaycasts = false;

            var dapat = Label("CardKicker", rt, "DAPAT!", 14, TextAnchor.MiddleCenter,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(12, -30), new Vector2(-12, -12));
            dapat.color = UiKit.Gold;
            dapat.fontStyle = FontStyle.Bold;
            _cardTitle = Label("CardTitle", rt, "", 28, TextAnchor.MiddleCenter,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(12, -64), new Vector2(-12, -30));
            _cardTitle.fontStyle = FontStyle.Bold;

            // The fish on its turntable, framed in a rounded window.
            var frame = Box("PortraitFrame", rt, new Color(0.06f, 0.10f, 0.12f, 1f),
                new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(-157, -250), new Vector2(157, -70), 14f);
            frame.gameObject.AddComponent<Mask>().showMaskGraphic = true;
            var portrait = Rect("Portrait", frame.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            _cardPortrait = portrait.gameObject.AddComponent<RawImage>();
            _cardPortrait.raycastTarget = false;

            _cardStats = Label("CardStats", rt, "", 16, TextAnchor.UpperCenter,
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(12, 72), new Vector2(-12, 124));
            _cardStats.color = UiKit.InkDim;
            _cardReward = Label("CardReward", rt, "", 18, TextAnchor.LowerCenter,
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
            // Thumb buttons are pills and circles: easier to hit, easier to read.
            UiKit.Rounded(img, Mathf.Min(size.x, size.y) * 0.5f);
            var t = Label(label + "Text", rt, label, 20, TextAnchor.MiddleCenter,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            t.raycastTarget = false;
            t.fontStyle = FontStyle.Bold;
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
            ApplyFightBar(tm);
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
            if (_dangerFlash != null)
            {
                bool danger = tm.Fish.HasValue && rod.Zone == TensionZone.Danger;
                // A double-thump, like a pulse.
                float beat = Mathf.Repeat(Time.time * 1.6f, 1f);
                float pulse = Mathf.Exp(-beat * 14f) + 0.6f * Mathf.Exp(-Mathf.Abs(beat - 0.22f) * 14f);
                float target = danger ? 0.25f + 0.6f * Mathf.Clamp01(pulse) : 0f;
                _dangerFlash.alpha = Mathf.MoveTowards(_dangerFlash.alpha, target, Time.unscaledDeltaTime * 5f);
            }
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
            if (_castHint != null && _castHint.activeSelf != charging) _castHint.SetActive(charging);
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
                // Pulse fast — this is an alarm, not a label.
                float pulse = 1f + Mathf.PingPong(Time.time * 7f, 1f) * 0.10f;
                _windowRow.localScale = new Vector3(pulse, pulse, 1f);
            }
            else
            {
                _windowRow.localScale = Vector3.one;
            }

            if (_edgeFlash != null)
            {
                float target = windowOpen ? 0.30f + Mathf.PingPong(Time.time * 7f, 1f) * 0.45f : 0f;
                _edgeFlash.alpha = Mathf.MoveTowards(_edgeFlash.alpha, target, Time.deltaTime * 6f);
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
            if (_cardEdge != null)
            {
                var rc = _cardTitle.color;
                _cardEdge.color = new Color(rc.r, rc.g, rc.b, 0.85f);
            }

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

            ShowOnStage(sp);
            _cardTimer = 3.4f;
            _cardGroup.alpha = 1f;
        }

        /// <summary>
        /// The card shows the actual 3D fish the player fought — the same mesh,
        /// on a little turntable far below the world, rendered into a texture.
        /// The stage camera only runs while the card is up.
        /// </summary>
        private void ShowOnStage(Species sp)
        {
            if (_stageCam == null) BuildStage();
            _stageFilter.sharedMesh = Render.FishMeshGen.For(sp);
            var b = _stageFilter.sharedMesh.bounds;
            float len = Mathf.Max(b.size.z, b.size.y * 1.6f, 0.01f);
            _stageFish.localScale = Vector3.one * (1.25f / len);
            // Centre the mesh on the turntable (pivot is at the snout).
            _stageFilter.transform.localPosition = -b.center;
            _stageMat.SetFloat("_SwimAmp", Render.FishMeshGen.Swims(sp) ? 0.03f : 0f);
            _stageSpin = 0f;
            _stageCam.enabled = true;
            _cardPortrait.texture = _stageRT;
        }

        private void BuildStage()
        {
            var root = new GameObject("CatchStage").transform;
            root.position = new Vector3(0f, -1000f, 0f);

            _stageRT = new RenderTexture(640, 368, 16, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var camGo = new GameObject("StageCamera");
            camGo.transform.SetParent(root, false);
            camGo.transform.localPosition = new Vector3(-1.75f, 0.18f, 0f);
            camGo.transform.localRotation = Quaternion.LookRotation(new Vector3(1.75f, -0.18f, 0f));
            _stageCam = camGo.AddComponent<Camera>();
            _stageCam.targetTexture = _stageRT;
            _stageCam.clearFlags = CameraClearFlags.SolidColor;
            _stageCam.backgroundColor = new Color(0.06f, 0.10f, 0.12f, 1f);
            _stageCam.fieldOfView = 34f;
            _stageCam.nearClipPlane = 0.05f;
            _stageCam.farClipPlane = 8f;
            _stageCam.enabled = false;

            _stageFish = new GameObject("Turntable").transform;
            _stageFish.SetParent(root, false);
            var meshGo = new GameObject("Fish");
            meshGo.transform.SetParent(_stageFish, false);
            _stageFilter = meshGo.AddComponent<MeshFilter>();
            var mr = meshGo.AddComponent<MeshRenderer>();
            var shader = Shader.Find("Pancing/Fish") ?? Shader.Find("Pancing/VertexLit");
            // Bright ambient: the card must read at midnight too.
            _stageMat = new Material(shader) { name = "StageFish" };
            _stageMat.SetFloat("_Ambient", 0.75f);
            _stageMat.SetFloat("_SwimFreq", 5f);
            mr.sharedMaterial = _stageMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        private void ApplyCard(in FishingGame.Telemetry tm, float dt)
        {
            if (_cardTimer <= 0f)
            {
                if (_stageCam != null && _stageCam.enabled) _stageCam.enabled = false;
                return;
            }
            _cardTimer -= dt;
            _cardGroup.alpha = Mathf.Clamp01(_cardTimer / 0.5f);
            // A slow turn either side of the side-on view, like holding it up.
            _stageSpin += dt;
            if (_stageFish != null)
                _stageFish.localRotation = Quaternion.Euler(Mathf.Sin(_stageSpin * 1.3f) * 6f, Mathf.Sin(_stageSpin * 0.9f) * 32f, 0f);
        }

        private void OnDestroy()
        {
            if (_stageRT != null) { _stageRT.Release(); Destroy(_stageRT); }
            if (_stageMat != null) Destroy(_stageMat);
            if (_stageFish != null) Destroy(_stageFish.parent.gameObject);
        }
    }
}

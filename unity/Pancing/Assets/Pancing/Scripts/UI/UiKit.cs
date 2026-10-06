using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace Pancing.UI
{
    /// <summary>
    /// The handful of uGUI primitives every screen in this game is built from.
    ///
    /// There are no prefabs anywhere in the project — the HUD and the panels are
    /// constructed in code at boot — so these five builders are the entire widget
    /// library. Keeping them in one place is what stops the HUD and the shop from
    /// drifting into two slightly different visual languages.
    /// </summary>
    public static class UiKit
    {
        /// <summary>Set once by the HUD at startup; every builder below uses it.</summary>
        public static Font Font;

        public static readonly Color Ink = new Color(0.95f, 0.97f, 0.96f);
        public static readonly Color InkDim = new Color(0.68f, 0.75f, 0.76f);
        public static readonly Color Panel = new Color(0.035f, 0.075f, 0.095f, 0.72f);
        public static readonly Color PanelSolid = new Color(0.045f, 0.085f, 0.105f, 0.97f);
        public static readonly Color Edge = new Color(1f, 1f, 1f, 0.13f);
        public static readonly Color Track = new Color(0f, 0f, 0f, 0.42f);
        public static readonly Color ButtonBase = new Color(0.10f, 0.24f, 0.27f, 0.92f);
        public static readonly Color Accent = new Color(0.42f, 0.78f, 0.62f);
        public static readonly Color Danger = new Color(0.90f, 0.38f, 0.32f);
        public static readonly Color Gold = new Color(1f, 0.84f, 0.42f);

        /* --- rounded sprites, generated once ----------------------------------- */

        // The whole look hangs on these two: a filled rounded rectangle and a thin
        // rounded ring, both 9-sliced, generated at runtime so there are still no
        // image files in the project. Corner radius per widget comes from
        // pixelsPerUnitMultiplier (texture radius / wanted radius).
        private const int SpriteSize = 64;
        private const float SpriteRadius = 16f;
        private static Sprite _round, _ring;

        public static Sprite Round => _round != null ? _round : (_round = MakeRounded(false));
        public static Sprite Ring => _ring != null ? _ring : (_ring = MakeRounded(true));

        private static Sprite MakeRounded(bool ring)
        {
            var tex = new Texture2D(SpriteSize, SpriteSize, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = ring ? "ui_ring" : "ui_round" };
            var px = new Color32[SpriteSize * SpriteSize];
            float r = SpriteRadius, half = SpriteSize * 0.5f;
            for (int y = 0; y < SpriteSize; y++)
            for (int x = 0; x < SpriteSize; x++)
            {
                // Signed distance to a rounded square filling the texture.
                float qx = Mathf.Abs(x + 0.5f - half) - (half - r);
                float qy = Mathf.Abs(y + 0.5f - half) - (half - r);
                float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
                float d = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                float a = Mathf.Clamp01(0.5f - d);                 // 1 px anti-aliased edge
                if (ring) a *= Mathf.Clamp01(d + 2.5f);           // keep a 2 px band
                px[y * SpriteSize + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, SpriteSize, SpriteSize), new Vector2(0.5f, 0.5f),
                                 100f, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
        }

        /// <summary>Give an Image rounded corners of `radius` reference pixels.</summary>
        public static void Rounded(Image img, float radius, bool ring = false)
        {
            if (radius <= 0f) return;
            img.sprite = ring ? Ring : Round;
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = SpriteRadius / radius;
        }

        /// <summary>A panel: rounded glass with a faint outline.</summary>
        public static Image Card(string name, Transform parent, Color color,
                                 Vector2 anchorMin, Vector2 anchorMax,
                                 Vector2 offsetMin, Vector2 offsetMax, float radius = 14f)
        {
            var img = Box(name, parent, color, anchorMin, anchorMax, offsetMin, offsetMax, radius);
            var edge = Box(name + "Edge", img.transform, Edge, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            Rounded(edge, radius, ring: true);
            return img;
        }

        public static Font Resolve() =>
            Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
            ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

        public static RectTransform Rect(string name, Transform parent,
                                         Vector2 anchorMin, Vector2 anchorMax,
                                         Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            return rt;
        }

        public static Image Box(string name, Transform parent, Color color,
                                Vector2 anchorMin, Vector2 anchorMax,
                                Vector2 offsetMin, Vector2 offsetMax, float radius = 0f)
        {
            var rt = Rect(name, parent, anchorMin, anchorMax, offsetMin, offsetMax);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            Rounded(img, radius);
            return img;
        }

        public static Text Label(string name, Transform parent, string text, int size,
                                 TextAnchor align, Vector2 anchorMin, Vector2 anchorMax,
                                 Vector2 offsetMin, Vector2 offsetMax)
        {
            var rt = Rect(name, parent, anchorMin, anchorMax, offsetMin, offsetMax);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.alignment = align;
            t.color = Ink;
            t.raycastTarget = false;
            // Overflow by default. Single-line readouts like "0 N / 35 N" sit in
            // tight rects and must never wrap; anything that genuinely needs to
            // flow asks for it with Paragraph().
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            // A soft drop shadow: the HUD sits over bright sky and sand.
            var sh = rt.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.55f);
            sh.effectDistance = new Vector2(1f, -1.5f);
            return t;
        }

        /// <summary>A Label that wraps and clips, for descriptions rather than readouts.</summary>
        public static Text Paragraph(string name, Transform parent, string text, int size,
                                     TextAnchor align, Vector2 anchorMin, Vector2 anchorMax,
                                     Vector2 offsetMin, Vector2 offsetMax)
        {
            var t = Label(name, parent, text, size, align, anchorMin, anchorMax, offsetMin, offsetMax);
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            return t;
        }

        /// <summary>A left-anchored fill inside a track. Drive it with fillAmount.</summary>
        public static Image Bar(string name, Transform parent, Color trackColor, Color fillColor,
                                Vector2 anchorMin, Vector2 anchorMax,
                                Vector2 offsetMin, Vector2 offsetMax, out Image track)
        {
            // A pill: the track is rounded and masks the (square) fill, so the fill
            // keeps a rounded left end and a clean edge wherever it stops.
            track = Box(name + "Track", parent, trackColor, anchorMin, anchorMax, offsetMin, offsetMax, 8f);
            track.gameObject.AddComponent<Mask>().showMaskGraphic = true;
            var fill = Box(name + "Fill", track.transform, fillColor,
                           Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 0f;
            return fill;
        }

        /// <summary>
        /// A clickable button with a label. Returns the background Image so the
        /// caller can recolour it — a disabled state here is a colour and a dead
        /// callback rather than a separate widget, because every disabled button in
        /// this game still has to explain WHY (too expensive, level too low).
        /// </summary>
        public static Image Button(string name, Transform parent, string label, int size,
                                   Vector2 anchorMin, Vector2 anchorMax,
                                   Vector2 offsetMin, Vector2 offsetMax,
                                   System.Action onClick, out Text labelText)
        {
            var rt = Rect(name, parent, anchorMin, anchorMax, offsetMin, offsetMax);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = ButtonBase;
            Rounded(img, 10f);

            // Hover/press glow as an overlay, so callers that recolour the button
            // for its state (equipped, unaffordable) are never overwritten.
            var glow = Box(name + "Glow", rt, new Color(1f, 1f, 1f, 0f), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, 10f);
            var edge = Box(name + "Edge", rt, Edge, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            Rounded(edge, 10f, ring: true);

            labelText = Label(name + "Text", rt, label, size, TextAnchor.MiddleCenter,
                              Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            labelText.raycastTarget = false;
            labelText.fontStyle = FontStyle.Bold;

            var hover = rt.gameObject.AddComponent<EventTrigger>();
            void On(EventTriggerType type, float a)
            {
                var e = new EventTrigger.Entry { eventID = type };
                e.callback.AddListener(_ => glow.color = new Color(1f, 1f, 1f, a));
                hover.triggers.Add(e);
            }
            On(EventTriggerType.PointerEnter, 0.10f);
            On(EventTriggerType.PointerExit, 0f);
            On(EventTriggerType.PointerDown, 0.22f);
            On(EventTriggerType.PointerUp, 0.10f);

            if (onClick != null)
            {
                var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
                entry.callback.AddListener(_ => onClick());
                hover.triggers.Add(entry);
            }
            return img;
        }

        /// <summary>
        /// A vertical scrolling list. Returns the content transform to parent rows
        /// into; it grows downward and the ScrollRect handles the rest.
        ///
        /// Built by hand rather than with a layout group: the rows here are fixed
        /// height and absolutely positioned, which is both cheaper and far easier
        /// to reason about than making ContentSizeFitter and VerticalLayoutGroup
        /// agree about a scroll viewport.
        /// </summary>
        public static RectTransform ScrollList(string name, Transform parent,
                                               Vector2 anchorMin, Vector2 anchorMax,
                                               Vector2 offsetMin, Vector2 offsetMax,
                                               out ScrollRect scroll)
        {
            var viewport = Rect(name, parent, anchorMin, anchorMax, offsetMin, offsetMax);
            var mask = viewport.gameObject.AddComponent<Image>();
            mask.color = new Color(0f, 0f, 0f, 0.18f);
            viewport.gameObject.AddComponent<Mask>().showMaskGraphic = true;

            var content = Rect("Content", viewport, new Vector2(0, 1), new Vector2(1, 1),
                               Vector2.zero, Vector2.zero);
            content.pivot = new Vector2(0.5f, 1f);

            scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Elastic;
            scroll.elasticity = 0.08f;
            scroll.scrollSensitivity = 34f;
            scroll.inertia = true;
            scroll.decelerationRate = 0.12f;
            return content;
        }

        /// <summary>Set a scroll list's content height for `rows` rows, so it scrolls.</summary>
        public static void SetContentHeight(RectTransform content, float height)
        {
            content.sizeDelta = new Vector2(0f, height);
            content.anchoredPosition = new Vector2(0f, 0f);
        }
    }
}

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

        public static readonly Color Ink = new Color(0.93f, 0.96f, 0.95f);
        public static readonly Color InkDim = new Color(0.66f, 0.72f, 0.72f);
        public static readonly Color Panel = new Color(0.05f, 0.08f, 0.09f, 0.62f);
        public static readonly Color PanelSolid = new Color(0.06f, 0.09f, 0.10f, 0.97f);
        public static readonly Color Accent = new Color(0.42f, 0.78f, 0.62f);
        public static readonly Color Danger = new Color(0.90f, 0.38f, 0.32f);
        public static readonly Color Gold = new Color(1f, 0.84f, 0.42f);

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
                                Vector2 offsetMin, Vector2 offsetMax)
        {
            var rt = Rect(name, parent, anchorMin, anchorMax, offsetMin, offsetMax);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
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
            track = Box(name + "Track", parent, trackColor, anchorMin, anchorMax, offsetMin, offsetMax);
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
            img.color = new Color(0.13f, 0.22f, 0.24f, 0.95f);

            labelText = Label(name + "Text", rt, label, size, TextAnchor.MiddleCenter,
                              Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            labelText.raycastTarget = false;

            if (onClick != null)
            {
                var trigger = rt.gameObject.AddComponent<EventTrigger>();
                var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
                entry.callback.AddListener(_ => onClick());
                trigger.triggers.Add(entry);
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

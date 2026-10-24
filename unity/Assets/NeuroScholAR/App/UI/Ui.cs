using System;
using UnityEngine;
using UnityEngine.UI;

namespace NeuroScholAR.App.UI
{
    /// <summary>
    /// Tiny code-only UI kit (uGUI, legacy Text with the built-in font). Building UI in code keeps the
    /// project free of hand-edited prefabs and makes every screen readable top to bottom in one file.
    /// Units: canvases are laid out in "design millimetres" and shrunk by <see cref="WorldScale"/>, so panels
    /// sit within arm's reach (for fingertip pokes) at about the angular size they had at 1.2 m.
    /// </summary>
    public static class Ui
    {
        public static readonly Color Background = new Color(0.08f, 0.09f, 0.13f, 0.92f);
        public static readonly Color Panel = new Color(0.14f, 0.16f, 0.22f, 0.95f);
        public static readonly Color Accent = new Color(0.25f, 0.55f, 0.95f);
        public static readonly Color TextColor = new Color(0.93f, 0.94f, 0.97f);
        public static readonly Color Muted = new Color(0.65f, 0.68f, 0.75f);
        public static readonly Color Green = new Color(0.2f, 0.78f, 0.35f);
        public static readonly Color Yellow = new Color(0.98f, 0.78f, 0.15f);
        public static readonly Color Red = new Color(0.92f, 0.26f, 0.24f);

        /// <summary>Metres per canvas unit. 0.45 mm: a 900-unit panel is 40 cm wide, comfortable at 55 cm.</summary>
        public const float WorldScale = 0.00045f;

        private static Font _font;
        public static Font Font => _font ??= Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        /// <summary>
        /// World-space canvas, sized in millimetres. Raycastable by touch and mouse; when
        /// <paramref name="interactive"/>, also gets a thin collider so gaze, hand rays and fingertips find it.
        /// </summary>
        public static Canvas WorldCanvas(string name, Transform parent, Vector2 sizeMm, Camera eventCamera, bool interactive = true)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = eventCamera;
            go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 4;
            go.AddComponent<GraphicRaycaster>();
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = sizeMm;
            rect.localScale = Vector3.one * WorldScale;
            if (interactive)
            {
                // Starts at the canvas plane and extends behind it: gaze rays hit the visible face, and a fingertip
                // pushing a button "in" stays inside the collider.
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(sizeMm.x, sizeMm.y, 100);
                box.center = new Vector3(0, 0, 50);
            }

            return canvas;
        }

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static Image Box(string name, Transform parent, Color color)
        {
            var image = Rect(name, parent).gameObject.AddComponent<Image>();
            image.color = color;
            return image;
        }

        /// <summary>Vertical stack that sizes itself to its children.</summary>
        public static RectTransform Column(string name, Transform parent, float spacing = 12, int padding = 0, Color? background = null)
        {
            var rect = background.HasValue ? Box(name, parent, background.Value).rectTransform : Rect(name, parent);
            var layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return rect;
        }

        public static RectTransform Row(string name, Transform parent, float spacing = 12)
        {
            var rect = Rect(name, parent);
            var layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.MiddleCenter;
            return rect;
        }

        public static Text Label(Transform parent, string text, int size = 28, Color? color = null,
            TextAnchor anchor = TextAnchor.MiddleLeft, FontStyle style = FontStyle.Normal)
        {
            var label = Rect("Text", parent).gameObject.AddComponent<Text>();
            label.font = Font;
            label.text = text;
            label.fontSize = size;
            label.fontStyle = style;
            label.color = color ?? TextColor;
            label.alignment = anchor;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = false;
            return label;
        }

        public static Button Button(Transform parent, string text, Action onClick, Color? color = null, float height = 64, int fontSize = 28)
        {
            var image = Box("Button " + text, parent, color ?? Accent);
            var button = image.gameObject.AddComponent<Button>();
            var colors = button.colors;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f); // gaze hover is visible
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.6f);
            button.colors = colors;
            button.onClick.AddListener(() => onClick());
            image.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            var label = Label(image.transform, text, fontSize, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            Stretch(label.rectTransform);
            return button;
        }

        public static void SetButtonText(Button button, string text) => button.GetComponentInChildren<Text>().text = text;

        public static InputField Input(Transform parent, string placeholder, float height = 60)
        {
            var image = Box("Input", parent, new Color(1, 1, 1, 0.95f));
            image.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            var text = Label(image.transform, string.Empty, 26, Color.black);
            text.supportRichText = false;
            Stretch(text.rectTransform, 12);
            var hint = Label(image.transform, placeholder, 26, new Color(0, 0, 0, 0.4f), style: FontStyle.Italic);
            Stretch(hint.rectTransform, 12);
            var input = image.gameObject.AddComponent<InputField>();
            input.textComponent = text;
            input.placeholder = hint;
            input.characterLimit = 300;
            return input;
        }

        /// <summary>Horizontal fill bar (0..1). Returns the fill image; set its color and call <see cref="SetFill"/>.</summary>
        public static Image Bar(Transform parent, float height, Color color)
        {
            var back = Box("Bar", parent, new Color(1, 1, 1, 0.15f));
            back.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            var fill = Box("Fill", back.transform, color);
            fill.rectTransform.anchorMin = Vector2.zero;
            fill.rectTransform.anchorMax = new Vector2(0, 1);
            fill.rectTransform.offsetMin = fill.rectTransform.offsetMax = Vector2.zero;
            return fill;
        }

        public static void SetFill(Image fill, float value) =>
            fill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(value), 1);

        public static void Stretch(RectTransform rect, float inset = 0)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        public static LayoutElement Height(Component c, float height)
        {
            var e = c.GetComponent<LayoutElement>() ?? c.gameObject.AddComponent<LayoutElement>();
            e.preferredHeight = height;
            return e;
        }

        public static void Clear(Transform t)
        {
            for (var i = t.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
            }
        }

        public static string Clock(double seconds)
        {
            var s = Math.Max(0, (int)seconds);
            return $"{s / 60:00}:{s % 60:00}";
        }
    }
}

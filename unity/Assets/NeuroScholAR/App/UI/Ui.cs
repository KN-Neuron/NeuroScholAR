using System;
using UnityEngine;
using UnityEngine.UI;

namespace NeuroScholAR.App.UI
{
    public static class Ui
    {
        // GLASS & PANEL COLOR PALETTE
        public static readonly Color Background = new Color(0.0f, 0.0f, 0.0f, 0.0f);
        public static readonly Color Glass = new Color(0.10f, 0.12f, 0.18f, 0.75f);   // Translucent outer frame
        public static readonly Color Panel = new Color(0.18f, 0.20f, 0.28f, 0.85f);   // Inner card background
        public static readonly Color Accent = new Color(0.55f, 0.38f, 0.85f, 0.95f);  // Soft Violet / Purple
        public static readonly Color TextColor = new Color(0.96f, 0.97f, 1.00f, 1.0f);
        public static readonly Color Muted = new Color(0.72f, 0.75f, 0.82f, 0.85f);
        public static readonly Color Green = new Color(0.24f, 0.78f, 0.45f, 0.95f);
        public static readonly Color Yellow = new Color(0.98f, 0.78f, 0.15f);
        public static readonly Color Red = new Color(0.92f, 0.26f, 0.24f);

        public const float WorldScale = 0.00045f;

        private static Font _font;
        public static Font Font => _font ??= Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // Runtime 9-sliced rounded sprite generator
        private static Sprite _roundedSprite;
        public static Sprite RoundedSprite
        {
            get
            {
                if (_roundedSprite != null) return _roundedSprite;

                int size = 64;
                int radius = 16;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                var colors = new Color32[size * size];

                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float cx = x < radius ? radius - x : (x >= size - radius ? x - (size - 1 - radius) : 0);
                        float cy = y < radius ? radius - y : (y >= size - radius ? y - (size - 1 - radius) : 0);
                        float distSq = cx * cx + cy * cy;

                        if (distSq > radius * radius)
                        {
                            colors[y * size + x] = new Color32(0, 0, 0, 0);
                        }
                        else if (distSq > (radius - 1.5f) * (radius - 1.5f))
                        {
                            float alpha = Mathf.Clamp01(radius - Mathf.Sqrt(distSq));
                            byte a = (byte)(alpha * 255);
                            colors[y * size + x] = new Color32(255, 255, 255, a);
                        }
                        else
                        {
                            colors[y * size + x] = new Color32(255, 255, 255, 255);
                        }
                    }
                }

                tex.SetPixels32(colors);
                tex.Apply();
                Vector4 border = new Vector4(radius, radius, radius, radius);
                _roundedSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
                return _roundedSprite;
            }
        }

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
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(sizeMm.x, sizeMm.y, 100);
                box.center = new Vector3(0, 0, 50);
            }
            return canvas;
        }

        /// <summary>Resizes a world canvas made by <see cref="WorldCanvas"/>, keeping its touch collider in sync.</summary>
        public static void SetCanvasSize(Canvas canvas, Vector2 sizeMm)
        {
            var rect = (RectTransform)canvas.transform;
            if (rect.sizeDelta == sizeMm)
            {
                return;
            }

            rect.sizeDelta = sizeMm;
            if (canvas.TryGetComponent<BoxCollider>(out var box))
            {
                box.size = new Vector3(sizeMm.x, sizeMm.y, 100);
            }
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
            image.sprite = RoundedSprite;
            image.type = Image.Type.Sliced;
            return image;
        }

        public static RectTransform Column(string name, Transform parent, float spacing = 14, int padding = 20, Color? background = null)
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

        public static Button Button(Transform parent, string text, Action onClick, Color? color = null, float height = 60, int fontSize = 24)
        {
            var image = Box("Button " + text, parent, color ?? Accent);
            var button = image.gameObject.AddComponent<Button>();
            var colors = button.colors;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f);
            colors.pressedColor = new Color(0.85f, 0.85f, 0.85f);
            colors.disabledColor = new Color(0.40f, 0.40f, 0.45f, 0.5f);
            button.colors = colors;
            button.onClick.AddListener(() => onClick?.Invoke());
            image.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            var label = Label(image.transform, text, fontSize, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            Stretch(label.rectTransform);
            return button;
        }

        public static void SetButtonText(Button button, string text) => button.GetComponentInChildren<Text>().text = text;

        public static InputField Input(Transform parent, string placeholder, float height = 56)
        {
            var image = Box("Input", parent, new Color(1, 1, 1, 0.90f));
            image.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            var text = Label(image.transform, string.Empty, 24, Color.black);
            text.supportRichText = false;
            Stretch(text.rectTransform, 12);
            var hint = Label(image.transform, placeholder, 24, new Color(0, 0, 0, 0.45f), style: FontStyle.Italic);
            Stretch(hint.rectTransform, 12);
            var input = image.gameObject.AddComponent<InputField>();
            input.textComponent = text;
            input.placeholder = hint;
            input.characterLimit = 300;
            return input;
        }

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
                var child = t.GetChild(i);
                child.SetParent(null);
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        public static string Clock(double seconds)
        {
            var s = Math.Max(0, (int)seconds);
            return $"{s / 60:00}:{s % 60:00}";
        }
    }
}
using System;
using UnityEngine;
using UnityEngine.UI;

namespace NeuroScholAR.App.UI
{
    public static class Ui
    {
        // GLASS PALETTE
        // XREAL is an additive display: it can only add light, black is fully see-through. So "glass" is a faint
        // purple tint (low alpha, the room stays visible) plus a thin bright edge that tells the eye where the panel is.
        public static readonly Color Background = new Color(0.0f, 0.0f, 0.0f, 0.0f);
        public static readonly Color Glass = new Color(0.28f, 0.20f, 0.50f, 0.22f);    // outer frame: barely tinted
        public static readonly Color Panel = new Color(0.40f, 0.30f, 0.66f, 0.40f);    // cards, HUD pills, secondary buttons
        public static readonly Color Raised = new Color(0.62f, 0.50f, 0.95f, 0.38f);   // buttons sitting on a card
        public static readonly Color EdgeColor = new Color(0.82f, 0.72f, 1.00f, 0.60f); // thin glass rim
        public static readonly Color Accent = new Color(0.55f, 0.38f, 0.85f, 0.95f);   // Soft Violet / Purple
        public static readonly Color AccentSoft = new Color(0.80f, 0.70f, 1.00f, 1.0f); // small caps labels on glass
        public static readonly Color TextColor = new Color(0.96f, 0.97f, 1.00f, 1.0f);
        public static readonly Color Muted = new Color(0.72f, 0.75f, 0.82f, 0.85f);
        public static readonly Color Green = new Color(0.24f, 0.78f, 0.45f, 0.95f);
        public static readonly Color Yellow = new Color(0.98f, 0.78f, 0.15f);
        public static readonly Color Red = new Color(0.92f, 0.26f, 0.24f);

        public const float WorldScale = 0.00045f;

        private const int SpriteSize = 64;
        private const float SpriteRadius = 16;
        private const float EdgeStroke = 2.5f;

        private static Font _font;
        public static Font Font => _font ??= Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        private static Sprite _roundedSprite;
        private static Sprite _outlineSprite;

        /// <summary>9-sliced filled rounded rectangle (corner radius 16 units; change per image with <see cref="Corners"/>).</summary>
        public static Sprite RoundedSprite => _roundedSprite ??= MakeRounded(ring: false);

        /// <summary>Same shape as <see cref="RoundedSprite"/>, only a thin rim: the glass edge.</summary>
        public static Sprite OutlineSprite => _outlineSprite ??= MakeRounded(ring: true);

        /// <summary>Anti-aliased rounded rect from its signed distance field, generated once at runtime.</summary>
        private static Sprite MakeRounded(bool ring)
        {
            var tex = new Texture2D(SpriteSize, SpriteSize, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var colors = new Color32[SpriteSize * SpriteSize];
            var half = SpriteSize / 2f;
            for (var y = 0; y < SpriteSize; y++)
            {
                for (var x = 0; x < SpriteSize; x++)
                {
                    // Distance from the rounded-rect edge: negative inside, positive outside.
                    var qx = Mathf.Abs(x + 0.5f - half) - (half - SpriteRadius);
                    var qy = Mathf.Abs(y + 0.5f - half) - (half - SpriteRadius);
                    var outside = new Vector2(Mathf.Max(qx, 0), Mathf.Max(qy, 0)).magnitude;
                    var d = outside + Mathf.Min(Mathf.Max(qx, qy), 0) - SpriteRadius;
                    var alpha = Mathf.Clamp01(0.5f - d);
                    if (ring)
                    {
                        alpha *= Mathf.Clamp01(d + EdgeStroke + 0.5f);
                    }

                    colors[y * SpriteSize + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
                }
            }

            tex.SetPixels32(colors);
            tex.Apply();
            var border = new Vector4(SpriteRadius, SpriteRadius, SpriteRadius, SpriteRadius);
            return Sprite.Create(tex, new Rect(0, 0, SpriteSize, SpriteSize), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
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

        /// <summary>Corner radius in canvas units (default 16). Half the height gives a pill. Call before <see cref="AddEdge"/>.</summary>
        public static void Corners(Image image, float radius) => image.pixelsPerUnitMultiplier = SpriteRadius / Mathf.Max(1, radius);

        /// <summary>Thin bright rim over a box: what makes a faint tint read as a glass panel on the glasses.</summary>
        public static Image AddEdge(Image box, Color? color = null)
        {
            var edge = Rect("Edge", box.transform).gameObject.AddComponent<Image>();
            edge.sprite = OutlineSprite;
            edge.type = Image.Type.Sliced;
            edge.pixelsPerUnitMultiplier = box.pixelsPerUnitMultiplier;
            edge.color = color ?? EdgeColor;
            edge.raycastTarget = false;
            edge.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            edge.transform.SetAsFirstSibling(); // over the fill, under the content
            Stretch(edge.rectTransform);
            return edge;
        }

        /// <summary>Glass card or pill: tinted rounded box with an edge and a layout group. Display only (no raycasts).</summary>
        public static RectTransform Card(string name, Transform parent, Color fill, float radius, int padX, int padY, float spacing, bool horizontal)
        {
            var box = Box(name, parent, fill);
            box.raycastTarget = false;
            Corners(box, radius);
            AddEdge(box);
            var layout = horizontal
                ? (HorizontalOrVerticalLayoutGroup)box.gameObject.AddComponent<HorizontalLayoutGroup>()
                : box.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = new RectOffset(padX, padX, padY, padY);
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = !horizontal;
            layout.childForceExpandHeight = false;
            layout.childAlignment = horizontal ? TextAnchor.MiddleCenter : TextAnchor.UpperLeft;
            return box.rectTransform;
        }

        public static RectTransform Column(string name, Transform parent, float spacing = 14, int padding = 20, Color? background = null, bool edge = false)
        {
            RectTransform rect;
            if (background.HasValue)
            {
                var box = Box(name, parent, background.Value);
                if (edge)
                {
                    AddEdge(box);
                }

                rect = box.rectTransform;
            }
            else
            {
                rect = Rect(name, parent);
            }

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
            AddEdge(image, new Color(1, 1, 1, 0.22f));
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

        /// <summary>Progress bar; returns the fill. Corners follow the height, so thin bars stay rounded.</summary>
        public static Image Bar(Transform parent, float height, Color color)
        {
            var back = Box("Bar", parent, new Color(1, 1, 1, 0.15f));
            back.raycastTarget = false;
            Corners(back, Mathf.Min(SpriteRadius, height / 2));
            back.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            var fill = Box("Fill", back.transform, color);
            fill.raycastTarget = false;
            fill.pixelsPerUnitMultiplier = back.pixelsPerUnitMultiplier;
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

using UnityEngine;
using UnityEngine.UI;

namespace NeuroScholAR.App.UI
{
    /// <summary>
    /// White glass loading spinner: a faint frosted ring with a thin bright rim, and a white arc whose tail fades out,
    /// led by a small glowing dot. XREAL can only add light, so white is the brightest it shows: the arc glows and the
    /// faint ring reads as glass, matching the purple glass panels. Both sprites are generated once (anti-aliased,
    /// like <see cref="Ui.RoundedSprite"/>); turning is a transform rotation, so nothing is allocated per frame.
    /// Create with <see cref="Ui.Spinner"/>.
    /// </summary>
    public sealed class GlassSpinner : MonoBehaviour
    {
        private const float TurnsPerSecond = 1f;
        private const int SpriteSize = 128;
        private const float Thickness = 9f;    // ring width in sprite pixels
        private const float ArcDegrees = 110f; // bright head to fully faded tail
        private const float TrackAlpha = 0.16f;
        private const float RimAlpha = 0.55f;

        private static Sprite _track;
        private static Sprite _arc;

        private RectTransform _rotor;

        public static RectTransform Create(Transform parent, float size)
        {
            var root = Ui.Rect("Spinner", parent);
            root.sizeDelta = new Vector2(size, size);
            AddImage(root, _track ??= MakeSprite(arc: false));

            var rotor = Ui.Rect("Arc", root);
            Ui.Stretch(rotor);
            AddImage(rotor, _arc ??= MakeSprite(arc: true));

            root.gameObject.AddComponent<GlassSpinner>()._rotor = rotor;
            return root;
        }

        private void Update() =>
            _rotor.localRotation = Quaternion.Euler(0, 0, -Time.unscaledTime * 360f * TurnsPerSecond); // clockwise

        private static void AddImage(RectTransform rect, Sprite sprite)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.color = Color.white;
            image.preserveAspect = true;
            image.raycastTarget = false;
        }

        /// <summary>
        /// Track: frosted ring plus a bright outer rim. Arc: the ring with an angular fade behind a head at 12 o'clock,
        /// plus a round glowing head dot. The tail trails counter-clockwise, so turning clockwise the dot leads.
        /// </summary>
        private static Sprite MakeSprite(bool arc)
        {
            var tex = new Texture2D(SpriteSize, SpriteSize, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var colors = new Color32[SpriteSize * SpriteSize];
            var half = SpriteSize / 2f;
            var outer = half - 3f; // room for the head's glow
            var inner = outer - Thickness;
            var middle = outer - Thickness / 2;
            for (var y = 0; y < SpriteSize; y++)
            {
                for (var x = 0; x < SpriteSize; x++)
                {
                    var dx = x + 0.5f - half;
                    var dy = y + 0.5f - half;
                    var r = Mathf.Sqrt(dx * dx + dy * dy);
                    var ring = Mathf.Clamp01(outer - r + 0.5f) * Mathf.Clamp01(r - inner + 0.5f); // anti-aliased annulus
                    float alpha;
                    if (!arc)
                    {
                        var rim = Mathf.Clamp01(1.25f - Mathf.Abs(r - (outer - 0.75f)));
                        alpha = Mathf.Max(ring * TrackAlpha, rim * RimAlpha);
                    }
                    else
                    {
                        var behind = Mathf.Repeat(Mathf.Atan2(dy, dx) * Mathf.Rad2Deg - 90f, 360f); // degrees behind the head
                        var tail = behind <= ArcDegrees ? Mathf.Pow(1 - behind / ArcDegrees, 1.6f) : 0;
                        var toHead = Mathf.Sqrt(dx * dx + (dy - middle) * (dy - middle));
                        var dot = Mathf.Clamp01(Thickness * 0.7f - toHead + 0.5f);
                        var glow = Mathf.Clamp01(1 - toHead / (Thickness * 2f)) * 0.35f;
                        alpha = Mathf.Max(ring * tail, Mathf.Max(dot, glow));
                    }

                    colors[y * SpriteSize + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
                }
            }

            tex.SetPixels32(colors);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, SpriteSize, SpriteSize), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}

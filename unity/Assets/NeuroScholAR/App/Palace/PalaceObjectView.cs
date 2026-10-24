using System.Text;
using UnityEngine;

namespace NeuroScholAR.App.Palace
{
    /// <summary>How a palace object is lit, in priority order over hover and "viewed".</summary>
    public enum Highlight
    {
        None,

        /// <summary>Test feedback: this is where the asked pair lives (pulses).</summary>
        Target,

        /// <summary>Test feedback: the user picked this one and it was right.</summary>
        Correct,

        /// <summary>Test feedback: the user picked this one and it was wrong.</summary>
        Wrong,
    }

    /// <summary>
    /// Visual state of one palace object: hover, test highlight, "already viewed" tint, and a floating label
    /// that turns to face the user as they walk around the room.
    /// </summary>
    public sealed class PalaceObjectView : MonoBehaviour
    {
        private const int WrapColumns = 28;
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private static Font _font;

        private Renderer[] _renderers;
        private MaterialPropertyBlock _block;
        private TextMesh _label;
        private Transform _labelTransform;
        private string _labelSource;
        private bool _hovered;
        private bool _viewed;
        private Highlight _highlight;

        public int Index { get; private set; }

        /// <summary>Seconds the gaze has rested on this object without a break (used for "look to learn").</summary>
        public float GazeSeconds { get; set; }

        public void Init(int index, Renderer[] renderers, float labelHeight)
        {
            Index = index;
            _renderers = renderers;
            _block = new MaterialPropertyBlock();
            _font ??= Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var labelGo = new GameObject("Label");
            _labelTransform = labelGo.transform;
            _labelTransform.SetParent(transform, false);
            _labelTransform.localPosition = new Vector3(0, labelHeight, 0);
            _label = labelGo.AddComponent<TextMesh>();
            _label.font = _font;
            labelGo.GetComponent<MeshRenderer>().sharedMaterial = _font.material;
            _label.anchor = TextAnchor.LowerCenter;
            _label.alignment = TextAlignment.Center;
            _label.characterSize = 0.006f;
            _label.fontSize = 64;
            SetLabel("?");
            Apply(0);
        }

        public bool Hovered => _hovered;

        public void SetLabel(string text)
        {
            text ??= string.Empty;
            if (text != _labelSource)
            {
                _labelSource = text;
                _label.text = Wrap(text);
            }
        }

        public void SetHovered(bool hovered)
        {
            if (_hovered != hovered)
            {
                _hovered = hovered;
                Apply(0);
            }
        }

        public void SetViewed(bool viewed)
        {
            if (_viewed != viewed)
            {
                _viewed = viewed;
                Apply(0);
            }
        }

        public void SetHighlight(Highlight highlight)
        {
            if (_highlight != highlight)
            {
                _highlight = highlight;
                Apply(0);
            }
        }

        private void Update()
        {
            if (_highlight == Highlight.Target)
            {
                Apply(0.5f + 0.5f * Mathf.Sin(Time.time * 6f));
            }
        }

        private void LateUpdate()
        {
            var cam = Camera.main;
            if (cam != null && _label.text.Length > 0)
            {
                // Billboard around Y only, so text stays upright; TextMesh reads correctly when its +Z points away from the viewer.
                var away = _labelTransform.position - cam.transform.position;
                away.y = 0;
                if (away.sqrMagnitude > 1e-4f)
                {
                    _labelTransform.rotation = Quaternion.LookRotation(away);
                }
            }
        }

        private void Apply(float pulse)
        {
            var emission = _highlight switch
            {
                Highlight.Target => new Color(1f, 0.85f, 0.2f) * (0.4f + 1.2f * pulse),
                Highlight.Correct => new Color(0.2f, 0.9f, 0.3f),
                Highlight.Wrong => new Color(0.95f, 0.2f, 0.15f),
                _ => _hovered ? new Color(0.35f, 0.6f, 1f) * 0.6f : _viewed ? new Color(0.1f, 0.35f, 0.1f) : Color.black,
            };

            _block.SetColor(EmissionColor, emission);
            foreach (var r in _renderers)
            {
                r.SetPropertyBlock(_block);
            }
        }

        /// <summary>Greedy word wrap for TextMesh, which has no wrapping of its own. Keeps explicit line breaks.</summary>
        private static string Wrap(string text)
        {
            var sb = new StringBuilder(text.Length + 8);
            foreach (var line in text.Split('\n'))
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                var column = 0;
                foreach (var word in line.Split(' '))
                {
                    if (column > 0 && column + 1 + word.Length > WrapColumns)
                    {
                        sb.Append('\n');
                        column = 0;
                    }
                    else if (column > 0)
                    {
                        sb.Append(' ');
                        column++;
                    }

                    sb.Append(word);
                    column += word.Length;
                }
            }

            return sb.ToString();
        }
    }
}

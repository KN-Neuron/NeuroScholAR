using System.Text;
using UnityEngine;

namespace NeuroScholAR.App.Palace
{
    /// <summary>How a palace object is lit, in priority order over hover and "viewed".</summary>
    public enum Highlight
    {
        None,

        /// <summary>Test feedback: this is where the asked pair lives (pulses gold to white, with a beam and a larger label).</summary>
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
        private const float LabelSize = 0.006f;
        private const float TargetLabelSize = 0.009f; // the right answer's label: readable from further away

        // "Here it is" beam over the right object after a wrong answer: a thin gold column above the label, visible
        // across the room even when the object itself is small or half hidden behind furniture.
        private const float BeamGap = 0.25f;    // above the label anchor, clear of a two- or three-line label
        private const float BeamHeight = 1.2f;
        private const float BeamWidth = 0.04f;
        private const float PulseSpeed = 6f;    // radians per second: about one pulse per second

        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private static readonly Color TargetGold = new Color(1f, 0.85f, 0.25f);
        private static readonly Color TargetWhite = new Color(1f, 1f, 0.85f);
        private static Font _font;

        private Renderer[] _renderers;
        private MaterialPropertyBlock _block;
        private GameObject _beam;
        private Material _beamMaterial;
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
            _label.characterSize = LabelSize;
            _label.fontSize = 64;

            _beam = GameObject.CreatePrimitive(PrimitiveType.Cylinder); // 2 m tall at scale 1
            _beam.name = "Beam";
            Destroy(_beam.GetComponent<Collider>()); // must never catch gaze rays or fingertips
            _beam.transform.SetParent(transform, false);
            _beam.transform.localPosition = new Vector3(0, labelHeight + BeamGap + BeamHeight / 2, 0);
            _beam.transform.localScale = new Vector3(BeamWidth, BeamHeight / 2, BeamWidth);
            _beamMaterial = DevMaterials.Transparent(TargetGold);
            _beam.GetComponent<Renderer>().sharedMaterial = _beamMaterial;
            _beam.SetActive(false);

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
                var target = highlight == Highlight.Target;
                _beam.SetActive(target);
                _label.characterSize = target ? TargetLabelSize : LabelSize;
                _label.color = target ? TargetGold : Color.white;
                Apply(0);
            }
        }

        private void Update()
        {
            if (_highlight == Highlight.Target)
            {
                var pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * PulseSpeed);
                Apply(pulse);
                _beamMaterial.color = new Color(TargetGold.r, TargetGold.g, TargetGold.b, 0.35f + 0.45f * pulse);
            }
        }

        private void OnDestroy()
        {
            if (_beamMaterial != null)
            {
                Destroy(_beamMaterial); // one per object; objects are rebuilt every session
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
                // From strong gold to nearly white: the brightest thing in the room on the additive glasses.
                Highlight.Target => Color.Lerp(TargetGold * 0.8f, TargetWhite, pulse) * (1f + pulse),
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

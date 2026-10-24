using NeuroScholAR.App.Palace;
using UnityEngine;

namespace NeuroScholAR.App.UI
{
    /// <summary>
    /// Session menu on the left palm: appears when the user turns their left palm towards their face, floats
    /// just above the hand, and is poked with the right index finger. Out of sight the rest of the time.
    /// Never shown otherwise: hands down or out of view means no menu. Only on a device without hand tracking
    /// at all (Editor, phone) it falls back to a body-locked panel low in the view.
    /// </summary>
    public sealed class PalmMenu : MonoBehaviour
    {
        private const float ShowFacing = 0.75f; // cos of the palm-to-eyes angle needed to open (~41°)
        private const float HideFacing = 0.45f; // hysteresis, so it doesn't flicker at the edge
        private const float ShowInView = 0.8f;  // cos of the angle between gaze and palm (~37°): you are looking at it
        private const float HideInView = 0.55f;
        private const float MaxReach = 0.75f;   // palm further than this from the eyes isn't "looking at my hand"
        private const float OpenHoldSeconds = 0.25f; // a passing glance at the palm doesn't open it
        private const float AbovePalm = 0.11f;  // metres along the fingers
        private const float OffPalm = 0.04f;    // metres out of the palm, so the finger doesn't hit the hand first

        public Transform Head;
        public Interactor Hands;

        private Canvas _canvas;
        private Collider _collider;
        private LazyFollow _fallback;
        private bool _open;
        private float _wantOpenSince = float.NegativeInfinity;

        private void Awake()
        {
            _canvas = GetComponent<Canvas>();
            _collider = GetComponent<Collider>();
            _fallback = gameObject.AddComponent<LazyFollow>();
            _fallback.Distance = 0.6f;
            _fallback.HeightOffset = -0.35f;
            _fallback.enabled = false;
        }

        private void OnEnable() => _fallback?.SnapNow();

        private void LateUpdate()
        {
            if (Head == null || Hands == null)
            {
                return;
            }

            _fallback.Head = Head;
            var noHandTracking = !Hands.HandTrackingAvailable;
            _fallback.enabled = noHandTracking;
            if (noHandTracking)
            {
                Show(true);
                return;
            }

            if (!Hands.TryGetLeftPalm(out var palm, out var normal, out var fingers))
            {
                Show(false);
                return;
            }

            var toPalm = palm - Head.position;
            var facing = Vector3.Dot(normal, -toPalm.normalized);
            var inView = Vector3.Dot(Head.forward, toPalm.normalized);
            var near = toPalm.magnitude < MaxReach;
            if (_open)
            {
                _open = facing > HideFacing && inView > HideInView && near;
            }
            else if (facing > ShowFacing && inView > ShowInView && near)
            {
                if (float.IsNegativeInfinity(_wantOpenSince))
                {
                    _wantOpenSince = Time.time;
                }

                _open = Time.time - _wantOpenSince >= OpenHoldSeconds;
            }

            if (!_open)
            {
                _wantOpenSince = facing > ShowFacing && inView > ShowInView && near ? _wantOpenSince : float.NegativeInfinity;
            }

            Show(_open);
            if (_open)
            {
                var position = palm + fingers * AbovePalm + normal * OffPalm;
                transform.SetPositionAndRotation(position, Quaternion.LookRotation(position - Head.position, Vector3.up));
            }
        }

        private void Show(bool visible)
        {
            _canvas.enabled = visible;
            if (_collider != null)
            {
                _collider.enabled = visible;
            }
        }
    }
}

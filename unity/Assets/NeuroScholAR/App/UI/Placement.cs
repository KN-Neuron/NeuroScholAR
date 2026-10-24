using UnityEngine;

namespace NeuroScholAR.App.UI
{
    /// <summary>World-locked UI placement helpers. Panels sit in front of the user's heading (yaw only, so they stay upright).</summary>
    public static class Placement
    {
        public static void InFront(Transform target, Transform head, float distance, float heightOffset)
        {
            var forward = head.forward;
            forward.y = 0;
            if (forward.sqrMagnitude < 1e-4f)
            {
                forward = Vector3.forward; // looking straight up or down
            }

            forward.Normalize();
            target.position = head.position + forward * distance + Vector3.up * heightOffset;
            target.rotation = Quaternion.LookRotation(forward);
        }
    }

    /// <summary>
    /// Body-locked panel: stays put while the user looks around, and glides back in front when they turn
    /// away by more than <see cref="Threshold"/> degrees, walk off, or their head height changes a lot (also
    /// fixes panels placed before head tracking had started). Within arm's reach, so buttons can be poked.
    /// </summary>
    public sealed class LazyFollow : MonoBehaviour
    {
        public Transform Head;
        public float Distance = 0.5f;
        public float HeightOffset = -0.22f;
        private const float HeightTolerance = 0.25f;
        public float Threshold = 40f;
        public float Speed = 3f;

        /// <summary>
        /// Follow where the user looks, up and down too (prompts that must be seen: the glasses' view is narrow).
        /// <see cref="HeightOffset"/> is ignored; use a small <see cref="Threshold"/>, just above the panel's half-width.
        /// </summary>
        public bool FollowGaze;

        private bool _moving;

        private void LateUpdate()
        {
            if (Head == null)
            {
                return;
            }

            var forward = Head.forward;
            if (!FollowGaze)
            {
                forward.y = 0;
            }

            if (forward.sqrMagnitude < 1e-4f)
            {
                return;
            }

            forward.Normalize();
            var current = transform.position - Head.position;
            var heightOff = !FollowGaze && Mathf.Abs(current.y - HeightOffset) > HeightTolerance;
            if (!FollowGaze)
            {
                current.y = 0;
            }

            var angle = Vector3.Angle(current, forward);
            var distance = current.magnitude;
            if (angle > Threshold || heightOff || distance < Distance * 0.5f || distance > Distance * 1.6f)
            {
                _moving = true;
            }

            if (!_moving)
            {
                return;
            }

            var targetPos = Head.position + forward * Distance + (FollowGaze ? Vector3.zero : Vector3.up * HeightOffset);
            var targetRot = Quaternion.LookRotation(forward, Vector3.up);
            var t = 1 - Mathf.Exp(-Speed * Time.deltaTime);
            transform.SetPositionAndRotation(Vector3.Lerp(transform.position, targetPos, t), Quaternion.Slerp(transform.rotation, targetRot, t));
            if (angle < 2f && (transform.position - targetPos).sqrMagnitude < 1e-4f)
            {
                _moving = false;
            }
        }

        public void SnapNow()
        {
            if (Head == null)
            {
                return;
            }

            if (FollowGaze)
            {
                transform.SetPositionAndRotation(Head.position + Head.forward * Distance, Quaternion.LookRotation(Head.forward, Vector3.up));
            }
            else
            {
                Placement.InFront(transform, Head, Distance, HeightOffset);
            }
        }
    }
}

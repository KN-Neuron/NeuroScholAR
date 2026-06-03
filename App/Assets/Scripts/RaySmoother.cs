using UnityEngine;

public class RaySmoother : MonoBehaviour
{
    [Tooltip("Drag your actual VR Controller here")]
    public Transform rawController;

    [Tooltip("Lower numbers = heavier smoothing (more lag). Higher = snappier.")]
    public float smoothSpeed = 15f;

    void Update()
    {
        if (rawController == null) return;

        // 1. Instantly snap to the hand's exact position so the laser doesn't lag behind
        transform.position = rawController.position;

        // 2. "Slerp" (Smoothly interpolate) the rotation to filter out the micro-shakes
        transform.rotation = Quaternion.Slerp(transform.rotation, rawController.rotation, Time.deltaTime * smoothSpeed);
    }
}
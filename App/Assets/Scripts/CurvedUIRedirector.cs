using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public class CurvedUIRedirector : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler,
    IPointerMoveHandler, IPointerDownHandler,
    IPointerUpHandler, IPointerClickHandler
{
    [Header("UI References")]
    [Tooltip("The flat Canvas that holds your actual buttons.")]
    public Canvas targetCanvas;

    [Tooltip("The orthographic camera pointing directly at the flat Canvas.")]
    public Camera dummyUICamera;

    [Header("Calibration Settings")]
    [Tooltip("Adjust this to scale the horizontal wrap area of your UI.")]
    public float horizontalScale = 1f;
    [Tooltip("Adjust this to shift the UI left or right along the curve.")]
    public float horizontalOffset = 0.5f;

    // Pass all XR Interaction Toolkit events into our redirector function
    public void OnPointerMove(PointerEventData eventData) => RedirectEvent(eventData, ExecuteEvents.pointerMoveHandler);
    public void OnPointerDown(PointerEventData eventData) => RedirectEvent(eventData, ExecuteEvents.pointerDownHandler);
    public void OnPointerUp(PointerEventData eventData) => RedirectEvent(eventData, ExecuteEvents.pointerUpHandler);
    public void OnPointerClick(PointerEventData eventData) => RedirectEvent(eventData, ExecuteEvents.pointerClickHandler);
    public void OnPointerEnter(PointerEventData eventData) => RedirectEvent(eventData, ExecuteEvents.pointerEnterHandler);
    public void OnPointerExit(PointerEventData eventData) => RedirectEvent(eventData, ExecuteEvents.pointerExitHandler);

    private void RedirectEvent<T>(PointerEventData eventData, ExecuteEvents.EventFunction<T> eventHandler) where T : IEventSystemHandler
    {
        if (targetCanvas == null || dummyUICamera == null) return;

        // Check where the XR Ray hit the 3D cylinder
        RaycastResult hit = eventData.pointerCurrentRaycast;
        if (hit.gameObject != this.gameObject) return;

        // Convert the global hit position into the Cylinder's local coordinate space
        Vector3 localHitPoint = transform.InverseTransformPoint(hit.worldPosition);

        // Calculate UV coordinates mathematically based on a standard Unity Cylinder geometry
        // Calculate the angle around the cylinder's Y-axis (-PI to PI)
        float angle = Mathf.Atan2(localHitPoint.x, localHitPoint.z);

        // Normalize angle to a 0.0 to 1.0 range (U coordinate)
        float u = (angle + Mathf.PI) / (2f * Mathf.PI);

        // Normalize local height (default Unity cylinder goes from Y = -1 to Y = 1) to a 0.0 to 1.0 range (V coordinate)
        float v = (localHitPoint.y + 1f) / 2f;

        // Apply our calibration variables to align the clicks with your visual canvas curve
        u = (u * horizontalScale) + horizontalOffset;

        Vector2 screenPosition = new Vector2(
            u * dummyUICamera.pixelWidth,
            v * dummyUICamera.pixelHeight
        );

        PointerEventData fakeEventData = new PointerEventData(EventSystem.current)
        {
            position = screenPosition,
            button = eventData.button,
            clickCount = eventData.clickCount,
            clickTime = eventData.clickTime,
            pointerId = eventData.pointerId,
            dragging = eventData.dragging
        };

        // Fire a Raycast against the flat Canvas UI
        List<RaycastResult> raycastResults = new List<RaycastResult>();
        EventSystem.current.RaycastAll(fakeEventData, raycastResults);

        // Execute the event on the correct UI element (Button, Input Field, etc.)
        foreach (var result in raycastResults)
        {
            if (result.gameObject.transform.IsChildOf(targetCanvas.transform))
            {
                ExecuteEvents.Execute(result.gameObject, fakeEventData, eventHandler);

                // Fixes the generic type comparison error by using safe system reflection
                if (typeof(T) == typeof(IPointerDownHandler))
                {
                    EventSystem.current.SetSelectedGameObject(result.gameObject);
                }
                break; // Stop after hitting the top-most UI element
            }
        }
    }
}
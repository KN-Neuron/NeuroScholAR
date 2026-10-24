using UnityEngine;
using UnityEngine.InputSystem;

namespace NeuroScholAR.App.Palace
{
    /// <summary>
    /// Stand-in for head tracking in the Editor (no XR device): hold the right mouse button and drag,
    /// or use the arrow keys. Space/Enter is the select button. Disabled in player builds.
    /// </summary>
    public sealed class EditorLook : MonoBehaviour
    {
        private const float MouseSpeed = 0.15f;
        private const float KeySpeed = 90f;

        private float _yaw;
        private float _pitch;

        private void Awake()
        {
            if (!Application.isEditor)
            {
                enabled = false;
            }
        }

        private void LateUpdate()
        {
            var mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.isPressed)
            {
                var delta = mouse.delta.ReadValue();
                _yaw += delta.x * MouseSpeed;
                _pitch -= delta.y * MouseSpeed;
            }

            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                var step = KeySpeed * Time.deltaTime;
                _yaw += (keyboard.rightArrowKey.isPressed ? step : 0) - (keyboard.leftArrowKey.isPressed ? step : 0);
                _pitch += (keyboard.downArrowKey.isPressed ? step : 0) - (keyboard.upArrowKey.isPressed ? step : 0);
            }

            _pitch = Mathf.Clamp(_pitch, -80, 80);
            transform.localRotation = Quaternion.Euler(_pitch, _yaw, 0);
        }
    }
}

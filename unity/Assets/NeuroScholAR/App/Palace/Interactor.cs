using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;
using UnityEngine.XR.Hands;

namespace NeuroScholAR.App.Palace
{
    /// <summary>
    /// All input in one place. Two kinds of pointer reach the same targets (UI buttons on world-space panels,
    /// palace objects):
    /// <list type="bullet">
    /// <item>Gaze: the head ray. Resting on an object reads it ("look to learn", <see cref="ObjectGazed"/>);
    /// the Beam Pro controller, a tap, a gamepad or Space/Enter (Editor) clicks what it points at.</item>
    /// <item>Poke: the index fingertip of each tracked hand. Buttons travel with the finger like real keys and
    /// click once pushed <see cref="PressDepth"/> in; touching a palace object selects it.</item>
    /// </list>
    /// Tracked hands are drawn with XR Hands' rigged hand meshes so the user sees their finger against the
    /// virtual buttons. UI is hit through a collider behind each interactive canvas; the hit point is resolved
    /// with the regular uGUI raycast, so buttons keep their normal hover/press colours.
    /// </summary>
    public sealed class Interactor : MonoBehaviour
    {
        private const float MaxDistance = 8f;
        private const float DwellSeconds = 1.2f;
        private const float ObjectPokeRadius = 0.015f;
        private const float ObjectReleaseRadius = 0.04f;
        private const float HoverDepth = 0.04f;    // fingertip this close in front of a panel hovers a button
        private const float PressDepth = 0.008f;   // pushed this far "into" the button: click
        private const float ReleaseDepth = 0.003f; // pulled back above this: ready for the next press
        private const float MaxTravel = 0.012f;    // how far the button visibly goes down

        private readonly List<RaycastResult> _uiHits = new List<RaycastResult>();
        private readonly List<Selectable> _selectables = new List<Selectable>();
        private readonly Collider[] _overlaps = new Collider[8];
        private readonly List<XRHandSubsystem> _handSubsystems = new List<XRHandSubsystem>();
        private readonly HashSet<PalaceObjectView> _hovered = new HashSet<PalaceObjectView>();
        private readonly HashSet<PalaceObjectView> _hoveredLastFrame = new HashSet<PalaceObjectView>();
        private readonly Finger _leftFinger = new Finger();
        private readonly Finger _rightFinger = new Finger();
        private Camera _camera;
        private RectTransform _reticle;
        private PointerEventData _gazeData;
        private GameObject _gazeUi;
        private PalaceObjectView _gazeObject;
        private PalaceObjectView _dwellTarget;
        private bool _dwellFired;
        private XRHandSubsystem _hands;
        private string _gazeHitName;
        private Vector3 _gazePoint;
        private float _gazeDistance;
        private Transform _gazeMarker;
        private Transform _leftMarker;
        private Transform _rightMarker;

        /// <summary>Palace object clicked (poke or select button).</summary>
        public event Action<int> ObjectSelected;

        /// <summary>The gaze has rested on a palace object long enough to read it. Fires once per look.</summary>
        public event Action<int> ObjectGazed;

        /// <summary>A select press that didn't hit a button (the calibration mini-game listens to this).</summary>
        public event Action AnyPress;

        /// <summary>The device has hand tracking running (the palm menu falls back to a floating menu only without it).</summary>
        public bool HandTrackingAvailable => _hands != null;

        /// <summary>Dev overlay line: what the gaze ray hits, hand tracking, fingertip poke depth.</summary>
        public string DevText =>
            $"Wzrok: {(_gazeHitName == null ? "nic" : $"{_gazeHitName} {_gazeDistance:F2} m")}" +
            $"{(_gazeUi != null ? $" (UI: {_gazeUi.name})" : string.Empty)}\n" +
            $"Ręce: {(_hands == null ? "brak śledzenia" : $"L {FingerText(_leftFinger)}, P {FingerText(_rightFinger)}")}";

        private static string FingerText(Finger f) =>
            !f.HasTip ? "–" : f.Target == null ? "śledzona" : $"{f.Target.name} {f.Depth * 1000:F0} mm{(f.Down ? " WCIŚNIĘTY" : string.Empty)}";

        public void Init(Camera cam, RectTransform reticle)
        {
            _camera = cam;
            _reticle = reticle;
            var trackingSpace = cam.transform.parent; // XR Origin's camera offset: hand poses are relative to it
            CreateHandMesh("Hands/LeftHand", Handedness.Left, trackingSpace);
            CreateHandMesh("Hands/RightHand", Handedness.Right, trackingSpace);
        }

        /// <summary>
        /// Left palm pose in world space: centre, normal pointing out of the palm, and "up" towards the fingers.
        /// False when the left hand isn't tracked.
        /// </summary>
        public bool TryGetLeftPalm(out Vector3 position, out Vector3 normal, out Vector3 fingers)
        {
            position = normal = fingers = default;
            if (_hands == null || !_hands.leftHand.isTracked)
            {
                return false;
            }

            var hand = _hands.leftHand;
            if (!TryJoint(hand, XRHandJointID.Palm, out position) || !TryJoint(hand, XRHandJointID.Wrist, out var wrist) ||
                !TryJoint(hand, XRHandJointID.MiddleProximal, out var middle) ||
                !TryJoint(hand, XRHandJointID.IndexProximal, out var index) ||
                !TryJoint(hand, XRHandJointID.LittleProximal, out var little))
            {
                return false;
            }

            // From geometry rather than joint axes, so it doesn't depend on the provider's axis convention.
            fingers = (middle - wrist).normalized;
            normal = Vector3.Cross(fingers, little - index).normalized; // left hand: out of the palm (verified on device)
            return true;
        }

        private void Update()
        {
            if (_camera == null || EventSystem.current == null)
            {
                return;
            }

            _hovered.Clear();
            UpdateGaze();
            UpdateDwell();

            _hands = RunningHands();
            if (_hands != null)
            {
                UpdateFinger(_leftFinger, _hands.leftHand);
                UpdateFinger(_rightFinger, _hands.rightHand);
            }
            else
            {
                _leftFinger.HasTip = _rightFinger.HasTip = false;
            }

            UpdateDevMarkers();

            foreach (var view in _hoveredLastFrame)
            {
                if (view != null && !_hovered.Contains(view))
                {
                    view.SetHovered(false);
                }
            }

            foreach (var view in _hovered)
            {
                view.SetHovered(true);
            }

            _hoveredLastFrame.Clear();
            _hoveredLastFrame.UnionWith(_hovered);

            if (_reticle != null)
            {
                _reticle.localScale = Vector3.one * (_gazeUi != null || _gazeObject != null ? 1.6f : 1f);
            }
        }

        // ---------- Gaze ----------

        private void UpdateGaze()
        {
            _gazeData ??= new PointerEventData(EventSystem.current);
            var head = _camera.transform;
            GameObject ui = null;
            PalaceObjectView view = null;
            _gazeHitName = null;
            if (Physics.Raycast(new Ray(head.position, head.forward), out var hit, MaxDistance))
            {
                _gazeHitName = hit.collider.name;
                _gazePoint = hit.point;
                _gazeDistance = hit.distance;
                view = hit.collider.GetComponentInParent<PalaceObjectView>();
                if (view == null && hit.collider.TryGetComponent<Canvas>(out var canvas))
                {
                    ui = UiAt(canvas, hit.point, _gazeData);
                }
            }

            SetHover(ref _gazeUi, ui, _gazeData);
            _gazeObject = view;
            if (view != null)
            {
                _hovered.Add(view);
            }

            if (SelectPressed())
            {
                Click(_gazeUi, view, _gazeData);
            }
        }

        private void UpdateDwell()
        {
            var view = _gazeObject;
            if (view != _dwellTarget)
            {
                if (_dwellTarget != null)
                {
                    _dwellTarget.GazeSeconds = 0;
                }

                _dwellTarget = view;
                _dwellFired = false;
            }

            if (view == null || _dwellFired)
            {
                return;
            }

            view.GazeSeconds += Time.deltaTime;
            if (view.GazeSeconds >= DwellSeconds)
            {
                _dwellFired = true;
                ObjectGazed?.Invoke(view.Index);
            }
        }

        // ---------- Poke ----------

        private void UpdateFinger(Finger finger, XRHand hand)
        {
            if (!hand.isTracked || !TryJoint(hand, XRHandJointID.IndexTip, out var tip))
            {
                finger.HasTip = false;
                finger.Release();
                return;
            }

            finger.HasTip = true;
            finger.Tip = tip;

            finger.Data ??= new PointerEventData(EventSystem.current);
            PokeButtons(finger, tip);
            PokeObjects(finger, tip);
        }

        /// <summary>
        /// Finds the button under the fingertip and how deep the finger is pushed into its panel; the button
        /// follows the finger down and clicks at <see cref="PressDepth"/>. A press must start in front of the
        /// panel, so reaching through it from behind never clicks.
        /// </summary>
        private void PokeButtons(Finger finger, Vector3 tip)
        {
            GameObject target = null;
            var depth = 0f;
            var count = Physics.OverlapSphereNonAlloc(tip, HoverDepth, _overlaps);
            for (var i = 0; i < count && target == null; i++)
            {
                if (!_overlaps[i].TryGetComponent<Canvas>(out var canvas))
                {
                    continue;
                }

                var panel = canvas.transform;
                var local = panel.InverseTransformPoint(tip);
                depth = local.z * panel.lossyScale.z; // canvas +Z points away from the viewer: positive = pushed in
                if (depth < -HoverDepth || depth > HoverDepth || !((RectTransform)panel).rect.Contains(local))
                {
                    continue;
                }

                target = PokeTarget(canvas, tip);
            }

            if (target != finger.Target)
            {
                finger.Release();
                finger.Target = target;
                finger.Armed = false;
                if (target != null)
                {
                    ExecuteEvents.Execute(target, finger.Data, ExecuteEvents.pointerEnterHandler);
                }
            }

            finger.Depth = depth;
            if (target == null)
            {
                return;
            }

            finger.Press(Mathf.Clamp(depth, 0, MaxTravel));
            if (depth < ReleaseDepth)
            {
                if (finger.Down)
                {
                    finger.Down = false;
                    ExecuteEvents.Execute(target, finger.Data, ExecuteEvents.pointerUpHandler);
                }

                finger.Armed = true;
            }
            else if (finger.Armed && depth >= PressDepth)
            {
                finger.Armed = false;
                finger.Down = true;
                ExecuteEvents.Execute(target, finger.Data, ExecuteEvents.pointerDownHandler);
                Click(target, null, finger.Data);
            }
        }

        private void PokeObjects(Finger finger, Vector3 tip)
        {
            var count = Physics.OverlapSphereNonAlloc(tip, finger.ObjectArmed ? ObjectPokeRadius : ObjectReleaseRadius, _overlaps);
            PalaceObjectView touched = null;
            for (var i = 0; i < count && touched == null; i++)
            {
                touched = _overlaps[i].GetComponentInParent<PalaceObjectView>();
            }

            if (!finger.ObjectArmed)
            {
                finger.ObjectArmed = touched == null;
                return;
            }

            if (touched != null)
            {
                finger.ObjectArmed = false;
                Click(null, touched, finger.Data);
            }
        }

        // ---------- Dev markers ----------

        /// <summary>Dev mode: yellow dot where the gaze ray hits, cyan dots on tracked index fingertips (red while pressing).</summary>
        private void UpdateDevMarkers()
        {
            var show = DevMode.Enabled;
            if (!show && _gazeMarker == null)
            {
                return;
            }

            _gazeMarker ??= DevMaterials.Marker("Dev gaze", new Color(1f, 0.9f, 0.2f, 0.9f), 0.02f);
            _leftMarker ??= DevMaterials.Marker("Dev left tip", new Color(0.2f, 0.9f, 1f, 0.8f), 0.015f);
            _rightMarker ??= DevMaterials.Marker("Dev right tip", new Color(0.2f, 0.9f, 1f, 0.8f), 0.015f);
            Place(_gazeMarker, show && _gazeHitName != null, _gazePoint);
            Place(_leftMarker, show && _leftFinger.HasTip, _leftFinger.Tip);
            Place(_rightMarker, show && _rightFinger.HasTip, _rightFinger.Tip);
            _leftMarker.localScale = Vector3.one * (_leftFinger.Down ? 0.025f : 0.015f);
            _rightMarker.localScale = Vector3.one * (_rightFinger.Down ? 0.025f : 0.015f);
        }

        private static void Place(Transform marker, bool visible, Vector3 position)
        {
            marker.gameObject.SetActive(visible);
            if (visible)
            {
                marker.position = position;
            }
        }

        // ---------- Shared ----------

        private void Click(GameObject ui, PalaceObjectView view, PointerEventData data)
        {
            if (ui == null)
            {
                AnyPress?.Invoke();
            }

            if (ui != null)
            {
                ExecuteEvents.ExecuteHierarchy(ui, data, ExecuteEvents.pointerClickHandler);
            }
            else if (view != null)
            {
                ObjectSelected?.Invoke(view.Index);
            }
        }

        /// <summary>
        /// Button of <paramref name="canvas"/> under the fingertip, by plain rect containment. Not through the camera
        /// like <see cref="UiAt"/>: uGUI drops points outside the viewport, and the glasses' narrow field of view
        /// leaves the lower buttons of a tall panel off-screen while the finger is on them.
        /// </summary>
        private GameObject PokeTarget(Canvas canvas, Vector3 tip)
        {
            canvas.GetComponentsInChildren(false, _selectables);
            foreach (var selectable in _selectables)
            {
                var rect = (RectTransform)selectable.transform;
                if (selectable.IsInteractable() && rect.rect.Contains(rect.InverseTransformPoint(tip)))
                {
                    return ExecuteEvents.GetEventHandler<IPointerClickHandler>(selectable.gameObject);
                }
            }

            return null;
        }

        /// <summary>Clickable UI element of <paramref name="canvas"/> at a world point on it, or null.</summary>
        private GameObject UiAt(Canvas canvas, Vector3 worldPoint, PointerEventData data)
        {
            data.position = _camera.WorldToScreenPoint(worldPoint);
            _uiHits.Clear();
            EventSystem.current.RaycastAll(data, _uiHits);
            foreach (var hit in _uiHits)
            {
                if (hit.gameObject.GetComponentInParent<Canvas>()?.rootCanvas != canvas)
                {
                    continue; // another panel in front of this point from the camera's view
                }

                var target = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
                if (target != null)
                {
                    return target;
                }
            }

            return null;
        }

        private static void SetHover(ref GameObject current, GameObject next, PointerEventData data)
        {
            if (next == current)
            {
                return;
            }

            if (current != null)
            {
                ExecuteEvents.Execute(current, data, ExecuteEvents.pointerExitHandler);
            }

            current = next;
            if (current != null)
            {
                ExecuteEvents.Execute(current, data, ExecuteEvents.pointerEnterHandler);
            }
        }

        private XRHandSubsystem RunningHands()
        {
            _handSubsystems.Clear();
            SubsystemManager.GetSubsystems(_handSubsystems);
            foreach (var subsystem in _handSubsystems)
            {
                if (subsystem.running)
                {
                    return subsystem;
                }
            }

            return null;
        }

        /// <summary>Joint position in world space (hand data is relative to the XR Origin's camera offset).</summary>
        private bool TryJoint(XRHand hand, XRHandJointID id, out Vector3 position)
        {
            position = default;
            if (!hand.GetJoint(id).TryGetPose(out var pose))
            {
                return false;
            }

            var space = _camera.transform.parent;
            position = space == null ? pose.position : space.TransformPoint(pose.position);
            return true;
        }

        /// <summary>
        /// XR Hands' rigged hand model (from its HandVisualizer sample, in Resources/Hands), driven by the
        /// package's own skeleton driver and hidden automatically while the hand isn't tracked.
        /// </summary>
        private static void CreateHandMesh(string resource, Handedness handedness, Transform trackingSpace)
        {
            var prefab = Resources.Load<GameObject>(resource);
            if (prefab == null)
            {
                Debug.LogWarning("NeuroScholAR: hand model missing: " + resource);
                return;
            }

            var root = Instantiate(prefab, trackingSpace);
            root.SetActive(false); // finish wiring before the components' OnEnable runs
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;

            var events = root.AddComponent<XRHandTrackingEvents>();
            events.handedness = handedness;
            events.updateType = XRHandTrackingEvents.UpdateTypes.Dynamic;

            var renderer = root.GetComponentInChildren<SkinnedMeshRenderer>();
            var material = new Material(Shader.Find("NeuroScholAR/Simple")) { color = new Color(0.55f, 0.55f, 0.58f, 0.45f) };
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            renderer.sharedMaterial = material;
            var mesh = root.AddComponent<XRHandMeshController>();
            mesh.handMeshRenderer = renderer;
            mesh.handTrackingEvents = events;

            var driver = root.AddComponent<XRHandSkeletonDriver>();
            driver.jointTransformReferences = new List<JointToTransformReference>();
            foreach (Transform child in root.transform)
            {
                if (child.name.EndsWith(XRHandJointID.Wrist.ToString()))
                {
                    driver.rootTransform = child;
                }
            }

            XRHandSkeletonDriverUtility.FindJointsFromRoot(driver);
            driver.InitializeFromSerializedReferences();
            driver.handTrackingEvents = events;
            root.SetActive(true);
        }

        private static bool SelectPressed()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && (keyboard.spaceKey.wasPressedThisFrame || keyboard.enterKey.wasPressedThisFrame))
            {
                return true;
            }

            var gamepad = Gamepad.current;
            if (gamepad != null && (gamepad.buttonSouth.wasPressedThisFrame || gamepad.rightTrigger.wasPressedThisFrame))
            {
                return true;
            }

            // XREAL: the Beam Pro screen acts as a virtual controller exposed as an XR controller.
            foreach (var device in InputSystem.devices)
            {
                if (device is UnityEngine.InputSystem.XR.XRController controller &&
                    (Pressed(controller, "triggerPressed") || Pressed(controller, "primaryButton")))
                {
                    return true;
                }
            }

            // A touch or click directly on a UI element is handled by the regular EventSystem; don't double-fire.
            var pointer = UnityEngine.InputSystem.Pointer.current;
            if (pointer != null && pointer.press.wasPressedThisFrame)
            {
                return EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject();
            }

            return false;
        }

        private static bool Pressed(InputDevice device, string control) =>
            device.TryGetChildControl<ButtonControl>(control) is { } button && button.wasPressedThisFrame;

        /// <summary>One index fingertip's poke state.</summary>
        private sealed class Finger
        {
            private RectTransform _pressed;

            public PointerEventData Data;
            public GameObject Target;
            public bool Armed;
            public bool Down;
            public bool ObjectArmed = true;
            public bool HasTip;
            public Vector3 Tip;
            public float Depth;

            /// <summary>Moves the hovered button down by <paramref name="travel"/> metres, like a key under the finger.</summary>
            public void Press(float travel)
            {
                _pressed = Target.transform as RectTransform;
                if (_pressed != null)
                {
                    var p = _pressed.localPosition;
                    p.z = travel / _pressed.lossyScale.z; // local units of the button (canvas units)
                    _pressed.localPosition = p;
                }
            }

            public void Release()
            {
                if (_pressed != null)
                {
                    var p = _pressed.localPosition;
                    p.z = 0;
                    _pressed.localPosition = p;
                    _pressed = null;
                }

                if (Target != null && Data != null)
                {
                    if (Down)
                    {
                        ExecuteEvents.Execute(Target, Data, ExecuteEvents.pointerUpHandler);
                    }

                    ExecuteEvents.Execute(Target, Data, ExecuteEvents.pointerExitHandler);
                }

                Target = null;
                Down = false;
                Armed = false;
            }
        }
    }
}

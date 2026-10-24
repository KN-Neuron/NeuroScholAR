using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeuroScholAR.Core.Learning;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Num = System.Numerics;

namespace NeuroScholAR.App.Palace
{
    /// <summary>
    /// Preparation phase, in two steps. Scan: the user walks around the whole room (<see cref="ScanSeconds"/>, or
    /// until "Rozmieść teraz") while the device builds a room mesh and detects planes. Place: free spots become
    /// candidates and the palace objects drop onto them one by one, each as far from the others as possible
    /// (<see cref="SurfacePlanner"/>, engine-free and unit-tested).
    /// <para>
    /// Where spots come from: with a room mesh (XREAL Air 2 Ultra meshing), horizontal spots are found by raycasting
    /// down onto the mesh and keeping only flat, upward patches, so small surfaces count and an oversized plane
    /// outline never puts an object in the air; walls come from vertical planes, kept only where the mesh confirms
    /// them. Without a mesh, every detected plane is grid-sampled. Without either (Editor), a ring around the user.
    /// </para>
    /// Placed objects are held by AR anchors, so they stay on their surface when tracking is refined or re-localized.
    /// </summary>
    public sealed class SurfacePlacer : MonoBehaviour
    {
        private const float ScanSeconds = 25f;      // time to walk around the room before anything is placed
        private const float PlaceInterval = 0.5f;   // one object per interval, so they visibly "appear"
        private const float GridStep = 0.45f;
        private const float EdgeMargin = 0.15f;     // keep objects off table edges
        private const float FloorTolerance = 0.25f; // horizontal surfaces this close to the floor are floor
        private const float MinFurnitureSide = 0.2f;
        private const float MinWallSide = 0.5f;
        private const float WallMinHeight = 1.0f, WallMaxHeight = 1.9f; // above the floor: eye-level-ish
        private const float FloorObjectSize = 0.4f, FurnitureObjectSize = 0.2f, WallObjectSize = 0.3f;
        private const float NoSurfacesFallbackSeconds = 3f; // Editor / no plane or mesh subsystem: give up waiting
        private const float StandingEyeHeight = 1.5f;       // ring fallback floor guess when no floor was seen

        // Room mesh sampling. Rays every MeshGridStep within MeshRadius of the user; a spot needs MeshFlatRadius of
        // flat surface around it (object footprint) within MeshFlatTolerance, facing up (normal.y >= MinUpNormal).
        private const float MeshRadius = 4f;
        private const float MeshGridStep = 0.3f;
        private const float MeshFlatRadius = 0.1f;
        private const float MeshFlatTolerance = 0.04f;
        private const float MinUpNormal = 0.9f;
        private const float WallCheckDistance = 0.3f, WallCheckTolerance = 0.08f;

        // Built-in "Ignore Raycast" layer: default raycasts and overlaps (gaze, fingertips) skip the room mesh.
        private const int RoomMeshLayer = 2;
        private const int RoomMeshMask = 1 << RoomMeshLayer;

        // Must match between Physics.BakeMesh (background) and the MeshCollider, or assigning re-cooks on the main thread.
        private const MeshColliderCookingOptions Cooking =
            MeshColliderCookingOptions.CookForFasterSimulation | MeshColliderCookingOptions.EnableMeshCleaning |
            MeshColliderCookingOptions.WeldColocatedVertices | MeshColliderCookingOptions.UseFastMidphase;

        private readonly SurfacePlanner _planner = new SurfacePlanner();
        private readonly List<SurfaceSpot> _spots = new List<SurfaceSpot>();
        private readonly HashSet<string> _surfaceIds = new HashSet<string>();
        private readonly Dictionary<string, ARPlane> _planeById = new Dictionary<string, ARPlane>();
        private readonly List<Num.Vector2> _polygon = new List<Num.Vector2>();
        private readonly List<GameObject> _anchorObjects = new List<GameObject>();
        private readonly Dictionary<Color, Material> _devMaterials = new Dictionary<Color, Material>();
        private readonly SpotMarkers _markers = new SpotMarkers();
        private readonly List<MeshFilter> _dirtyChunks = new List<MeshFilter>();
        private readonly Dictionary<MeshFilter, Mesh> _colliderMeshes = new Dictionary<MeshFilter, Mesh>();
        private Task _bake;
        private MeshFilter _bakingChunk;
        private Mesh _bakingCopy;
        private float _bakeStartedAt;
        private int _meshEvents;
        private ARPlaneManager _planes;
        private ARMeshManager _meshing;
        private ARAnchorManager _anchors;
        private PalaceRoom _room;
        private Transform _head;
        private int _target;
        private float _nextStep;
        private float _startedAt;
        private bool _scanDone;
        private int _meshSpots;
        private int _wallsRejectedByMesh;

        /// <summary>All requested objects are placed.</summary>
        public event Action Completed;

        public bool IsPlacing { get; private set; }

        /// <summary>Still in the scan step (nothing placed yet).</summary>
        public bool IsScanning => IsPlacing && !_scanDone;

        public float ScanSecondsLeft => IsScanning ? Mathf.Max(0, ScanSeconds - (Time.time - _startedAt)) : 0;

        /// <summary>Usable surfaces found so far (shown while scanning).</summary>
        public int SurfaceCount => _surfaceIds.Count;
        public int PlacedCount => _room == null ? 0 : _room.Objects.Count;
        public int TargetCount => _target;

        /// <summary>Dev overlay lines: heights, planes per classification, mesh, spots, anchors.</summary>
        public string DevText { get; private set; } = string.Empty;

        /// <summary>False when the device offers neither plane detection nor meshing; the UI explains the ring fallback.</summary>
        public bool HasPlaneDetection => HasPlanes || MeshRunning;

        private bool HasPlanes => _planes != null && _planes.subsystem != null;
        private bool MeshRunning => _meshing != null && _meshing.subsystem != null && _meshing.subsystem.running;
        private bool HasMesh => MeshRunning && _meshing.meshes.Count > 0;
        private bool AnchorsRunning => _anchors != null && _anchors.subsystem != null && _anchors.subsystem.running;

        public void Init(PalaceRoom room, Transform head)
        {
            _room = room;
            _head = head;
            _planes = FindAnyObjectByType<ARPlaneManager>();
            if (_planes != null)
            {
                _planes.requestedDetectionMode = PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;
            }

            var origin = FindAnyObjectByType<XROrigin>();
            if (origin != null)
            {
                _meshing = CreateMeshing(origin);
                _anchors = origin.GetComponent<ARAnchorManager>() ?? origin.gameObject.AddComponent<ARAnchorManager>();
            }

            SetScanning(false);
        }

        /// <summary>Starts placing <paramref name="count"/> objects (indices 0..count-1) on surfaces.</summary>
        public void Begin(int count)
        {
            ReleaseAnchors();
            _room.Clear();
            _planner.Reset();
            _target = count;
            _startedAt = Time.time;
            _scanDone = false;
            _planeById.Clear();
            _surfaceIds.Clear();
            IsPlacing = true;
            SetScanning(true);
        }

        /// <summary>"Rozmieść teraz": ends the scan early and starts placing on what was found.</summary>
        public void PlaceNow() => _scanDone = true;

        /// <summary>
        /// "Rozmieść resztę tutaj": puts the objects not placed yet on any detected surface (relaxed spacing), and only
        /// what still doesn't fit on a ring around the user.
        /// </summary>
        public void PlaceRemainingAround()
        {
            if (!IsPlacing)
            {
                return;
            }

            CollectSpots();
            while (PlacedCount < _target && _planner.TryChoose(_spots, ToNum(_head.position), ToNum(_head.forward), out var spot, relaxed: true))
            {
                Spawn(spot);
            }

            // ponytail: no floor seen means we can only guess; 1.5 m under the eyes is a standing user.
            var floorY = FloorY(StandingEyeHeight);
            var floor = new Vector3(_head.position.x, floorY, _head.position.z);
            var first = PlacedCount;
            _room.SpawnAround(first, _target, floor, _head.eulerAngles.y);
            for (var i = first; i < PlacedCount; i++)
            {
                Anchor(_room.Objects[i]);
            }

            if (PlacedCount < _target)
            {
                // ponytail: ring holds 12 = PalaceSession.MaxObjects; widen the ring if MaxObjects grows
                Debug.LogWarning($"NeuroScholAR: fallback ring holds {PalaceRoom.FallbackCapacity} objects");
            }

            Finish();
        }

        /// <summary>Session over: tracking off, anchors (and the objects under them) gone.</summary>
        public void Stop()
        {
            IsPlacing = false;
            SetScanning(false);
            ReleaseAnchors();
            _markers.Hide();
        }

        private void Update()
        {
            UpdateColliders();
            if (Time.time < _nextStep)
            {
                return;
            }

            _nextStep = Time.time + PlaceInterval;
            if (IsPlacing || DevMode.Enabled)
            {
                var started = Time.realtimeSinceStartup;
                CollectSpots(); // while scanning: keeps SurfaceCount up to date; in dev mode: shows the spots
                var ms = (Time.realtimeSinceStartup - started) * 1000;
                if (ms > 30)
                {
                    Debug.LogWarning($"NeuroScholAR: slow spot search: {ms:F0} ms, {_spots.Count} spots");
                }
            }

            UpdateDevView();
            if (!IsPlacing)
            {
                return;
            }

            if (!HasPlaneDetection && Time.time - _startedAt > NoSurfacesFallbackSeconds)
            {
                PlaceRemainingAround();
                return;
            }

            _scanDone |= Time.time - _startedAt >= ScanSeconds;
            if (!_scanDone)
            {
                return;
            }

            // Strict rules first (spread out, max per surface); once they leave nothing, share surfaces rather than
            // waiting for a ring fallback in the air.
            var head = ToNum(_head.position);
            var forward = ToNum(_head.forward);
            if (_planner.TryChoose(_spots, head, forward, out var spot) ||
                _planner.TryChoose(_spots, head, forward, out spot, relaxed: true))
            {
                Spawn(spot);
            }

            if (PlacedCount >= _target)
            {
                Finish();
            }
        }

        /// <summary>Placing is done; tracking stays on until the session ends so anchors keep being refined.</summary>
        private void Finish()
        {
            IsPlacing = false;
            Completed?.Invoke();
        }

        private void Spawn(SurfaceSpot spot)
        {
            var position = ToUnity(spot.Position);
            _planeById.TryGetValue(spot.SurfaceId, out var plane); // null for spots found on the room mesh
            Vector3 facing;
            float size;
            switch (spot.Kind)
            {
                case SurfaceKind.Wall:
                    facing = plane != null ? plane.normal : _head.position - position;
                    size = WallObjectSize;
                    break;
                case SurfaceKind.Furniture:
                    facing = _head.position - position;
                    size = plane == null ? FurnitureObjectSize : Mathf.Min(FurnitureObjectSize, Mathf.Min(plane.size.x, plane.size.y) * 0.8f);
                    break;
                default:
                    facing = _head.position - position;
                    size = FloorObjectSize;
                    break;
            }

            Anchor(_room.Spawn(PlacedCount, position, facing, spot.Kind == SurfaceKind.Wall, size));
        }

        /// <summary>
        /// Puts the object under a new AR anchor at its pose. Without anchor support it stays fixed in the world.
        /// (Not under its plane: a plane's pose moves as the plane grows, which would slide the object.)
        /// </summary>
        private void Anchor(PalaceObjectView view)
        {
            if (!AnchorsRunning)
            {
                return;
            }

            var go = new GameObject($"Anchor {view.Index}");
            go.transform.SetPositionAndRotation(view.transform.position, view.transform.rotation);
            go.AddComponent<ARAnchor>(); // registers with the anchor manager; deactivates its GameObject on failure
            if (!go.activeSelf)
            {
                Destroy(go);
                Debug.LogWarning($"NeuroScholAR: could not anchor object {view.Index}; it stays fixed in the world");
                return;
            }

            view.transform.SetParent(go.transform, true);
            _anchorObjects.Add(go);
        }

        private void ReleaseAnchors()
        {
            foreach (var go in _anchorObjects)
            {
                if (go != null)
                {
                    Destroy(go);
                }
            }

            _anchorObjects.Clear();
        }

        // ---------- Candidate spots ----------

        private void CollectSpots()
        {
            _spots.Clear();
            _planeById.Clear();
            _surfaceIds.Clear();
            _meshSpots = 0;
            _wallsRejectedByMesh = 0;
            var meshed = HasMesh;
            var floorY = FloorY();
            if (meshed)
            {
                CollectMeshSpots(ref floorY);
            }

            if (HasPlanes)
            {
                foreach (var plane in _planes.trackables)
                {
                    // With a mesh, horizontal surfaces come from it: plane outlines overshoot (a bed's plane covers
                    // the floor around it too).
                    if (!TryClassify(plane, floorY, out var kind) || (meshed && kind != SurfaceKind.Wall))
                    {
                        continue;
                    }

                    var id = plane.trackableId.ToString();
                    _planeById[id] = plane;
                    SampleGrid(plane, kind, id, floorY, meshed);
                }
            }

            foreach (var spot in _spots)
            {
                _surfaceIds.Add(spot.SurfaceId);
            }
        }

        /// <summary>
        /// Downward rays on a grid around the user onto the room mesh; keeps flat, upward-facing patches with room for
        /// an object. Lowers <paramref name="floorY"/> to the lowest such patch (the real floor, once it was scanned).
        /// </summary>
        private void CollectMeshSpots(ref float floorY)
        {
            var centre = _head.position;
            var top = centre.y + 0.4f;
            var length = top - (floorY - 1f);
            var start = _spots.Count;
            for (var x = -MeshRadius; x <= MeshRadius; x += MeshGridStep)
            {
                for (var z = -MeshRadius; z <= MeshRadius; z += MeshGridStep)
                {
                    if (x * x + z * z <= MeshRadius * MeshRadius &&
                        TryFlatTop(new Vector3(centre.x + x, top, centre.z + z), length, out var point))
                    {
                        floorY = Mathf.Min(floorY, point.y);
                        _spots.Add(new SurfaceSpot(ToNum(point), SurfaceKind.Furniture, string.Empty));
                    }
                }
            }

            // Kinds and surface ids once the floor height is known.
            // ponytail: "same surface" = same 10 cm height band within a 1.5 m cell; real segmentation if caps misfire.
            for (var i = start; i < _spots.Count; i++)
            {
                var p = _spots[i].Position;
                var kind = p.Y - floorY < FloorTolerance ? SurfaceKind.Floor : SurfaceKind.Furniture;
                var id = $"mesh {Mathf.RoundToInt(p.Y / 0.1f)} {Mathf.FloorToInt(p.X / 1.5f)} {Mathf.FloorToInt(p.Z / 1.5f)}";
                _spots[i] = new SurfaceSpot(p, kind, id);
            }

            _meshSpots = _spots.Count - start;
        }

        /// <summary>Topmost mesh surface under <paramref name="origin"/>, if it is flat and level around the hit.</summary>
        private static bool TryFlatTop(Vector3 origin, float length, out Vector3 point)
        {
            point = default;
            if (!Physics.Raycast(origin, Vector3.down, out var hit, length, RoomMeshMask) || hit.normal.y < MinUpNormal)
            {
                return false;
            }

            for (var i = 0; i < 4; i++)
            {
                var offset = i == 0 ? Vector3.right : i == 1 ? Vector3.left : i == 2 ? Vector3.forward : Vector3.back;
                var from = hit.point + offset * MeshFlatRadius + Vector3.up * 0.15f; // under any shelf above it
                if (!Physics.Raycast(from, Vector3.down, out var side, 0.3f, RoomMeshMask) ||
                    side.normal.y < MinUpNormal || Mathf.Abs(side.point.y - hit.point.y) > MeshFlatTolerance)
                {
                    return false; // an edge, a hole or a bump: no room for the object here
                }
            }

            point = hit.point;
            return true;
        }

        /// <summary>A wall spot is real if the mesh is there: a ray back towards the plane meets it where expected.</summary>
        private static bool WallOnMesh(Vector3 spot, Vector3 normal) =>
            Physics.Raycast(spot + normal * WallCheckDistance, -normal, out var hit, WallCheckDistance * 2, RoomMeshMask) &&
            Mathf.Abs(hit.distance - WallCheckDistance) < WallCheckTolerance;

        /// <summary>Usable surface and its kind, or false (not tracked, merged, too small, ceiling or slope).</summary>
        private static bool TryClassify(ARPlane plane, float floorY, out SurfaceKind kind)
        {
            kind = SurfaceKind.Floor;
            if (plane.trackingState != TrackingState.Tracking || plane.subsumedBy != null)
            {
                return false;
            }

            var minSide = Mathf.Min(plane.size.x, plane.size.y);
            if (plane.alignment == PlaneAlignment.HorizontalUp)
            {
                kind = plane.center.y - floorY < FloorTolerance ? SurfaceKind.Floor : SurfaceKind.Furniture;
                return minSide >= MinFurnitureSide;
            }

            if (plane.alignment == PlaneAlignment.Vertical)
            {
                kind = SurfaceKind.Wall;
                return minSide >= MinWallSide;
            }

            return false;
        }

        private void SampleGrid(ARPlane plane, SurfaceKind kind, string id, float floorY, bool meshed)
        {
            _polygon.Clear();
            foreach (var p in plane.boundary)
            {
                _polygon.Add(new Num.Vector2(p.x, p.y));
            }

            if (_polygon.Count < 3)
            {
                return;
            }

            // Plane space: boundary is in the plane's local XZ, centred on plane.centerInPlaneSpace.
            var c = plane.centerInPlaneSpace;
            var half = plane.extents;
            for (var x = -half.x; x <= half.x; x += GridStep)
            {
                for (var z = -half.y; z <= half.y; z += GridStep)
                {
                    var local = new Num.Vector2(c.x + x, c.y + z);
                    if (!InsideWithMargin(local))
                    {
                        continue;
                    }

                    var world = plane.transform.TransformPoint(new Vector3(local.X, 0, local.Y));
                    if (kind == SurfaceKind.Wall && (world.y < floorY + WallMinHeight || world.y > floorY + WallMaxHeight))
                    {
                        continue;
                    }

                    if (meshed && kind == SurfaceKind.Wall && !WallOnMesh(world, plane.normal))
                    {
                        _wallsRejectedByMesh++;
                        continue;
                    }

                    _spots.Add(new SurfaceSpot(ToNum(world), kind, id));
                }
            }
        }

        private bool InsideWithMargin(Num.Vector2 p) =>
            SurfacePlanner.Contains(_polygon, p) &&
            SurfacePlanner.Contains(_polygon, p + new Num.Vector2(EdgeMargin, 0)) &&
            SurfacePlanner.Contains(_polygon, p - new Num.Vector2(EdgeMargin, 0)) &&
            SurfacePlanner.Contains(_polygon, p + new Num.Vector2(0, EdgeMargin)) &&
            SurfacePlanner.Contains(_polygon, p - new Num.Vector2(0, EdgeMargin));

        /// <summary>
        /// Lowest upward plane, but never above <paramref name="maxBelowEyes"/> under the eyes, so a bed or desk seen
        /// before the floor isn't taken for it. Guessing too low is harmless: the mesh or a floor plane corrects it.
        /// </summary>
        private float FloorY(float maxBelowEyes = 1.4f)
        {
            var floor = _head.position.y - maxBelowEyes;
            if (HasPlanes)
            {
                foreach (var plane in _planes.trackables)
                {
                    if (plane.alignment == PlaneAlignment.HorizontalUp && plane.trackingState == TrackingState.Tracking)
                    {
                        floor = Mathf.Min(floor, plane.center.y);
                    }
                }
            }

            return floor;
        }

        /// <summary>Planes and meshing run during the whole session (anchors and dev view need them), off otherwise.</summary>
        private void SetScanning(bool scanning)
        {
            if (_planes != null)
            {
                _planes.enabled = scanning;
            }

            // ponytail: meshing is started once and never stopped. XREAL crashed (SIGSEGV) right after a
            // stop/release/start cycle; costs some CPU outside sessions, revisit with a newer XREAL SDK.
            if (_meshing != null && scanning)
            {
                _meshing.enabled = true;
            }
        }

        /// <summary>
        /// Room meshing (only where the XR provider offers it). The chunk template has a collider for the spot
        /// raycasts, on the Ignore Raycast layer, and a renderer that only dev mode turns on.
        /// </summary>
        private ARMeshManager CreateMeshing(XROrigin origin)
        {
            // No MeshCollider on the template: ARMeshManager would then re-cook it on the main thread on every mesh
            // update, which froze the app. Colliders are baked in the background instead (UpdateColliders).
            var template = new GameObject("Room mesh chunk", typeof(MeshFilter), typeof(MeshRenderer));
            template.SetActive(false);
            template.layer = RoomMeshLayer;
            template.transform.SetParent(transform, false);
            template.GetComponent<MeshRenderer>().enabled = false;

            var go = new GameObject("Room meshing");
            go.SetActive(false); // configure before OnEnable starts the subsystem
            go.transform.SetParent(origin.transform, false); // ARMeshManager must live under the XR Origin
            var meshing = go.AddComponent<ARMeshManager>();
            meshing.meshPrefab = template.GetComponent<MeshFilter>();
            // Keep XREAL's known-good settings: with normals off (or density 0.3) its native meshing crashed (SIGSEGV)
            // within a second of starting. Normals cost nothing extra when the provider supplies them.
            meshing.normals = true;
            meshing.density = 0.5f;
            meshing.meshesChanged += OnMeshesChanged;
            meshing.enabled = false; // starts with the session (SetScanning), not on the setup screen
            go.SetActive(true);
            return meshing;
        }

        private void OnMeshesChanged(ARMeshesChangedEventArgs changes)
        {
            // ponytail: diagnostics for the meshing freeze (adb logcat -s Unity); drop once meshing is proven stable
            if (_meshEvents++ % 20 == 0)
            {
                var largest = 0;
                foreach (var chunk in _meshing.meshes)
                {
                    largest = Mathf.Max(largest, chunk.sharedMesh == null ? 0 : chunk.sharedMesh.vertexCount);
                }

                Debug.Log($"NeuroScholAR: mesh event #{_meshEvents}: +{changes.added.Count} ~{changes.updated.Count} " +
                          $"-{changes.removed.Count}, {_meshing.meshes.Count} chunks, largest {largest} vertices");
            }

            foreach (var chunk in changes.added)
            {
                MarkDirty(chunk);
            }

            foreach (var chunk in changes.updated)
            {
                MarkDirty(chunk);
            }

            foreach (var chunk in changes.removed)
            {
                _dirtyChunks.Remove(chunk);
                if (_colliderMeshes.TryGetValue(chunk, out var copy))
                {
                    Destroy(copy);
                    _colliderMeshes.Remove(chunk);
                }
            }
        }

        private void MarkDirty(MeshFilter chunk)
        {
            if (!_dirtyChunks.Contains(chunk))
            {
                _dirtyChunks.Add(chunk);
            }
        }

        /// <summary>
        /// One chunk at a time: copy its mesh (the provider keeps rewriting the original), cook the copy on a worker
        /// thread with Physics.BakeMesh, then hand it to the chunk's MeshCollider, which reuses the cooked data.
        /// ponytail: one bake in flight; colliders lag the mesh by a second or two, fine for picking spots.
        /// </summary>
        private void UpdateColliders()
        {
            if (_bake != null)
            {
                if (!_bake.IsCompleted)
                {
                    return;
                }

                var seconds = Time.realtimeSinceStartup - _bakeStartedAt;
                if (seconds > 1f)
                {
                    Debug.LogWarning($"NeuroScholAR: slow room mesh bake: {seconds:F1} s for {_bakingCopy.vertexCount} vertices");
                }

                if (_bake.IsFaulted)
                {
                    Debug.LogWarning("NeuroScholAR: room mesh bake failed: " + _bake.Exception?.GetBaseException().Message);
                    Destroy(_bakingCopy);
                }
                else if (_bakingChunk != null)
                {
                    var collider = _bakingChunk.GetComponent<MeshCollider>() ?? _bakingChunk.gameObject.AddComponent<MeshCollider>();
                    collider.cookingOptions = Cooking;
                    collider.sharedMesh = _bakingCopy;
                    if (_colliderMeshes.TryGetValue(_bakingChunk, out var old))
                    {
                        Destroy(old);
                    }

                    _colliderMeshes[_bakingChunk] = _bakingCopy;
                }
                else
                {
                    Destroy(_bakingCopy);
                }

                _bake = null;
                _bakingChunk = null;
                _bakingCopy = null;
            }

            while (_dirtyChunks.Count > 0)
            {
                var chunk = _dirtyChunks[0];
                _dirtyChunks.RemoveAt(0);
                if (chunk == null || chunk.sharedMesh == null || chunk.sharedMesh.vertexCount < 3)
                {
                    continue;
                }

                _bakingChunk = chunk;
                _bakingCopy = Instantiate(chunk.sharedMesh);
                var id = _bakingCopy.GetInstanceID();
                _bakeStartedAt = Time.realtimeSinceStartup;
                _bake = Task.Run(() => Physics.BakeMesh(id, false, Cooking));
                return;
            }
        }

        // ---------- Dev view ----------

        /// <summary>
        /// Every 0.5 s while dev mode is on: planes drawn by classification (green furniture, blue wall, orange floor,
        /// grey rejected), the room mesh in violet, and a dot on every candidate spot. Nothing is added while off.
        /// </summary>
        private void UpdateDevView()
        {
            var show = DevMode.Enabled;
            var floorY = FloorY();
            int furniture = 0, walls = 0, floors = 0, rejected = 0;
            if (HasPlanes)
            {
                foreach (var plane in _planes.trackables)
                {
                    var visualizer = plane.GetComponent<ARPlaneMeshVisualizer>();
                    if (visualizer == null)
                    {
                        if (!show)
                        {
                            continue;
                        }

                        visualizer = AddVisualizer(plane);
                    }

                    visualizer.enabled = show;
                    if (!show)
                    {
                        continue;
                    }

                    var usable = TryClassify(plane, floorY, out var kind);
                    var color = KindColor(usable ? kind : (SurfaceKind?)null);
                    if (!usable)
                    {
                        rejected++;
                    }
                    else if (kind == SurfaceKind.Furniture)
                    {
                        furniture++;
                    }
                    else if (kind == SurfaceKind.Wall)
                    {
                        walls++;
                    }
                    else
                    {
                        floors++;
                    }

                    plane.GetComponent<MeshRenderer>().sharedMaterial = DevMaterial(color, 0.2f);
                    plane.GetComponent<LineRenderer>().sharedMaterial = DevMaterial(color, 0.9f);
                }
            }

            if (_meshing != null)
            {
                foreach (var chunk in _meshing.meshes)
                {
                    var renderer = chunk.GetComponent<MeshRenderer>();
                    renderer.enabled = show;
                    if (show)
                    {
                        renderer.sharedMaterial = DevMaterial(new Color(0.7f, 0.4f, 1f), 0.25f);
                    }
                }
            }

            if (!show)
            {
                _markers.Hide();
                DevText = string.Empty;
                return;
            }

            _markers.Show(_spots, kind => DevMaterial(KindColor(kind), 0.9f));
            DevText =
                $"Oczy y={_head.position.y:F2}  podłoga y={floorY:F2} ({_head.position.y - floorY:F2} m pod oczami)  " +
                $"śledzenie {(_planes != null && _planes.enabled ? "wł." : "wył.")}\n" +
                $"Płaszczyzny: meble {furniture}, ściany {walls}, podłoga {floors}, odrzucone {rejected}\n" +
                $"Siatka: {(MeshRunning ? $"{_meshing.meshes.Count} fragm., {_meshSpots} miejsc" : "niedostępna")}; " +
                $"ściany odrzucone przez siatkę {_wallsRejectedByMesh}\n" +
                $"Miejsca {_spots.Count} na {SurfaceCount} powierzchniach; rozmieszczone {PlacedCount}/{_target}; " +
                $"kotwice {(AnchorsRunning ? _anchorObjects.Count.ToString() : "niedostępne")}";
        }

        private static Color KindColor(SurfaceKind? kind) =>
            kind == SurfaceKind.Furniture ? new Color(0.2f, 0.85f, 0.3f)
            : kind == SurfaceKind.Wall ? new Color(0.25f, 0.55f, 1f)
            : kind == SurfaceKind.Floor ? new Color(1f, 0.6f, 0.15f)
            : new Color(0.6f, 0.6f, 0.6f);

        private static ARPlaneMeshVisualizer AddVisualizer(ARPlane plane)
        {
            var go = plane.gameObject;
            if (!go.TryGetComponent<MeshFilter>(out _))
            {
                go.AddComponent<MeshFilter>();
            }

            go.AddComponent<MeshRenderer>();
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false; // the visualizer writes boundary points in plane space
            line.loop = true;
            line.widthMultiplier = 0.01f;
            return go.AddComponent<ARPlaneMeshVisualizer>(); // builds the mesh in OnEnable
        }

        private Material DevMaterial(Color color, float alpha)
        {
            color.a = alpha;
            if (!_devMaterials.TryGetValue(color, out var material))
            {
                material = DevMaterials.Transparent(color);
                _devMaterials[color] = material;
            }

            return material;
        }

        private static Num.Vector3 ToNum(Vector3 v) => new Num.Vector3(v.x, v.y, v.z);
        private static Vector3 ToUnity(Num.Vector3 v) => new Vector3(v.X, v.Y, v.Z);

        /// <summary>
        /// Dev dots on candidate spots: one mesh of small cubes per surface kind, rebuilt every 0.5 s (a few hundred
        /// spots at most), so no GameObject per spot.
        /// </summary>
        private sealed class SpotMarkers
        {
            private const float Half = 0.015f;
            private static readonly SurfaceKind[] Kinds = { SurfaceKind.Furniture, SurfaceKind.Wall, SurfaceKind.Floor };
            private readonly Dictionary<SurfaceKind, (Mesh mesh, MeshRenderer renderer)> _byKind =
                new Dictionary<SurfaceKind, (Mesh, MeshRenderer)>();
            private readonly List<Vector3> _vertices = new List<Vector3>();
            private readonly List<int> _triangles = new List<int>();

            public void Show(List<SurfaceSpot> spots, Func<SurfaceKind, Material> material)
            {
                foreach (var kind in Kinds)
                {
                    var (mesh, renderer) = Get(kind);
                    _vertices.Clear();
                    _triangles.Clear();
                    foreach (var spot in spots)
                    {
                        if (spot.Kind == kind)
                        {
                            AddCube(new Vector3(spot.Position.X, spot.Position.Y + Half, spot.Position.Z));
                        }
                    }

                    mesh.Clear();
                    mesh.SetVertices(_vertices);
                    mesh.SetTriangles(_triangles, 0);
                    mesh.RecalculateNormals();
                    mesh.RecalculateBounds();
                    renderer.sharedMaterial = material(kind);
                    renderer.enabled = _vertices.Count > 0;
                }
            }

            public void Hide()
            {
                foreach (var (_, renderer) in _byKind.Values)
                {
                    renderer.enabled = false;
                }
            }

            private (Mesh, MeshRenderer) Get(SurfaceKind kind)
            {
                if (!_byKind.TryGetValue(kind, out var entry))
                {
                    var go = new GameObject($"Dev spots ({kind})", typeof(MeshFilter), typeof(MeshRenderer));
                    var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                    mesh.MarkDynamic();
                    go.GetComponent<MeshFilter>().sharedMesh = mesh;
                    entry = (mesh, go.GetComponent<MeshRenderer>());
                    _byKind[kind] = entry;
                }

                return entry;
            }

            private void AddCube(Vector3 c)
            {
                var b = _vertices.Count;
                for (var i = 0; i < 8; i++)
                {
                    _vertices.Add(c + new Vector3((i & 1) == 0 ? -Half : Half, (i & 2) == 0 ? -Half : Half, (i & 4) == 0 ? -Half : Half));
                }

                // Six faces, outward winding (Unity: clockwise seen from outside).
                int[] faces = { 0, 2, 3, 1, 4, 5, 7, 6, 0, 1, 5, 4, 2, 6, 7, 3, 0, 4, 6, 2, 1, 3, 7, 5 };
                for (var f = 0; f < faces.Length; f += 4)
                {
                    _triangles.Add(b + faces[f]);
                    _triangles.Add(b + faces[f + 1]);
                    _triangles.Add(b + faces[f + 2]);
                    _triangles.Add(b + faces[f]);
                    _triangles.Add(b + faces[f + 2]);
                    _triangles.Add(b + faces[f + 3]);
                }
            }
        }
    }
}

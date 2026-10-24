using System.Collections.Generic;
using UnityEngine;

namespace NeuroScholAR.App.Palace
{
    /// <summary>
    /// Creates the palace objects from primitives at runtime (no art assets to import). Each object is a
    /// distinct kind and colour so it is easy to tell apart (the point of a memory palace). Objects are
    /// spawned one by one where <see cref="SurfacePlacer"/> finds room on real surfaces, scaled to fit:
    /// small on furniture, bigger on the floor, flat ones on walls. <see cref="Slots"/> is the fallback
    /// layout around the user when no surfaces are detected (Editor, phone without plane support).
    /// </summary>
    public sealed class PalaceRoom : MonoBehaviour
    {
        private enum Kind { Bookshelf, Painting, Table, Lamp, Armchair, Globe, Clock, Chest, Plant, Vase, Mirror, Piano }

        private static readonly Kind[] WallKinds = { Kind.Painting, Kind.Clock, Kind.Mirror };
        private static readonly Kind[] StandingKinds =
            { Kind.Globe, Kind.Lamp, Kind.Plant, Kind.Vase, Kind.Chest, Kind.Bookshelf, Kind.Armchair, Kind.Table, Kind.Piano };

        // Fallback ring: angle in degrees around the user (0 = straight ahead) and distance. Everything stands on the
        // floor: with no detected wall behind it, a "hanging" object would just float.
        private static readonly (float angle, float distance)[] Slots =
        {
            (0, 2.2f), (30, 2.0f), (60, 1.8f), (90, 2.0f), (120, 2.2f), (150, 1.9f),
            (180, 2.0f), (210, 1.9f), (240, 2.2f), (270, 1.8f), (300, 2.0f), (330, 1.9f),
        };

        private readonly List<PalaceObjectView> _objects = new List<PalaceObjectView>();
        private readonly HashSet<Kind> _usedKinds = new HashSet<Kind>();
        private Material _baseMaterial;

        public IReadOnlyList<PalaceObjectView> Objects => _objects;

        /// <summary>
        /// Creates object <paramref name="index"/> standing at <paramref name="position"/> (or hanging, for a wall),
        /// with its front towards <paramref name="facing"/> (wall normal, or towards the user), scaled so its largest side is <paramref name="maxSize"/> metres.
        /// </summary>
        public PalaceObjectView Spawn(int index, Vector3 position, Vector3 facing, bool wall, float maxSize)
        {
            _baseMaterial ??= new Material(Shader.Find("NeuroScholAR/Simple"));
            var kind = PickKind(wall);
            var color = Color.HSVToRGB(index * 0.618034f % 1f, 0.65f, 0.95f); // golden-ratio hues stay distinct

            var root = new GameObject($"Object {index} ({kind})");
            root.transform.SetParent(transform, false);
            facing.y = 0;
            // Models are built with their front towards -Z (chest lock, piano keys, picture canvas).
            root.transform.SetPositionAndRotation(position, Quaternion.LookRotation(facing.sqrMagnitude > 1e-4f ? -facing : Vector3.back));

            var model = new GameObject("Model").transform;
            model.SetParent(root.transform, false);
            var renderers = BuildShape(kind, model, color);
            var bounds = Encapsulate(model, renderers);
            var scale = maxSize / Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            model.localScale = Vector3.one * scale;
            // Standing objects rest on the surface; wall objects are centred on the spot and sit just off the wall.
            model.localPosition = wall
                ? -bounds.center * scale + Vector3.back * (bounds.extents.z * scale + 0.01f)
                : new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z) * scale;

            var box = root.AddComponent<BoxCollider>();
            box.center = model.localPosition + bounds.center * scale;
            box.size = bounds.size * scale + Vector3.one * 0.06f; // a little forgiving for rays and fingertips

            var view = root.AddComponent<PalaceObjectView>();
            view.Init(index, renderers, box.center.y + bounds.extents.y * scale + 0.08f);
            _objects.Add(view);
            return view;
        }

        /// <summary>Fallback: places objects <paramref name="from"/>.. on the ring around a floor point below the user.</summary>
        public void SpawnAround(int from, int count, Vector3 floorCenter, float yawDegrees)
        {
            var rotation = Quaternion.Euler(0, yawDegrees, 0);
            for (var i = from; i < count && i < Slots.Length; i++)
            {
                var slot = Slots[i];
                var direction = rotation * Quaternion.Euler(0, slot.angle, 0) * Vector3.forward;
                Spawn(i, floorCenter + direction * slot.distance, -direction, false, 0.45f);
            }
        }

        public static int FallbackCapacity => Slots.Length;

        public void Clear()
        {
            // Objects live under their AR anchor (SurfacePlacer), not under this room: destroy them by reference.
            foreach (var view in _objects)
            {
                if (view != null)
                {
                    Destroy(view.gameObject);
                }
            }

            _objects.Clear();
            _usedKinds.Clear();
        }

        /// <summary>A kind not used yet if possible, so every place looks different; wall spots need flat kinds.</summary>
        private Kind PickKind(bool wall)
        {
            var kinds = wall ? WallKinds : StandingKinds;
            foreach (var k in kinds)
            {
                if (_usedKinds.Add(k))
                {
                    return k;
                }
            }

            return kinds[_objects.Count % kinds.Length]; // repeats get a different colour anyway
        }

        private Renderer[] BuildShape(Kind kind, Transform parent, Color color)
        {
            var parts = new List<Renderer>();

            void Part(PrimitiveType type, Vector3 position, Vector3 scale, Color tint)
            {
                var go = GameObject.CreatePrimitive(type);
                Destroy(go.GetComponent<Collider>()); // one box collider on the root is enough
                go.transform.SetParent(parent, false);
                go.transform.localPosition = position;
                go.transform.localScale = scale;
                var r = go.GetComponent<Renderer>();
                r.sharedMaterial = Tinted(tint);
                parts.Add(r);
            }

            var dark = color * 0.6f;
            switch (kind)
            {
                case Kind.Painting:
                    Part(PrimitiveType.Cube, Vector3.zero, new Vector3(0.9f, 0.65f, 0.04f), new Color(0.5f, 0.38f, 0.1f));
                    Part(PrimitiveType.Cube, new Vector3(0, 0, -0.025f), new Vector3(0.78f, 0.53f, 0.02f), color);
                    break;
                case Kind.Mirror:
                    Part(PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.7f, 0.02f, 0.7f), color);
                    parts[0].transform.localRotation = Quaternion.Euler(90, 0, 0);
                    break;
                case Kind.Clock:
                    Part(PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.5f, 0.04f, 0.5f), color);
                    Part(PrimitiveType.Cube, new Vector3(0, 0.08f, -0.05f), new Vector3(0.03f, 0.18f, 0.02f), Color.black);
                    Part(PrimitiveType.Cube, new Vector3(0.06f, 0, -0.05f), new Vector3(0.14f, 0.03f, 0.02f), Color.black);
                    parts[0].transform.localRotation = Quaternion.Euler(90, 0, 0);
                    break;
                case Kind.Bookshelf:
                    Part(PrimitiveType.Cube, new Vector3(0, 0.9f, 0), new Vector3(0.9f, 1.8f, 0.35f), color);
                    for (var shelf = 0; shelf < 4; shelf++)
                    {
                        Part(PrimitiveType.Cube, new Vector3(0, 0.3f + shelf * 0.42f, -0.12f), new Vector3(0.75f, 0.25f, 0.15f),
                            Color.HSVToRGB(shelf * 0.23f % 1, 0.6f, 0.8f));
                    }

                    break;
                case Kind.Globe:
                    Part(PrimitiveType.Cylinder, new Vector3(0, 0.4f, 0), new Vector3(0.06f, 0.4f, 0.06f), dark);
                    Part(PrimitiveType.Sphere, new Vector3(0, 1.0f, 0), Vector3.one * 0.45f, color);
                    break;
                case Kind.Armchair:
                    Part(PrimitiveType.Cube, new Vector3(0, 0.25f, 0), new Vector3(0.8f, 0.5f, 0.7f), color);
                    Part(PrimitiveType.Cube, new Vector3(0, 0.75f, 0.3f), new Vector3(0.8f, 0.6f, 0.15f), color);
                    Part(PrimitiveType.Cube, new Vector3(-0.38f, 0.6f, 0), new Vector3(0.12f, 0.25f, 0.7f), dark);
                    Part(PrimitiveType.Cube, new Vector3(0.38f, 0.6f, 0), new Vector3(0.12f, 0.25f, 0.7f), dark);
                    break;
                case Kind.Lamp:
                    Part(PrimitiveType.Cylinder, new Vector3(0, 0.02f, 0), new Vector3(0.35f, 0.02f, 0.35f), Color.gray);
                    Part(PrimitiveType.Cylinder, new Vector3(0, 0.75f, 0), new Vector3(0.04f, 0.75f, 0.04f), Color.gray);
                    Part(PrimitiveType.Cylinder, new Vector3(0, 1.55f, 0), new Vector3(0.45f, 0.18f, 0.45f), color);
                    break;
                case Kind.Piano:
                    Part(PrimitiveType.Cube, new Vector3(0, 0.6f, 0), new Vector3(1.4f, 1.2f, 0.55f), color);
                    Part(PrimitiveType.Cube, new Vector3(0, 0.75f, -0.35f), new Vector3(1.3f, 0.05f, 0.2f), Color.white);
                    break;
                case Kind.Plant:
                    Part(PrimitiveType.Cylinder, new Vector3(0, 0.2f, 0), new Vector3(0.4f, 0.2f, 0.4f), new Color(0.6f, 0.3f, 0.2f));
                    Part(PrimitiveType.Sphere, new Vector3(0, 0.8f, 0), new Vector3(0.7f, 0.9f, 0.7f), color);
                    break;
                case Kind.Table:
                    Part(PrimitiveType.Cube, new Vector3(0, 0.72f, 0), new Vector3(1.1f, 0.06f, 0.7f), color);
                    foreach (var x in new[] { -0.48f, 0.48f })
                    {
                        foreach (var z in new[] { -0.28f, 0.28f })
                        {
                            Part(PrimitiveType.Cube, new Vector3(x, 0.36f, z), new Vector3(0.06f, 0.72f, 0.06f), dark);
                        }
                    }

                    break;
                case Kind.Chest:
                    Part(PrimitiveType.Cube, new Vector3(0, 0.25f, 0), new Vector3(0.9f, 0.5f, 0.5f), color);
                    Part(PrimitiveType.Cube, new Vector3(0, 0.3f, -0.26f), new Vector3(0.12f, 0.12f, 0.03f), new Color(0.9f, 0.75f, 0.2f));
                    break;
                case Kind.Vase:
                    Part(PrimitiveType.Cylinder, new Vector3(0, 0.3f, 0), new Vector3(0.6f, 0.3f, 0.6f), dark);
                    Part(PrimitiveType.Sphere, new Vector3(0, 0.8f, 0), new Vector3(0.35f, 0.5f, 0.35f), color);
                    break;
            }

            return parts.ToArray();
        }

        private Material Tinted(Color color)
        {
            return new Material(_baseMaterial) { color = color };
        }

        /// <summary>Union of the parts' world bounds, expressed in the root's local space (slightly loose, which suits a reticle).</summary>
        private static Bounds Encapsulate(Transform root, Renderer[] renderers)
        {
            var world = renderers[0].bounds;
            foreach (var r in renderers)
            {
                world.Encapsulate(r.bounds);
            }

            var size = root.InverseTransformVector(world.size);
            return new Bounds(root.InverseTransformPoint(world.center), new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)));
        }
    }
}

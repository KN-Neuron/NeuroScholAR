using System;
using System.Collections.Generic;
using System.Numerics;

namespace NeuroScholAR.Core.Learning
{
    /// <summary>What a detected surface is, judged from its orientation and height above the floor.</summary>
    public enum SurfaceKind
    {
        Floor,

        /// <summary>Horizontal and raised: desk, bed, shelf, sofa.</summary>
        Furniture,

        Wall,
    }

    /// <summary>A free spot on a detected surface (world metres, Y up).</summary>
    public readonly struct SurfaceSpot
    {
        public SurfaceSpot(Vector3 position, SurfaceKind kind, string surfaceId)
        {
            Position = position;
            Kind = kind;
            SurfaceId = surfaceId;
        }

        public Vector3 Position { get; }
        public SurfaceKind Kind { get; }
        public string SurfaceId { get; }
    }

    public sealed class PlacementConfig
    {
        /// <summary>Minimum distance between two placed objects, so each one is its own memorable place.</summary>
        public float MinSpacing = 1.0f;

        /// <summary>Ignore spots further than this from the user (planes from another room seen through a door).</summary>
        public float MaxDistance = 8f;

        /// <summary>
        /// Weight of "far from every object placed so far" (metres, capped at <see cref="SpreadCap"/>). Dominates the
        /// score, so objects fill the whole scanned room instead of clustering where the scan started.
        /// </summary>
        public float SpreadWeight = 1.5f;
        public float SpreadCap = 3f;

        /// <summary>At most this many objects per detected surface (floor excepted), so a big desk doesn't take them all.</summary>
        public int MaxPerSurface = 2;

        /// <summary>
        /// Spacing used by the relaxed pass (see <see cref="SurfacePlanner.TryChoose"/>): once the strict rules leave
        /// no spot, objects may share a surface and sit closer, which still beats hanging them in the air.
        /// </summary>
        public float RelaxedSpacing = 0.45f;

        /// <summary>Preference per kind: furniture first, then walls. Floor is only a last resort (see TryChoose).</summary>
        public float FurnitureScore = 2f;
        public float WallScore = 1.5f;
        public float FloorScore = 1f;
    }

    /// <summary>
    /// Picks where the next palace object goes among candidate spots on detected surfaces. Engine-free so the
    /// rules are unit-tested; the Unity side only turns AR planes into <see cref="SurfaceSpot"/>s.
    /// Greedy farthest-point choice: each next object goes where it is furthest from the others (plus a bonus
    /// for furniture and walls), so after a full-room scan they end up spread over the whole room.
    /// ponytail: greedy one-at-a-time; a global layout optimiser only if spreads look poor in real rooms.
    /// </summary>
    public sealed class SurfacePlanner
    {
        private readonly PlacementConfig _config;
        private readonly List<SurfaceSpot> _placed = new List<SurfaceSpot>();

        public SurfacePlanner(PlacementConfig config = null) => _config = config ?? new PlacementConfig();

        public IReadOnlyList<SurfaceSpot> Placed => _placed;

        public void Reset() => _placed.Clear();

        /// <summary>Records a spot taken by something other than <see cref="TryChoose"/> (e.g. the fallback ring).</summary>
        public void Occupy(SurfaceSpot spot) => _placed.Add(spot);

        /// <summary>
        /// Best free candidate for the next object, or false if none qualifies yet (keep walking).
        /// The chosen spot is recorded as occupied. Floor spots are used only when no furniture or wall spot is free,
        /// whatever the scores: objects on the floor are easy to miss and in the way.
        /// </summary>
        /// <param name="user">User (head) position.</param>
        /// <param name="forward">User's look direction; a small tie-breaker towards spots in view.</param>
        /// <param name="relaxed">Ignore the per-surface cap and use <see cref="PlacementConfig.RelaxedSpacing"/>.</param>
        public bool TryChoose(IReadOnlyList<SurfaceSpot> candidates, Vector3 user, Vector3 forward, out SurfaceSpot chosen,
            bool relaxed = false)
        {
            chosen = default;
            var bestScore = float.NegativeInfinity;
            var bestOnFloor = true;
            var flatForward = Flat(forward);
            if (flatForward != Vector3.Zero)
            {
                flatForward = Vector3.Normalize(flatForward);
            }

            foreach (var c in candidates)
            {
                var toSpot = Flat(c.Position - user);
                var distance = toSpot.Length();
                if (distance > _config.MaxDistance || !IsFree(c, relaxed))
                {
                    continue;
                }

                var facing = distance < 1e-3f || flatForward == Vector3.Zero ? 1f : Vector3.Dot(toSpot / distance, flatForward);
                var score = KindScore(c.Kind) + _config.SpreadWeight * Spread(c) + 0.3f * facing - 0.05f * distance;
                var onFloor = c.Kind == SurfaceKind.Floor;
                if (onFloor == bestOnFloor ? score > bestScore : !onFloor)
                {
                    bestScore = score;
                    bestOnFloor = onFloor;
                    chosen = c;
                }
            }

            if (float.IsNegativeInfinity(bestScore))
            {
                return false;
            }

            _placed.Add(chosen);
            return true;
        }

        /// <summary>Even-odd point-in-polygon test (plane boundaries are convex or nearly so, either is fine).</summary>
        public static bool Contains(IReadOnlyList<Vector2> polygon, Vector2 p)
        {
            var inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        private bool IsFree(SurfaceSpot c, bool relaxed)
        {
            var spacing = relaxed ? _config.RelaxedSpacing : _config.MinSpacing;
            var onSurface = 0;
            foreach (var p in _placed)
            {
                if (Vector3.Distance(p.Position, c.Position) < spacing)
                {
                    return false;
                }

                if (p.SurfaceId == c.SurfaceId)
                {
                    onSurface++;
                }
            }

            return relaxed || c.Kind == SurfaceKind.Floor || onSurface < _config.MaxPerSurface;
        }

        private float Spread(SurfaceSpot c)
        {
            var nearest = _config.SpreadCap;
            foreach (var p in _placed)
            {
                nearest = Math.Min(nearest, Vector3.Distance(p.Position, c.Position));
            }

            return nearest;
        }

        private float KindScore(SurfaceKind kind) =>
            kind == SurfaceKind.Furniture ? _config.FurnitureScore : kind == SurfaceKind.Wall ? _config.WallScore : _config.FloorScore;

        private static Vector3 Flat(Vector3 v)
        {
            v.Y = 0;
            return v.Length() < 1e-6f ? Vector3.Zero : v;
        }
    }
}

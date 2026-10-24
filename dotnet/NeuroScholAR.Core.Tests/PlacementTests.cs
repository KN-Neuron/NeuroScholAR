using System.Collections.Generic;
using System.Numerics;
using NeuroScholAR.Core.Learning;
using NUnit.Framework;

namespace NeuroScholAR.Core.Tests
{
    public class PlacementTests
    {
        private static readonly Vector3 User = new Vector3(0, 1.6f, 0);
        private static readonly Vector3 Ahead = new Vector3(0, 0, 1);

        [Test]
        public void PrefersFurniture_ThenSpreadsOut()
        {
            var planner = new SurfacePlanner();
            var spots = new List<SurfaceSpot>
            {
                new SurfaceSpot(new Vector3(0, 0, 2.5f), SurfaceKind.Floor, "floor"),
                new SurfaceSpot(new Vector3(0.5f, 0.7f, 1.5f), SurfaceKind.Furniture, "desk"),
                new SurfaceSpot(new Vector3(0, 0.7f, -1.5f), SurfaceKind.Furniture, "bed"), // behind the user
            };

            Assert.That(planner.TryChoose(spots, User, Ahead, out var first), Is.True);
            Assert.That(first.SurfaceId, Is.EqualTo("desk"));

            // The desk spot is taken; next is the bed, furthest from the desk (spread wins over "in view").
            Assert.That(planner.TryChoose(spots, User, Ahead, out var second), Is.True);
            Assert.That(second.SurfaceId, Is.EqualTo("bed"));
            Assert.That(planner.TryChoose(spots, User, Ahead, out var third), Is.True);
            Assert.That(third.SurfaceId, Is.EqualTo("floor"));
            Assert.That(planner.TryChoose(spots, User, Ahead, out _), Is.False, "all taken");
        }

        [Test]
        public void RespectsMaxPerSurface_AndDistance()
        {
            var planner = new SurfacePlanner(new PlacementConfig { MaxPerSurface = 1 });
            var spots = new List<SurfaceSpot>
            {
                new SurfaceSpot(new Vector3(-1, 0.7f, 1), SurfaceKind.Furniture, "desk"),
                new SurfaceSpot(new Vector3(1, 0.7f, 1), SurfaceKind.Furniture, "desk"),
                new SurfaceSpot(new Vector3(0, 1.5f, 12), SurfaceKind.Wall, "far wall"),
            };

            Assert.That(planner.TryChoose(spots, User, Ahead, out _), Is.True);
            Assert.That(planner.TryChoose(spots, User, Ahead, out _), Is.False, "desk is full, wall too far");
        }

        [Test]
        public void FloorIsLastResort_AndRelaxedPassSharesSurfaces()
        {
            var planner = new SurfacePlanner(new PlacementConfig { MaxPerSurface = 1 });
            var spots = new List<SurfaceSpot>
            {
                // Far, in view and well spread: would win on score alone.
                new SurfaceSpot(new Vector3(0, 0, 6), SurfaceKind.Floor, "floor"),
                new SurfaceSpot(new Vector3(0, 0.7f, 1), SurfaceKind.Furniture, "desk"),
                new SurfaceSpot(new Vector3(0.6f, 0.7f, 1), SurfaceKind.Furniture, "desk"),
            };

            Assert.That(planner.TryChoose(spots, User, Ahead, out var first), Is.True);
            Assert.That(first.SurfaceId, Is.EqualTo("desk"));

            // Strict: the desk is full, so only the floor is left.
            Assert.That(planner.TryChoose(spots, User, Ahead, out var strict), Is.True);
            Assert.That(strict.SurfaceId, Is.EqualTo("floor"));

            // Relaxed: shares the desk (0.6 m apart) instead of nothing.
            Assert.That(planner.TryChoose(spots, User, Ahead, out _), Is.False);
            Assert.That(planner.TryChoose(spots, User, Ahead, out var relaxed, relaxed: true), Is.True);
            Assert.That(relaxed.Position.X, Is.EqualTo(0.6f));
        }

        [Test]
        public void PointInPolygon()
        {
            var square = new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
            Assert.That(SurfacePlanner.Contains(square, Vector2.Zero), Is.True);
            Assert.That(SurfacePlanner.Contains(square, new Vector2(1.5f, 0)), Is.False);
        }
    }
}

namespace NeuroScholAR.Core.Tests
{
    public class PlacementSpreadTests
    {
        [Test]
        public void SpreadsOverTheRoom_InsteadOfClusteringNearTheUser()
        {
            // A 6 m floor sampled every 0.5 m; the user stands at one end looking along it.
            var spots = new List<SurfaceSpot>();
            for (var x = 0f; x <= 6f; x += 0.5f)
            {
                spots.Add(new SurfaceSpot(new Vector3(x, 0, 0), SurfaceKind.Floor, "floor"));
            }

            var planner = new SurfacePlanner();
            for (var i = 0; i < 3; i++)
            {
                Assert.That(planner.TryChoose(spots, new Vector3(0, 1.6f, 0), new Vector3(1, 0, 0), out _), Is.True);
            }

            var xs = new List<float>();
            foreach (var p in planner.Placed)
            {
                xs.Add(p.Position.X);
            }

            xs.Sort();
            Assert.That(xs[2] - xs[0], Is.GreaterThanOrEqualTo(5.5f), "uses the whole floor");
            Assert.That(xs[1] - xs[0], Is.GreaterThanOrEqualTo(2f));
        }
    }
}

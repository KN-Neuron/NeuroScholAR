namespace NeuroScholAR.Core.Learning
{
    /// <summary>
    /// Notices when placing the palace objects is stuck: after the scan, no new object for
    /// <see cref="PlacementConfig.PlacementTimeoutSeconds"/>. Without it a room with no free spot left (cluttered desks,
    /// small cabinet tops) waited forever with no message. Engine-free so the timing is unit-tested with a manual clock.
    /// </summary>
    public sealed class PlacementWatchdog
    {
        private readonly IClock _clock;
        private readonly PlacementConfig _config;
        private double _since = double.NaN;
        private int _placed;

        public PlacementWatchdog(IClock clock, PlacementConfig config = null)
        {
            _clock = clock;
            _config = config ?? new PlacementConfig();
        }

        public double TimeoutSeconds => _config.PlacementTimeoutSeconds;

        /// <summary>Seconds since the last object (or since waiting started); 0 while not waiting.</summary>
        public double SecondsWithoutProgress => double.IsNaN(_since) ? 0 : _clock.NowSeconds - _since;

        public void Reset()
        {
            _since = double.NaN;
            _placed = 0;
        }

        /// <summary>
        /// Call every frame. <paramref name="waiting"/>: the scan is over and objects are still missing (false while
        /// scanning, paused or already failed; that restarts the timer). True once the timeout has passed.
        /// </summary>
        public bool Check(bool waiting, int placed)
        {
            var now = _clock.NowSeconds;
            if (!waiting)
            {
                _since = double.NaN;
                _placed = placed;
                return false;
            }

            if (double.IsNaN(_since) || placed > _placed)
            {
                _since = now;
                _placed = placed;
            }

            return now - _since >= _config.PlacementTimeoutSeconds;
        }
    }
}

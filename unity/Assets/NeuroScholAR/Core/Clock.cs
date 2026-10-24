using System.Diagnostics;

namespace NeuroScholAR.Core
{
    /// <summary>Monotonic time source. Injected everywhere timing matters so tests can drive time by hand.</summary>
    public interface IClock
    {
        double NowSeconds { get; }
    }

    public sealed class SystemClock : IClock
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        public double NowSeconds => _watch.Elapsed.TotalSeconds;
    }

    public sealed class ManualClock : IClock
    {
        public double NowSeconds { get; set; }
        public void Advance(double seconds) => NowSeconds += seconds;
    }
}

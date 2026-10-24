using System;

namespace NeuroScholAR.Core.Session
{
    /// <summary>
    /// The 30-second baseline as a mini-game: a target moves slowly and occasionally turns green; the user
    /// keeps looking at it and presses when it turns green. Low effort, steady attention: a good resting
    /// "engaged" baseline. The score is only motivation; the EEG is what matters.
    /// </summary>
    public sealed class CalibrationGame
    {
        private readonly Random _random;
        private readonly double _minGapS;
        private readonly double _maxGapS;
        private readonly double _windowS;
        private double _nextGreenAt;
        private double _greenUntil = -1;

        /// <param name="windowS">
        /// How long the target stays green. 3 s: a fingertip poke (reach, push the button in) takes 1-2 s on the
        /// glasses, and a game you keep losing is not the calm, engaged baseline we want.
        /// </param>
        public CalibrationGame(double startS, int seed = 0, double minGapS = 3, double maxGapS = 6, double windowS = 3)
        {
            _random = new Random(seed);
            _minGapS = minGapS;
            _maxGapS = maxGapS;
            _windowS = windowS;
            _nextGreenAt = startS + Gap();
        }

        public bool IsGreen { get; private set; }
        public int Hits { get; private set; }
        public int Misses { get; private set; }
        public int Targets { get; private set; }

        /// <summary>Target position on a unit circle, slowly drifting (degrees). Pure function of time.</summary>
        public static double AngleAt(double t) => 25 * Math.Sin(t * 0.7) + 10 * Math.Sin(t * 1.9);

        public void Tick(double now)
        {
            if (IsGreen && now >= _greenUntil)
            {
                IsGreen = false;
                Misses++;
                _nextGreenAt = now + Gap();
            }
            else if (!IsGreen && now >= _nextGreenAt)
            {
                IsGreen = true;
                Targets++;
                _greenUntil = now + _windowS;
            }
        }

        public void Press(double now)
        {
            if (IsGreen)
            {
                IsGreen = false;
                Hits++;
                _nextGreenAt = now + Gap();
            }
            else
            {
                Misses++;
            }
        }

        private double Gap() => _minGapS + _random.NextDouble() * (_maxGapS - _minGapS);
    }
}

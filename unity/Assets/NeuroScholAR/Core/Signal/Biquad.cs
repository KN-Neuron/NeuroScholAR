using System;

namespace NeuroScholAR.Core.Signal
{
    /// <summary>
    /// Second-order IIR section (RBJ cookbook formulas). Causal and O(1) per sample, so it can filter the
    /// live stream sample by sample. Chaining high-pass and low-pass sections gives the band-pass.
    /// </summary>
    public sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        private const double ButterworthQ = 0.7071067811865476;

        public static Biquad HighPass(double cutoffHz, double fs)
        {
            var (cos, alpha) = Prewarp(cutoffHz, fs, ButterworthQ);
            return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public static Biquad LowPass(double cutoffHz, double fs)
        {
            var (cos, alpha) = Prewarp(cutoffHz, fs, ButterworthQ);
            return new Biquad((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        /// <summary>Narrow band-stop for mains hum. Q≈30 removes ±1 Hz around the line frequency.</summary>
        public static Biquad Notch(double centerHz, double fs, double q = 30)
        {
            var (cos, alpha) = Prewarp(centerHz, fs, q);
            return new Biquad(1, -2 * cos, 1, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public double Process(double x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return y;
        }

        public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;

        private static (double cos, double alpha) Prewarp(double f, double fs, double q)
        {
            if (f <= 0 || f >= fs / 2)
            {
                throw new ArgumentOutOfRangeException(nameof(f), "Cutoff must be between 0 and Nyquist");
            }

            var w = 2 * Math.PI * f / fs;
            return (Math.Cos(w), Math.Sin(w) / (2 * q));
        }
    }
}

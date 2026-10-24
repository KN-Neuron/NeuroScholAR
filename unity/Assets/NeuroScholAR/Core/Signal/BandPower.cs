using System;

namespace NeuroScholAR.Core.Signal
{
    /// <summary>Absolute band powers in µV² for one window.</summary>
    public struct BandPowers
    {
        public double Delta, Theta, Alpha, Beta, Gamma;

        /// <summary>β/(θ+α) engagement ratio. NaN when θ+α is ~0 (flat or broken signal), never ±∞.</summary>
        public double FocusRatio
        {
            get
            {
                var denominator = Theta + Alpha;
                return denominator > 1e-12 ? Beta / denominator : double.NaN;
            }
        }

        public void Add(BandPowers other)
        {
            Delta += other.Delta;
            Theta += other.Theta;
            Alpha += other.Alpha;
            Beta += other.Beta;
            Gamma += other.Gamma;
        }

        public void Scale(double factor)
        {
            Delta *= factor;
            Theta *= factor;
            Alpha *= factor;
            Beta *= factor;
            Gamma *= factor;
        }
    }

    /// <summary>
    /// Welch PSD (Hann window, 50 % overlap) integrated over the classic EEG bands.
    /// Averaging several segments trades frequency resolution for a much less noisy estimate.
    /// All buffers are allocated in the constructor.
    /// </summary>
    public sealed class WelchBandPower
    {
        // Band edges in Hz, [low, high).
        public const double DeltaLow = 1, ThetaLow = 4, AlphaLow = 8, BetaLow = 13, GammaLow = 30, GammaHigh = 45;

        private readonly Fft _fft;
        private readonly double[] _window;
        private readonly double[] _re;
        private readonly double[] _im;
        private readonly double[] _psd;
        private readonly double _scale;
        private readonly double _binHz;

        public WelchBandPower(int segmentLength, double samplingRateHz)
        {
            _fft = new Fft(segmentLength);
            _window = new double[segmentLength];
            _re = new double[segmentLength];
            _im = new double[segmentLength];
            _psd = new double[segmentLength / 2 + 1];
            _binHz = samplingRateHz / segmentLength;

            var windowEnergy = 0.0;
            for (var i = 0; i < segmentLength; i++)
            {
                _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / segmentLength);
                windowEnergy += _window[i] * _window[i];
            }

            // One-sided PSD in µV²/Hz, so summing bins × binHz gives power (A²/2 for a sine of amplitude A).
            _scale = 2.0 / (samplingRateHz * windowEnergy);
        }

        public int SegmentLength => _fft.Size;

        /// <summary>Band powers of <paramref name="signal"/>[0..length). Needs length ≥ segment length.</summary>
        public BandPowers Compute(float[] signal, int length)
        {
            var n = _fft.Size;
            if (length < n)
            {
                throw new ArgumentException("Window shorter than one Welch segment", nameof(length));
            }

            Array.Clear(_psd, 0, _psd.Length);
            var hop = n / 2;
            var segments = 0;
            for (var start = 0; start + n <= length; start += hop, segments++)
            {
                var mean = 0.0;
                for (var i = 0; i < n; i++)
                {
                    mean += signal[start + i];
                }

                mean /= n;
                for (var i = 0; i < n; i++)
                {
                    _re[i] = (signal[start + i] - mean) * _window[i];
                    _im[i] = 0;
                }

                _fft.Transform(_re, _im);
                for (var k = 0; k < _psd.Length; k++)
                {
                    _psd[k] += _re[k] * _re[k] + _im[k] * _im[k];
                }
            }

            var norm = _scale / segments;
            return new BandPowers
            {
                Delta = Integrate(DeltaLow, ThetaLow) * norm,
                Theta = Integrate(ThetaLow, AlphaLow) * norm,
                Alpha = Integrate(AlphaLow, BetaLow) * norm,
                Beta = Integrate(BetaLow, GammaLow) * norm,
                Gamma = Integrate(GammaLow, GammaHigh) * norm,
            };
        }

        private double Integrate(double lowHz, double highHz)
        {
            var sum = 0.0;
            var first = (int)Math.Ceiling(lowHz / _binHz);
            var last = Math.Min(_psd.Length - 1, (int)Math.Ceiling(highHz / _binHz) - 1);
            for (var k = first; k <= last; k++)
            {
                sum += _psd[k];
            }

            return sum * _binHz;
        }
    }
}

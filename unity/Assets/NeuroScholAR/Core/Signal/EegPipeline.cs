using System;
using System.Collections.Generic;

namespace NeuroScholAR.Core.Signal
{
    /// <summary>Every signal-processing threshold in one place. Defaults are tuned for BrainBit 2 at 250 Hz.</summary>
    public sealed class EegPipelineConfig
    {
        /// <summary>Mains frequency for the notch: 50 Hz in Europe, 60 Hz in the Americas.</summary>
        public double LineFrequencyHz = 50;
        public double HighPassHz = 1;
        public double LowPassHz = 45;

        /// <summary>Analysis window. Rounded up to a power of two in samples (2 s → 512 at 250 Hz).</summary>
        public double WindowSeconds = 2;

        /// <summary>How often a window is analysed. Spec: focus refreshes every 1 s.</summary>
        public double HopSeconds = 1;

        /// <summary>Filter settle time after (re)connect; windows overlapping it are skipped.</summary>
        public double WarmupSeconds = 0.5;

        /// <summary>A timestamp jump bigger than this means packets were lost: filters and buffers restart.</summary>
        public long MaxGapMs = 250;

        public double MaxNanFraction = 0.05;
        public double OcularThresholdUv = 60;
        public double HighAmplitudeUv = 120;

        /// <summary>Electrode contact: band-passed std outside [min, max] µV, or too many NaN, is "Słaby kontakt".</summary>
        public double ContactMinStdUv = 0.5;
        public double ContactMaxStdUv = 80;
        public double ContactMaxNanFraction = 0.2;

        public string LeftOcularChannel = "T3";
        public string RightOcularChannel = "T4";
    }

    /// <summary>Result of analysing one window (once per hop).</summary>
    public struct EegFrame
    {
        public long TimestampMs;
        public BandPowers Bands;

        /// <summary>β/(θ+α) over clean channels; NaN when the window was rejected.</summary>
        public double Ratio;

        public ArtifactKind Artifact;

        /// <summary>Per channel, true = OK, false = "Słaby kontakt". New array each frame (1 Hz, safe to keep).</summary>
        public bool[] ContactOk;
    }

    /// <summary>
    /// Single-threaded streaming pipeline: notch → band-pass → ring buffer → (every hop) contact check,
    /// artifact rejection, Welch band powers. Owned by exactly one thread (<see cref="EegProcessingWorker"/>).
    /// </summary>
    public sealed class EegPipeline
    {
        private readonly EegPipelineConfig _config;
        private readonly int _channelCount;
        private readonly Biquad[][] _filters; // per channel: notch, high-pass, low-pass
        private readonly RingBuffer[] _rings;
        private readonly float[][] _window;
        private readonly bool[] _usable;
        private readonly WelchBandPower _welch;
        private readonly ArtifactDetector _detector;
        private readonly int _hopSamples;
        private readonly int _warmupSamples;
        private int _sinceReset;
        private int _sinceHop;
        private long _lastTimestampMs = long.MinValue;

        public EegPipeline(IReadOnlyList<string> channelNames, int samplingRateHz, EegPipelineConfig config = null)
        {
            if (channelNames == null || channelNames.Count == 0)
            {
                throw new ArgumentException("At least one channel is required", nameof(channelNames));
            }

            if (samplingRateHz <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(samplingRateHz));
            }

            _config = config ?? new EegPipelineConfig();
            _channelCount = channelNames.Count;
            SamplingRateHz = samplingRateHz;
            WindowSamples = NextPowerOfTwo((int)Math.Ceiling(_config.WindowSeconds * samplingRateHz));
            _hopSamples = Math.Max(1, (int)Math.Round(_config.HopSeconds * samplingRateHz));
            _warmupSamples = (int)Math.Round(_config.WarmupSeconds * samplingRateHz);
            _welch = new WelchBandPower(WindowSamples / 2, samplingRateHz);

            _filters = new Biquad[_channelCount][];
            _rings = new RingBuffer[_channelCount];
            _window = new float[_channelCount][];
            _usable = new bool[_channelCount];
            var notchOk = _config.LineFrequencyHz < samplingRateHz / 2.0;
            for (var ch = 0; ch < _channelCount; ch++)
            {
                _filters[ch] = notchOk
                    ? new[]
                    {
                        Biquad.Notch(_config.LineFrequencyHz, samplingRateHz),
                        Biquad.HighPass(_config.HighPassHz, samplingRateHz),
                        Biquad.LowPass(Math.Min(_config.LowPassHz, samplingRateHz * 0.45), samplingRateHz),
                    }
                    : new[]
                    {
                        Biquad.HighPass(_config.HighPassHz, samplingRateHz),
                        Biquad.LowPass(Math.Min(_config.LowPassHz, samplingRateHz * 0.45), samplingRateHz),
                    };
                _rings[ch] = new RingBuffer(WindowSamples);
                _window[ch] = new float[WindowSamples];
            }

            var names = new List<string>(channelNames);
            _detector = new ArtifactDetector(_config, names.IndexOf(_config.LeftOcularChannel),
                names.IndexOf(_config.RightOcularChannel));
        }

        public int SamplingRateHz { get; }
        public int WindowSamples { get; }

        /// <summary>Feeds one sample. Returns true and fills <paramref name="frame"/> when a window was analysed.</summary>
        public bool Push(long timestampMs, float[] channelsUv, out EegFrame frame)
        {
            frame = default;
            if (channelsUv == null || channelsUv.Length != _channelCount)
            {
                return false; // malformed sample: drop it, the NaN accounting covers the gap
            }

            if (_lastTimestampMs != long.MinValue &&
                (timestampMs - _lastTimestampMs > _config.MaxGapMs || timestampMs < _lastTimestampMs))
            {
                Reset();
            }

            _lastTimestampMs = timestampMs;
            for (var ch = 0; ch < _channelCount; ch++)
            {
                var x = channelsUv[ch];
                if (float.IsNaN(x) || float.IsInfinity(x))
                {
                    _rings[ch].Push(float.NaN); // keep the NaN out of the IIR state, or it poisons it forever
                    continue;
                }

                double y = x;
                var chain = _filters[ch];
                for (var f = 0; f < chain.Length; f++)
                {
                    y = chain[f].Process(y);
                }

                _rings[ch].Push((float)y);
            }

            _sinceReset++;
            _sinceHop++;
            if (_sinceHop < _hopSamples || !_rings[0].IsFull || _sinceReset < WindowSamples + _warmupSamples)
            {
                return false;
            }

            _sinceHop = 0;
            frame = Analyze(timestampMs);
            return true;
        }

        /// <summary>Drops filter state and buffered samples, e.g. after a reconnect.</summary>
        public void Reset()
        {
            for (var ch = 0; ch < _channelCount; ch++)
            {
                foreach (var filter in _filters[ch])
                {
                    filter.Reset();
                }

                _rings[ch].Clear();
            }

            _sinceReset = 0;
            _sinceHop = 0;
            _lastTimestampMs = long.MinValue;
        }

        private EegFrame Analyze(long timestampMs)
        {
            var contact = new bool[_channelCount];
            var anyUsable = false;
            for (var ch = 0; ch < _channelCount; ch++)
            {
                _rings[ch].CopyTo(_window[ch]);
                contact[ch] = ContactOk(_window[ch]);
                _usable[ch] = contact[ch];
                anyUsable |= contact[ch];
            }

            var frame = new EegFrame { TimestampMs = timestampMs, ContactOk = contact, Ratio = double.NaN };
            if (!anyUsable)
            {
                frame.Artifact = ArtifactKind.Corrupted;
                return frame;
            }

            frame.Artifact = _detector.Detect(_window, WindowSamples, _usable);
            if (frame.Artifact != ArtifactKind.None)
            {
                return frame; // spec: exclude artifact windows from the focus index
            }

            var used = 0;
            for (var ch = 0; ch < _channelCount; ch++)
            {
                if (!_usable[ch])
                {
                    continue;
                }

                FillNan(_window[ch]);
                frame.Bands.Add(_welch.Compute(_window[ch], WindowSamples));
                used++;
            }

            frame.Bands.Scale(1.0 / used);
            frame.Ratio = frame.Bands.FocusRatio;
            if (double.IsNaN(frame.Ratio))
            {
                frame.Artifact = ArtifactKind.Corrupted;
            }

            return frame;
        }

        private bool ContactOk(float[] data)
        {
            var n = 0;
            var sum = 0.0;
            var sumSq = 0.0;
            foreach (var v in data)
            {
                if (float.IsNaN(v))
                {
                    continue;
                }

                n++;
                sum += v;
                sumSq += v * (double)v;
            }

            if (n == 0 || 1.0 - (double)n / data.Length > _config.ContactMaxNanFraction)
            {
                return false;
            }

            var mean = sum / n;
            var std = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
            return std >= _config.ContactMinStdUv && std <= _config.ContactMaxStdUv;
        }

        /// <summary>Replaces the few tolerated NaNs with 0 (the band-passed mean) so the FFT stays finite.</summary>
        private static void FillNan(float[] data)
        {
            for (var i = 0; i < data.Length; i++)
            {
                if (float.IsNaN(data[i]))
                {
                    data[i] = 0;
                }
            }
        }

        private static int NextPowerOfTwo(int n)
        {
            var p = 1;
            while (p < n)
            {
                p <<= 1;
            }

            return p;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroScholAR.Core.Eeg
{
    /// <summary>Knobs for <see cref="SimulatedEegSource"/>. Defaults look like a BrainBit 2.</summary>
    public sealed class SimulatedEegOptions
    {
        public string[] ChannelNames = { "O1", "O2", "T3", "T4" };
        public int SamplingRateHz = 250;

        /// <summary>Focus profile: session seconds → focus 0..1. Drives the β vs θ/α amplitudes.</summary>
        public Func<double, double> Focus = FocusProfiles.Constant(0.5);

        public double BlinksPerMinute = 6;
        public double EyeMovementsPerMinute = 3;

        /// <summary>Chance per sample that one channel reads NaN (lost BLE packet).</summary>
        public double NanProbability;

        /// <summary>Channel indices with poor contact: they get huge noise, so they read as "Słaby kontakt".</summary>
        public int[] BadContactChannels = Array.Empty<int>();

        public double NoiseUv = 3;
        public double LineNoiseUv = 5;
        public int Seed = 1234;

        /// <summary>When set, <see cref="SimulatedEegSource.ConnectAsync"/> fails with this error (tests the error UI).</summary>
        public EegErrorKind? FailConnectWith;

        /// <summary>
        /// True: a background thread streams in real time (the app). False: the caller drives time with
        /// <see cref="SimulatedEegSource.Advance"/> (tests and the scripted end-to-end run).
        /// </summary>
        public bool RealTime = true;
    }

    /// <summary>Ready-made focus profiles.</summary>
    public static class FocusProfiles
    {
        public static Func<double, double> Constant(double focus) => _ => focus;

        /// <summary>Piecewise profile: (startSecond, focus) steps, sorted by start.</summary>
        public static Func<double, double> Steps(params (double startS, double focus)[] steps) => t =>
        {
            var focus = steps.Length > 0 ? steps[0].focus : 0.5;
            foreach (var (startS, f) in steps)
            {
                if (t >= startS)
                {
                    focus = f;
                }
            }

            return focus;
        };

        /// <summary>Slow drift between low and high, like attention waxing and waning.</summary>
        public static Func<double, double> Wave(double periodS) =>
            t => 0.5 + 0.4 * Math.Sin(2 * Math.PI * t / periodS);
    }

    /// <summary>
    /// Synthetic EEG with a known focus level, blinks, eye movements, NaN dropouts and forced disconnects,
    /// so the whole app runs and is tested without hardware.
    /// Focus maps to band amplitudes: β rises and θ/α fall as focus goes up, so β/(θ+α) grows monotonically.
    /// </summary>
    public sealed class SimulatedEegSource : IEegSource
    {
        private const int BatchSize = 25; // 100 ms at 250 Hz, about what the BrainBit SDK delivers
        private const double BlinkUv = 180;
        private const double BlinkWidthS = 0.12;
        private const double SaccadeUv = 90;
        private const double SaccadeLengthS = 0.4;

        private readonly SimulatedEegOptions _options;
        private readonly Random _random;
        private readonly object _gate = new object();
        private readonly int[] _eyeChannels;
        private long _sampleIndex;
        private double _nextBlinkS;
        private double _nextSaccadeS;
        private double _blinkCenterS = double.NegativeInfinity;
        private double _saccadeStartS = double.NegativeInfinity;
        private double _saccadeSign = 1;
        private CancellationTokenSource _loop;
        private int _failNextConnects;
        private EegConnectionState _state = EegConnectionState.Disconnected;

        public SimulatedEegSource(SimulatedEegOptions options = null)
        {
            _options = options ?? new SimulatedEegOptions();
            _random = new Random(_options.Seed);
            _eyeChannels = new[] { IndexOf("T3"), IndexOf("T4") };
            _nextBlinkS = NextInterval(_options.BlinksPerMinute);
            _nextSaccadeS = NextInterval(_options.EyeMovementsPerMinute);
        }

        public event Action<IReadOnlyList<EegSample>> SamplesReceived;
        public event Action<EegConnectionState> StateChanged;

        public IReadOnlyList<string> ChannelNames => _options.ChannelNames;
        public int SamplingRateHz => _options.SamplingRateHz;
        public EegConnectionState State => _state;

        /// <summary>Session start, so timestamps look real. Fixed for reproducible tests.</summary>
        public long EpochMs { get; set; } = 1729180000000;

        /// <summary>Seconds of signal generated so far.</summary>
        public double ElapsedS => (double)_sampleIndex / _options.SamplingRateHz;

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Same state sequence as the real adapter: Connecting, then Disconnected right before a failure throws.
            SetState(EegConnectionState.Connecting);
            if (_options.FailConnectWith.HasValue)
            {
                SetState(EegConnectionState.Disconnected);
                throw new EegConnectionException(_options.FailConnectWith.Value, "Simulated connection failure");
            }

            if (Interlocked.Decrement(ref _failNextConnects) >= 0)
            {
                SetState(EegConnectionState.Disconnected);
                throw new EegConnectionException(EegErrorKind.DeviceNotFound, "Simulated headband out of range");
            }

            Interlocked.Exchange(ref _failNextConnects, 0);

            SetState(EegConnectionState.Connected);
            if (_options.RealTime)
            {
                _loop = new CancellationTokenSource();
                var token = _loop.Token;
                Task.Run(() => RunRealTime(token), token);
            }

            return Task.CompletedTask;
        }

        public void Disconnect()
        {
            StopLoop();
            SetState(EegConnectionState.Disconnected);
        }

        /// <summary>
        /// Simulates the headband dropping out (battery, range): an unrequested disconnect, after which the next
        /// <paramref name="failedReconnects"/> connection attempts fail as if the device were out of range.
        /// </summary>
        public void SimulateLinkLoss(int failedReconnects = 0)
        {
            Interlocked.Exchange(ref _failNextConnects, failedReconnects);
            Disconnect();
        }

        /// <summary>Generates <paramref name="seconds"/> of signal and raises <see cref="SamplesReceived"/> in batches.</summary>
        public void Advance(double seconds)
        {
            var count = (long)Math.Round(seconds * _options.SamplingRateHz);
            while (count > 0 && _state == EegConnectionState.Connected)
            {
                var n = (int)Math.Min(BatchSize, count);
                SamplesReceived?.Invoke(Generate(n));
                count -= n;
            }
        }

        public void Dispose() => StopLoop();

        private void RunRealTime(CancellationToken token)
        {
            var clock = Stopwatch.StartNew();
            var startIndex = _sampleIndex;
            while (!token.IsCancellationRequested)
            {
                var due = startIndex + (long)(clock.Elapsed.TotalSeconds * _options.SamplingRateHz);
                if (due - _sampleIndex >= BatchSize)
                {
                    SamplesReceived?.Invoke(Generate(BatchSize));
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }

        private EegSample[] Generate(int count)
        {
            lock (_gate)
            {
                var batch = new EegSample[count];
                var fs = _options.SamplingRateHz;
                for (var i = 0; i < count; i++, _sampleIndex++)
                {
                    var t = (double)_sampleIndex / fs;
                    var focus = Math.Max(0, Math.Min(1, _options.Focus(t)));
                    UpdateEyeEvents(t);
                    var eye = EyeArtifact(t);

                    var values = new float[_options.ChannelNames.Length];
                    for (var ch = 0; ch < values.Length; ch++)
                    {
                        values[ch] = (float)Channel(ch, t, focus, eye);
                    }

                    if (_options.NanProbability > 0 && _random.NextDouble() < _options.NanProbability)
                    {
                        values[_random.Next(values.Length)] = float.NaN;
                    }

                    batch[i] = new EegSample(EpochMs + (long)(t * 1000), values);
                }

                return batch;
            }
        }

        private double Channel(int ch, double t, double focus, (double blink, double saccade) eye)
        {
            // Amplitudes in µV. At focus 0.5 the ratio is about 1, so the baseline sits mid-scale.
            var phase = ch * 0.7; // decorrelate channels a bit
            var delta = 10 * Math.Sin(2 * Math.PI * 2 * t + phase);
            var theta = (12 - 8 * focus) * Math.Sin(2 * Math.PI * 6 * t + phase);
            var alpha = (14 - 8 * focus) * Math.Sin(2 * Math.PI * 10 * t + phase);
            var beta = (4 + 16 * focus) * Math.Sin(2 * Math.PI * 20 * t + phase);
            var gamma = 2 * Math.Sin(2 * Math.PI * 38 * t + phase);
            var line = _options.LineNoiseUv * Math.Sin(2 * Math.PI * 50 * t);
            var noiseScale = Array.IndexOf(_options.BadContactChannels, ch) >= 0 ? 80 : 1;
            var noise = _options.NoiseUv * noiseScale * Gaussian();
            var value = delta + theta + alpha + beta + gamma + line + noise;

            if (ch == _eyeChannels[0])
            {
                value += eye.blink + eye.saccade;
            }
            else if (ch == _eyeChannels[1])
            {
                value += eye.blink - eye.saccade; // horizontal EOG: opposite polarity on the two temples
            }

            return value;
        }

        private void UpdateEyeEvents(double t)
        {
            if (t >= _nextBlinkS)
            {
                _blinkCenterS = t + BlinkWidthS * 2;
                _nextBlinkS = t + NextInterval(_options.BlinksPerMinute);
            }

            if (t >= _nextSaccadeS)
            {
                _saccadeStartS = t;
                _saccadeSign = _random.NextDouble() < 0.5 ? -1 : 1;
                _nextSaccadeS = t + NextInterval(_options.EyeMovementsPerMinute);
            }
        }

        private (double blink, double saccade) EyeArtifact(double t)
        {
            var d = (t - _blinkCenterS) / BlinkWidthS;
            var blink = BlinkUv * Math.Exp(-0.5 * d * d);
            var inSaccade = t >= _saccadeStartS && t < _saccadeStartS + SaccadeLengthS;
            return (blink, inSaccade ? _saccadeSign * SaccadeUv : 0);
        }

        private double NextInterval(double perMinute) =>
            perMinute <= 0 ? double.PositiveInfinity : (60.0 / perMinute) * (0.5 + _random.NextDouble());

        private double Gaussian()
        {
            var u1 = 1.0 - _random.NextDouble();
            var u2 = _random.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        private int IndexOf(string name) => Array.IndexOf(_options.ChannelNames, name);

        private void StopLoop()
        {
            _loop?.Cancel();
            _loop?.Dispose();
            _loop = null;
        }

        private void SetState(EegConnectionState state)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
            StateChanged?.Invoke(state);
        }
    }
}

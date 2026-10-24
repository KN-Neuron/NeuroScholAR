#if NEUROSDK2
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeuroSDK;
using NeuroScholAR.Core.Eeg;

namespace NeuroScholAR.App.Eeg
{
    /// <summary>
    /// <see cref="IEegSource"/> over the BrainBit NeuroSDK2 (same API as the XNeuroSDK NuGet package).
    /// Compiled only when the SDK is imported and the NEUROSDK2 scripting define is set (see README).
    /// SDK callbacks arrive on SDK threads; we convert V → µV and forward without touching Unity.
    /// </summary>
    public sealed class BrainBitEegSource : IEegSource
    {
        private const int SamplingRate = 250; // BrainBit 2 streams at a fixed 250 Hz
        private const long ReanchorDriftMs = 1000;
        private static readonly string[] Channels = { "O1", "O2", "T3", "T4" };

        private readonly TimeSpan _scanTime;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly long _epochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private Scanner _scanner;
        private BrainBit2Sensor _sensor;
        private int[] _channelMap; // our channel index → SDK sample index
        private long _sampleCount;
        private long _anchorMs = -1;
        private volatile bool _disconnectRequested;

        public BrainBitEegSource(TimeSpan? scanTime = null) => _scanTime = scanTime ?? TimeSpan.FromSeconds(8);

        public event Action<IReadOnlyList<EegSample>> SamplesReceived;
        public event Action<EegConnectionState> StateChanged;

        public IReadOnlyList<string> ChannelNames => Channels;
        public int SamplingRateHz => SamplingRate;
        public EegConnectionState State { get; private set; } = EegConnectionState.Disconnected;

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            ReleaseSensor();
            _disconnectRequested = false;
            SetState(EegConnectionState.Connecting);
            try
            {
                _scanner = new Scanner(SensorFamily.SensorLEBrainBit2, SensorFamily.SensorLEBrainBitPro,
                    SensorFamily.SensorLEBrainBitFlex);
                _scanner.Start();
                await Task.Delay(_scanTime, cancellationToken).ConfigureAwait(false);
                _scanner.Stop();

                var found = _scanner.Sensors;
                UnityEngine.Debug.Log($"NeuroScholAR: BrainBit scan found {found?.Count ?? 0} device(s): " +
                                      string.Join(", ", found?.Select(s => $"{s.Name} [{s.SensFamily}] {s.Address}") ?? Enumerable.Empty<string>()));
                if (found == null || found.Count == 0)
                {
                    throw new EegConnectionException(EegErrorKind.DeviceNotFound,
                        "Nie znaleziono opaski. Włącz ją i zbliż do telefonu.");
                }

                // CreateSensor blocks while the BLE link is set up, keep it off the caller's thread.
                var info = found[0];
                _sensor = await Task.Run(() => (BrainBit2Sensor)_scanner.CreateSensor(info), cancellationToken)
                    .ConfigureAwait(false);
                _channelMap = MapChannels(_sensor.SupportedChannelsBrainBit2.Select(c => c.Name).ToList());
                _sensor.EventSensorStateChanged += OnSensorState;
                _sensor.EventBrainBit2SignalDataRecived += OnSignal;
                _anchorMs = -1;
                _sensor.ExecCommand(SensorCommand.CommandStartSignal);
                SetState(EegConnectionState.Connected);
            }
            catch (EegConnectionException)
            {
                Fail();
                throw;
            }
            catch (OperationCanceledException)
            {
                Fail();
                throw;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("NeuroScholAR: BrainBit connect failed: " + e);
                Fail();
                throw Classify(e);
            }
        }

        public void Disconnect()
        {
            _disconnectRequested = true;
            ReleaseSensor();
            SetState(EegConnectionState.Disconnected);
        }

        public void Dispose() => Disconnect();

        private void OnSignal(ISensor sender, SignalChannelsData[] batch)
        {
            if (batch == null || batch.Length == 0)
            {
                return;
            }

            // The SDK gives no timestamps: count samples at the nominal rate, re-anchoring when the count drifts
            // too far from wall time (lost packets). The jump then makes the pipeline restart, which is right.
            var nowMs = _epochMs + _clock.ElapsedMilliseconds;
            var expected = _anchorMs + _sampleCount * 1000 / SamplingRate;
            if (_anchorMs < 0 || Math.Abs(nowMs - batch.Length * 1000 / SamplingRate - expected) > ReanchorDriftMs)
            {
                _anchorMs = nowMs - batch.Length * 1000 / SamplingRate;
                _sampleCount = 0;
            }

            var samples = new EegSample[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                var raw = batch[i].Samples;
                var uv = new float[Channels.Length];
                for (var ch = 0; ch < uv.Length; ch++)
                {
                    var j = _channelMap[ch];
                    uv[ch] = raw != null && j >= 0 && j < raw.Length ? (float)(raw[j] * 1e6) : float.NaN;
                }

                samples[i] = new EegSample(_anchorMs + _sampleCount * 1000 / SamplingRate, uv);
                _sampleCount++;
            }

            SamplesReceived?.Invoke(samples);
        }

        private void OnSensorState(ISensor sender, SensorState state)
        {
            if (state == SensorState.StateOutOfRange && !_disconnectRequested)
            {
                ReleaseSensor();
                SetState(EegConnectionState.Disconnected); // the connection manager treats this as a link loss
            }
        }

        private static int[] MapChannels(IList<string> sdkNames) =>
            Channels.Select(name => sdkNames.IndexOf(name)).Select((index, ours) => index >= 0 ? index : ours).ToArray();

        private static EegConnectionException Classify(Exception e)
        {
            var message = e.Message ?? string.Empty;
            if (message.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new EegConnectionException(EegErrorKind.PermissionDenied, "Brak uprawnień Bluetooth.", e);
            }

            if (message.IndexOf("bluetooth", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new EegConnectionException(EegErrorKind.BluetoothUnavailable, "Bluetooth jest wyłączony lub niedostępny.", e);
            }

            // The raw exception goes on screen: on the glasses there is no other way to see why the SDK failed.
            return new EegConnectionException(EegErrorKind.ConnectionFailed, $"{e.GetType().Name}: {e.Message}", e);
        }

        private void Fail()
        {
            ReleaseSensor();
            SetState(EegConnectionState.Disconnected);
        }

        private void ReleaseSensor()
        {
            var sensor = _sensor;
            _sensor = null;
            if (sensor != null)
            {
                sensor.EventBrainBit2SignalDataRecived -= OnSignal;
                sensor.EventSensorStateChanged -= OnSensorState;
                try
                {
                    sensor.ExecCommand(SensorCommand.CommandStopSignal);
                    sensor.Disconnect();
                }
                catch (Exception)
                {
                    // Already gone (out of range): nothing to stop.
                }

                sensor.Dispose();
            }

            _scanner?.Dispose();
            _scanner = null;
        }

        private void SetState(EegConnectionState state)
        {
            if (State == state)
            {
                return;
            }

            State = state;
            StateChanged?.Invoke(state);
        }
    }
}
#endif

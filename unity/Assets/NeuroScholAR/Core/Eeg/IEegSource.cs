using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroScholAR.Core.Eeg
{
    /// <summary>
    /// One multi-channel EEG sample. Mirrors the spec schema
    /// <c>{ timestamp_ms, channels: { name: µV }, sampling_rate_hz }</c>, but channel names and the rate
    /// live once on the <see cref="IEegSource"/> instead of on every sample, so no dictionary is allocated per sample.
    /// </summary>
    public readonly struct EegSample
    {
        public EegSample(long timestampMs, float[] channelsUv)
        {
            TimestampMs = timestampMs;
            ChannelsUv = channelsUv;
        }

        public long TimestampMs { get; }

        /// <summary>Microvolts, indexed like <see cref="IEegSource.ChannelNames"/>. May contain NaN (dropped packet).</summary>
        public float[] ChannelsUv { get; }
    }

    public enum EegConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
    }

    public enum EegErrorKind
    {
        BluetoothUnavailable,
        PermissionDenied,
        DeviceNotFound,
        ConnectionFailed,
    }

    /// <summary>Thrown by <see cref="IEegSource.ConnectAsync"/>; the kind decides which message the UI shows.</summary>
    public sealed class EegConnectionException : Exception
    {
        public EegConnectionException(EegErrorKind kind, string message, Exception inner = null)
            : base(message, inner)
        {
            Kind = kind;
        }

        public EegErrorKind Kind { get; }
    }

    /// <summary>
    /// Anything that produces EEG: the BrainBit adapter or the simulator. Events may fire on any thread,
    /// so handlers must only enqueue (see <see cref="Signal.EegProcessingWorker"/>).
    /// </summary>
    public interface IEegSource : IDisposable
    {
        IReadOnlyList<string> ChannelNames { get; }
        int SamplingRateHz { get; }
        EegConnectionState State { get; }

        /// <summary>Raised on a background thread with a batch of consecutive samples.</summary>
        event Action<IReadOnlyList<EegSample>> SamplesReceived;

        /// <summary>Raised on a background thread. A drop to Disconnected that was not requested means the link was lost.</summary>
        event Action<EegConnectionState> StateChanged;

        /// <summary>Scan, connect and start streaming.</summary>
        /// <exception cref="EegConnectionException">Bluetooth off, permission denied, device missing or connect failure.</exception>
        Task ConnectAsync(CancellationToken cancellationToken);

        void Disconnect();
    }
}

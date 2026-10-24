using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using NeuroScholAR.Core.Eeg;
using NeuroScholAR.Core.Focus;

namespace NeuroScholAR.Core.Signal
{
    /// <summary>
    /// Threading boundary. SDK callbacks only enqueue samples; one background thread runs the pipeline and
    /// the focus estimator; the main thread drains finished <see cref="FocusFrame"/>s with <see cref="TryGetFrame"/>.
    /// Nothing heavy ever runs on the render thread.
    /// </summary>
    public sealed class EegProcessingWorker : IDisposable
    {
        private const int MaxQueuedSamples = 250 * 10; // 10 s backlog: beyond that we are hopelessly behind, drop

        private readonly IEegSource _source;
        private readonly EegPipeline _pipeline;
        private readonly FocusEstimator _estimator;
        private readonly object _estimatorGate = new object();
        private readonly ConcurrentQueue<EegSample> _input = new ConcurrentQueue<EegSample>();
        private readonly ConcurrentQueue<FocusFrame> _output = new ConcurrentQueue<FocusFrame>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private Thread _thread;
        private volatile bool _running;
        private volatile bool _resetRequested;

        public EegProcessingWorker(IEegSource source, EegPipelineConfig pipelineConfig = null, FocusConfig focusConfig = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _pipeline = new EegPipeline(source.ChannelNames, source.SamplingRateHz, pipelineConfig);
            _estimator = new FocusEstimator(focusConfig);
            _source.SamplesReceived += OnSamples;
            _source.StateChanged += OnStateChanged;
        }

        /// <summary>Samples dropped because the queue overflowed or the batch was malformed (diagnostics).</summary>
        public long DroppedSamples => Interlocked.Read(ref _dropped);
        private long _dropped;

        public bool IsCalibrated
        {
            get { lock (_estimatorGate) { return _estimator.IsCalibrated; } }
        }

        public CalibrationResult LastCalibration
        {
            get { lock (_estimatorGate) { return _estimator.LastCalibration; } }
        }

        /// <summary>Starts the processing thread. Tests skip this and call <see cref="Pump"/>.</summary>
        public void Start()
        {
            if (_thread != null)
            {
                return;
            }

            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "EEG processing" };
            _thread.Start();
        }

        public void StartCalibration()
        {
            lock (_estimatorGate) { _estimator.StartCalibration(); }
        }

        public void AbortCalibration()
        {
            lock (_estimatorGate) { _estimator.AbortCalibration(); }
        }

        public void SetBaseline(double ratio)
        {
            lock (_estimatorGate) { _estimator.SetBaseline(ratio); }
        }

        /// <summary>Main thread: next finished frame, oldest first.</summary>
        public bool TryGetFrame(out FocusFrame frame) => _output.TryDequeue(out frame);

        /// <summary>Processes everything queued, on the calling thread. Returns frames produced.</summary>
        public int Pump()
        {
            var produced = 0;
            if (_resetRequested)
            {
                // Fresh link: old filter state and half-filled windows describe a different recording.
                _resetRequested = false;
                _pipeline.Reset();
            }

            while (_input.TryDequeue(out var sample))
            {
                if (!_pipeline.Push(sample.TimestampMs, sample.ChannelsUv, out var eegFrame))
                {
                    continue;
                }

                FocusFrame focus;
                lock (_estimatorGate) { focus = _estimator.Process(eegFrame); }
                _output.Enqueue(focus);
                produced++;
            }

            return produced;
        }

        public void Dispose()
        {
            _source.SamplesReceived -= OnSamples;
            _source.StateChanged -= OnStateChanged;
            _running = false;
            _signal.Set();
            _thread?.Join(500);
            _signal.Dispose();
        }

        private void OnStateChanged(EegConnectionState state)
        {
            if (state == EegConnectionState.Connected)
            {
                _resetRequested = true;
            }
        }

        private void OnSamples(IReadOnlyList<EegSample> batch)
        {
            if (batch == null)
            {
                return;
            }

            for (var i = 0; i < batch.Count; i++)
            {
                if (_input.Count >= MaxQueuedSamples)
                {
                    Interlocked.Increment(ref _dropped);
                    continue;
                }

                _input.Enqueue(batch[i]);
            }

            if (_thread != null)
            {
                _signal.Set();
            }
        }

        private void Run()
        {
            while (_running)
            {
                _signal.WaitOne(100);
                try
                {
                    Pump();
                }
                catch (Exception)
                {
                    // A bug in one window must not kill EEG for the rest of the session: restart the stream.
                    Interlocked.Increment(ref _dropped);
                    _pipeline.Reset();
                }
            }
        }
    }
}

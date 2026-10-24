using System;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroScholAR.Core.Eeg
{
    public enum ConnectionLight
    {
        /// <summary>Streaming and every electrode has contact.</summary>
        Green,

        /// <summary>Connecting / reconnecting, or streaming with at least one weak electrode.</summary>
        Yellow,

        /// <summary>Not connected: the session runs without EEG.</summary>
        Red,
    }

    /// <summary>
    /// Owns the connect / lost / reconnect lifecycle so the session never has to care. On an unrequested drop
    /// it raises a one-shot alert and keeps retrying with backoff in the background while the app carries on
    /// in no-EEG mode. All members are safe to poll from the main thread.
    /// </summary>
    public sealed class EegConnectionManager : IDisposable
    {
        private static readonly TimeSpan[] Backoff =
        {
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(15),
        };

        private readonly IEegSource _source;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private CancellationTokenSource _reconnect;
        private volatile bool _userWantsConnection;
        private int _lostAlertPending;
        private int _reconnects;

        /// <param name="delay">Injectable wait (tests pass a no-op); defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
        public EegConnectionManager(IEegSource source, Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _delay = delay ?? Task.Delay;
            _source.StateChanged += OnStateChanged;
        }

        public IEegSource Source => _source;
        public EegConnectionState State { get; private set; } = EegConnectionState.Disconnected;

        /// <summary>Last connect error, for the setup screen ("Włącz Bluetooth", "Brak uprawnień", ...). Null after success.</summary>
        public EegConnectionException LastError { get; private set; }

        /// <summary>Successful automatic reconnects this session (summary diagnostics).</summary>
        public int ReconnectCount => _reconnects;

        public bool IsStreaming => State == EegConnectionState.Connected;

        /// <summary>User pressed "Połącz opaskę". Returns false (and sets <see cref="LastError"/>) on failure.</summary>
        public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
        {
            _userWantsConnection = true;
            State = EegConnectionState.Connecting;
            if (await TryConnectOnce(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            _userWantsConnection = false;
            State = EegConnectionState.Disconnected;
            return false;
        }

        /// <summary>User chose to stop EEG. No alert, no reconnect.</summary>
        public void Disconnect()
        {
            _userWantsConnection = false;
            CancelReconnect();
            _source.Disconnect();
            State = EegConnectionState.Disconnected;
        }

        /// <summary>True exactly once per connection loss: the UI shows its alert, then the flag clears.</summary>
        public bool TakeConnectionLostAlert() => Interlocked.Exchange(ref _lostAlertPending, 0) == 1;

        public static ConnectionLight Light(EegConnectionState state, bool[] contactOk)
        {
            switch (state)
            {
                case EegConnectionState.Connected:
                    return contactOk == null || Array.IndexOf(contactOk, false) < 0 ? ConnectionLight.Green : ConnectionLight.Yellow;
                case EegConnectionState.Connecting:
                case EegConnectionState.Reconnecting:
                    return ConnectionLight.Yellow;
                default:
                    return ConnectionLight.Red;
            }
        }

        public void Dispose()
        {
            _source.StateChanged -= OnStateChanged;
            CancelReconnect();
        }

        private async Task<bool> TryConnectOnce(CancellationToken cancellationToken)
        {
            try
            {
                await _source.ConnectAsync(cancellationToken).ConfigureAwait(false);
                LastError = null;
                State = EegConnectionState.Connected;
                return true;
            }
            catch (EegConnectionException e)
            {
                LastError = e;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                LastError = new EegConnectionException(EegErrorKind.ConnectionFailed, e.Message, e);
            }

            return false;
        }

        private void OnStateChanged(EegConnectionState state)
        {
            // Only a drop of a link we had counts. A source reports Disconnected when an attempt fails too (first
            // connect or a reconnect try): that is a connect error, reported through LastError, not a "lost" alert.
            if (state != EegConnectionState.Disconnected || !_userWantsConnection || State != EegConnectionState.Connected)
            {
                return;
            }

            // Unrequested drop: alert once, degrade to no-EEG, keep trying in the background.
            Interlocked.Exchange(ref _lostAlertPending, 1);
            State = EegConnectionState.Reconnecting;
            CancelReconnect();
            _reconnect = new CancellationTokenSource();
            var token = _reconnect.Token;
            _ = Task.Run(() => ReconnectLoop(token), token);
        }

        private async Task ReconnectLoop(CancellationToken token)
        {
            for (var attempt = 0; !token.IsCancellationRequested && _userWantsConnection; attempt++)
            {
                try
                {
                    await _delay(Backoff[Math.Min(attempt, Backoff.Length - 1)], token).ConfigureAwait(false);
                    if (await TryConnectOnce(token).ConfigureAwait(false))
                    {
                        Interlocked.Increment(ref _reconnects);
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private void CancelReconnect()
        {
            _reconnect?.Cancel();
            _reconnect?.Dispose();
            _reconnect = null;
        }
    }
}

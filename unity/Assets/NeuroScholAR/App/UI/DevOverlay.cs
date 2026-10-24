using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using NeuroScholAR.App.Palace;
using NeuroScholAR.Core.Eeg;
using NeuroScholAR.Core.Session;
using UnityEngine;
using UnityEngine.UI;

namespace NeuroScholAR.App.UI
{
    /// <summary>
    /// Dev mode stats, head-locked on the left of the view on every screen and drawn over everything: frame rate,
    /// app phase, EEG pipeline internals, adaptive rules, calibration game, placement and input state, and the last
    /// log lines (on the glasses there is no console). Text is rebuilt at 4 Hz with the rest of the HUD.
    /// </summary>
    public sealed class DevOverlay : IDisposable
    {
        private const int LogLines = 8;

        private readonly SessionController _app;
        private readonly SurfacePlacer _placer;
        private readonly Interactor _input;
        private readonly GameObject _root;
        private readonly Text _text;
        private readonly StringBuilder _sb = new StringBuilder(1024);
        private readonly ConcurrentQueue<string> _incoming = new ConcurrentQueue<string>();
        private readonly Queue<string> _log = new Queue<string>();
        private IEegSource _countedSource;
        private long _samples;
        private float _rateSince;
        private float _sampleRate;
        private int _frames;
        private float _fps;
        private float _fpsSince;
        private float _worstFrame;
        private float _shownWorstFrame;
        private readonly Queue<bool> _rejected = new Queue<bool>(); // last 60 EEG windows: excluded as artifact?
        private long _lastFrameCount;

        public DevOverlay(Camera camera, SessionController app, SurfacePlacer placer, Interactor input)
        {
            _app = app;
            _placer = placer;
            _input = input;

            var canvas = Ui.WorldCanvas("Dev overlay", camera.transform, new Vector2(760, 440), camera, interactive: false);
            canvas.transform.localPosition = new Vector3(0, 0, 1.2f); // same plane as the HUD
            canvas.transform.localScale = Vector3.one * 0.001f;
            canvas.sortingOrder = 20; // UI doesn't write depth: a higher order draws over the nearer screen panels
            _root = canvas.gameObject;
            var panel = Ui.Box("Panel", canvas.transform, new Color(0, 0, 0, 0.6f));
            panel.raycastTarget = false;
            panel.rectTransform.anchorMin = new Vector2(0, 0);
            panel.rectTransform.anchorMax = new Vector2(0.47f, 1);
            panel.rectTransform.offsetMin = new Vector2(0, 0);
            panel.rectTransform.offsetMax = new Vector2(0, -125); // below the HUD's focus bar
            _text = Ui.Label(panel.transform, string.Empty, 12, new Color(0.7f, 1f, 0.7f), TextAnchor.UpperLeft);
            _text.verticalOverflow = VerticalWrapMode.Truncate;
            Ui.Stretch(_text.rectTransform, 6);

            Application.logMessageReceivedThreaded += OnLog;
            _root.SetActive(false);
        }

        public void Dispose()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            Count(null);
        }

        /// <summary>Every frame: frame timing (cheap, no allocations).</summary>
        public void Tick()
        {
            _frames++;
            _worstFrame = Mathf.Max(_worstFrame, Time.unscaledDeltaTime);
            var now = Time.unscaledTime;
            if (now - _fpsSince >= 1f)
            {
                _fps = _frames / (now - _fpsSince);
                _shownWorstFrame = _worstFrame;
                _frames = 0;
                _worstFrame = 0;
                _fpsSince = now;
                _sampleRate = Interlocked.Exchange(ref _samples, 0) / Mathf.Max(0.001f, now - _rateSince);
                _rateSince = now;
            }
        }

        /// <summary>At 4 Hz: rebuilds the text (only while dev mode is on).</summary>
        public void Refresh()
        {
            while (_incoming.TryDequeue(out var line))
            {
                _log.Enqueue(line);
                while (_log.Count > LogLines)
                {
                    _log.Dequeue();
                }
            }

            var show = DevMode.Enabled;
            _root.SetActive(show);
            Count(show ? _app.Connection?.Source : null);
            if (!show)
            {
                return;
            }

            _sb.Clear();
            var palace = _app.Palace;
            _sb.Append($"[DEV] {_fps:F0} FPS (najgorsza klatka {_shownWorstFrame * 1000:F0} ms)  faza {_app.Phase}");
            if (palace != null)
            {
                _sb.Append($" / {palace.Mode}");
            }

            _sb.Append('\n');
            AppendEeg();
            AppendRules();
            Line(_placer.DevText);
            Line(_input.DevText);
            _sb.Append("Log:\n");
            foreach (var line in _log)
            {
                _sb.Append(line).Append('\n');
            }

            _text.text = _sb.ToString();
        }

        private void AppendEeg()
        {
            var connection = _app.Connection;
            if (connection == null)
            {
                _sb.Append("EEG: nie utworzono źródła\n");
                return;
            }

            var source = connection.Source;
            _sb.Append($"EEG: {source.GetType().Name}  stan {connection.State} (źródło {source.State})  " +
                       $"{_sampleRate:F0} próbek/s (nominalnie {source.SamplingRateHz})\n");
            if (connection.LastError != null)
            {
                _sb.Append($"  błąd: {connection.LastError.Kind}: {connection.LastError.Message}\n");
            }

            var worker = _app.Worker;
            var frame = _app.LastFrame;
            _sb.Append($"  ramki {_app.FrameCount}, utracone próbki {worker?.DroppedSamples ?? 0}, " +
                       $"skalibrowano {(worker != null && worker.IsCalibrated ? "tak" : "nie")}\n");
            if (_app.FrameCount != _lastFrameCount)
            {
                // ponytail: samples only the newest frame per 4 Hz refresh; frames come at 1 Hz, so none are missed.
                _lastFrameCount = _app.FrameCount;
                _rejected.Enqueue(frame.Artifact != Core.Signal.ArtifactKind.None);
                while (_rejected.Count > 60)
                {
                    _rejected.Dequeue();
                }
            }

            if (_app.FrameCount > 0)
            {
                var bad = 0;
                foreach (var r in _rejected)
                {
                    bad += r ? 1 : 0;
                }

                _sb.Append($"  odrzucone okna (ruch, mruganie): {bad} z ostatnich {_rejected.Count} s\n");
                var b = frame.Bands;
                _sb.Append($"  artefakt {frame.Artifact}  β/(θ+α) {frame.Ratio:F3}  skupienie {Fmt(frame.Focus)} (pokazane {Fmt(_app.Focus)})\n");
                _sb.Append($"  δ {b.Delta:F1} θ {b.Theta:F1} α {b.Alpha:F1} β {b.Beta:F1} γ {b.Gamma:F1} µV²\n");
                _sb.Append("  kontakt ").Append(SetupScreen.ElectrodeText(_app.ChannelNames, frame.ContactOk, true)).Append('\n');
            }

            var calibration = worker?.LastCalibration;
            if (calibration != null)
            {
                _sb.Append($"  kalibracja: {calibration.Outcome}\n");
            }
        }

        private void AppendRules()
        {
            var game = _app.Game;
            if (_app.Phase == AppPhase.Calibration && game != null)
            {
                _sb.Append($"Gra: {(game.IsGreen ? "ZIELONY" : "czekaj")}  trafienia {game.Hits}/{game.Targets}, pudła {game.Misses}, " +
                           $"postęp {_app.CalibrationProgress:P0}\n");
            }

            var rules = _app.Rules;
            if (rules != null)
            {
                _sb.Append($"Reguły: poziom {rules.Classify(_app.Focus)}, tempo+ {(rules.SpeedUp ? "tak" : "nie")}, " +
                           $"przerwa {(rules.OnBreak ? "trwa" : rules.BreakPromptOpen ? "pytanie" : "nie")}, " +
                           $"pauza {(rules.IsPaused ? "tak" : "nie")}, aktywny czas {rules.ActiveSeconds:F0} s\n");
            }
        }

        private void Line(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                _sb.Append(text).Append('\n');
            }
        }

        private static string Fmt(double? focus) => focus.HasValue ? $"{focus.Value:F0}%" : "—";

        /// <summary>Counts samples arriving from the current source (SDK thread: Interlocked only).</summary>
        private void Count(IEegSource source)
        {
            if (source == _countedSource)
            {
                return;
            }

            if (_countedSource != null)
            {
                _countedSource.SamplesReceived -= OnSamples;
            }

            _countedSource = source;
            if (source != null)
            {
                source.SamplesReceived += OnSamples;
            }
        }

        private void OnSamples(IReadOnlyList<EegSample> batch) => Interlocked.Add(ref _samples, batch.Count);

        /// <summary>Any thread. Warnings and errors always; plain logs only our own.</summary>
        private void OnLog(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Log && !message.StartsWith("NeuroScholAR", StringComparison.Ordinal))
            {
                return;
            }

            var first = message.Split('\n')[0];
            if (first.Length > 160)
            {
                first = first.Substring(0, 160) + "…";
            }

            var tag = type == LogType.Log ? "i" : type == LogType.Warning ? "W" : "E";
            _incoming.Enqueue($"{DateTime.Now:HH:mm:ss} {tag} {first}");
            while (_incoming.Count > LogLines * 4)
            {
                _incoming.TryDequeue(out _);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeuroScholAR.Core.Adaptive;
using NeuroScholAR.Core.Eeg;
using NeuroScholAR.Core.Focus;
using NeuroScholAR.Core.Learning;
using NeuroScholAR.Core.Signal;

namespace NeuroScholAR.Core.Session
{
    public enum AppPhase
    {
        Setup,
        Calibration,
        Session,
        Summary,
    }

    /// <summary>One-shot things the UI should react to (alerts, prompts, toasts). Drained with <see cref="SessionController.TryTakeNotice"/>.</summary>
    public enum Notice
    {
        ConnectionLost,
        Reconnected,
        CalibrationFailed,
        SuggestBreak,
        BreakEnded,
        SpeedUpOn,
        SpeedUpOff,
        SuggestEndSession,
        AllViewed,
        TestStarted,
        SessionFinished,
    }

    /// <summary>
    /// The whole app flow minus rendering: setup → (calibration) → palace session → summary. Owns the EEG
    /// connection, the palace game, the adaptive rules and the recording. Unity calls <see cref="Tick"/> every
    /// frame and renders the public state; the scripted end-to-end test drives the very same object.
    /// Everything here runs on the main thread; only the EEG worker runs elsewhere.
    /// </summary>
    public sealed class SessionController : IDisposable
    {
        private const double FocusStaleS = 3;

        private readonly IClock _clock;
        private readonly Func<IEegSource> _eegFactory;
        private readonly AdaptiveConfig _adaptiveConfig;
        private readonly EegPipelineConfig _pipelineConfig;
        private readonly FocusConfig _focusConfig;
        private readonly bool _threadedEeg;
        private readonly int _seed;
        private readonly Func<TimeSpan, CancellationToken, Task> _reconnectDelay;
        private readonly Queue<Notice> _notices = new Queue<Notice>();
        private readonly List<AdaptiveEvent> _events = new List<AdaptiveEvent>();
        private readonly List<FocusPoint> _timeline = new List<FocusPoint>();

        private EegConnectionState _lastConnectionState;
        private double _focusAt = double.NegativeInfinity;
        private double _sessionStart;
        private DateTimeOffset _startedAtUtc;
        private double _lastRulesTick = double.NegativeInfinity;
        private double _questionStart;
        private double _nextQuestionAt;
        private double _testAutoStartAt = double.PositiveInfinity;
        private bool _pausedForBreak;
        private bool _eegUsed;

        public SessionController(IClock clock, Func<IEegSource> eegFactory, AdaptiveConfig adaptiveConfig = null,
            EegPipelineConfig pipelineConfig = null, FocusConfig focusConfig = null, bool threadedEeg = true, int seed = 0,
            Func<TimeSpan, CancellationToken, Task> reconnectDelay = null)
        {
            _reconnectDelay = reconnectDelay;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _eegFactory = eegFactory;
            _adaptiveConfig = adaptiveConfig ?? new AdaptiveConfig();
            _pipelineConfig = pipelineConfig ?? new EegPipelineConfig();
            _focusConfig = focusConfig ?? new FocusConfig();
            _threadedEeg = threadedEeg;
            _seed = seed;
        }

        public AppPhase Phase { get; private set; } = AppPhase.Setup;
        public AdaptiveConfig AdaptiveConfig => _adaptiveConfig;

        // ---------- Setup ----------

        public ImportResult Material { get; private set; }
        public EegConnectionManager Connection { get; private set; }
        public EegProcessingWorker Worker { get; private set; }

        /// <summary>Per-electrode contact from the latest analysed window (null until the first window).</summary>
        public bool[] ContactOk { get; private set; }

        public IReadOnlyList<string> ChannelNames => Connection?.Source.ChannelNames;
        public bool EegStreaming => Connection != null && Connection.IsStreaming;
        public ConnectionLight Light => Connection == null ? ConnectionLight.Red : EegConnectionManager.Light(Connection.State, ContactOk);

        /// <summary>Step 1 of 3: material (predefined, imported or typed).</summary>
        public void SelectMaterial(ImportResult material)
        {
            Material = material ?? throw new ArgumentNullException(nameof(material));
        }

        /// <summary>Step 2 of 3 (optional): "Połącz opaskę EEG". False → <see cref="EegConnectionManager.LastError"/> says why.</summary>
        public async Task<bool> ConnectEegAsync(CancellationToken ct = default)
        {
            if (_eegFactory == null)
            {
                return false;
            }

            if (Connection == null)
            {
                var source = _eegFactory();
                Connection = new EegConnectionManager(source, _reconnectDelay);
                Worker = new EegProcessingWorker(source, _pipelineConfig, _focusConfig);
                if (_threadedEeg)
                {
                    Worker.Start();
                }
            }

            var ok = await Connection.ConnectAsync(ct).ConfigureAwait(true);
            _lastConnectionState = Connection.State;
            return ok;
        }

        public void DisconnectEeg()
        {
            Connection?.Disconnect();
            ContactOk = null;
        }

        /// <summary>Drops the EEG source entirely (e.g. switching between simulator and headband); the next connect creates a new one.</summary>
        public void ReleaseEeg()
        {
            DisconnectEeg();
            Worker?.Dispose();
            Connection?.Dispose();
            Connection?.Source.Dispose();
            Worker = null;
            Connection = null;
            Focus = null;
        }

        /// <summary>Step 3 of 3: "Rozpocznij naukę". Goes through calibration first when EEG is on and uncalibrated.</summary>
        /// <exception cref="InvalidOperationException">No material selected (the button is disabled in that case).</exception>
        public void Start()
        {
            if (Material == null)
            {
                throw new InvalidOperationException("Wybierz materiał przed rozpoczęciem.");
            }

            if (EegStreaming && !Worker.IsCalibrated)
            {
                StartCalibration();
            }
            else
            {
                BeginSession();
            }
        }

        // ---------- Calibration ----------

        public CalibrationGame Game { get; private set; }
        public double CalibrationProgress { get; private set; }
        public CalibrationResult CalibrationResult { get; private set; }

        public void StartCalibration()
        {
            Phase = AppPhase.Calibration;
            CalibrationProgress = 0;
            CalibrationResult = null;
            Game = new CalibrationGame(_clock.NowSeconds, _seed);
            Worker.StartCalibration();
        }

        /// <summary>User pressed "Przerwij" during calibration, or the headband dropped.</summary>
        public void AbortCalibration()
        {
            if (Phase != AppPhase.Calibration)
            {
                return;
            }

            Worker?.AbortCalibration();
            CalibrationResult = Worker?.LastCalibration;
            Phase = AppPhase.Setup;
        }

        /// <summary>After a failed/aborted calibration: learn anyway, focus features off.</summary>
        public void ContinueWithoutEeg()
        {
            DisconnectEeg();
            BeginSession();
        }

        // ---------- Session ----------

        public PalaceSession Palace { get; private set; }
        public AdaptiveRuleEngine Rules { get; private set; }

        /// <summary>Latest smoothed focus 0–100, or null (no EEG, not calibrated, lost link, stale, artifact).</summary>
        public double? Focus { get; private set; }

        /// <summary>Latest processed EEG frame, unfiltered (dev overlay). Default until the first one arrives.</summary>
        public FocusFrame LastFrame { get; private set; }

        /// <summary>Processed EEG frames so far (dev overlay: should grow by 1 per second while streaming).</summary>
        public long FrameCount { get; private set; }

        public FocusLevel FocusLevel => Rules?.Classify(Focus) ?? FocusLevel.Unknown;
        public bool EegEnabledForSession => _eegUsed;

        public double SessionSeconds => Phase == AppPhase.Session ? _clock.NowSeconds - _sessionStart : 0;
        public bool IsPaused => Palace != null && Palace.IsPaused;
        public bool SpeedUp => Rules != null && Rules.SpeedUp;

        /// <summary>Seconds left to find the object before the question counts as wrong (Test / Find only).</summary>
        public double AnswerTimeLeft =>
            Palace != null && Palace.Step == TestStep.Find
                ? Math.Max(0, _adaptiveConfig.AnswerTime(SpeedUp) - (_clock.NowSeconds - _questionStart))
                : 0;

        /// <summary>Seconds until the automatic switch to Test once everything was viewed, or null.</summary>
        public double? TestStartsIn =>
            Palace != null && Palace.Mode == PalaceMode.Learning && !double.IsPositiveInfinity(_testAutoStartAt)
                ? Math.Max(0, _testAutoStartAt - _clock.NowSeconds)
                : (double?)null;

        /// <summary>All objects are placed in the room (Preparation → Learning).</summary>
        public void FinishPlacement()
        {
            if (Phase == AppPhase.Session && Palace.Mode == PalaceMode.Preparation)
            {
                Palace.StartLearning();
            }
        }

        /// <summary>The user looked at object <paramref name="index"/> long enough to read it (Learning only).</summary>
        public void ViewObject(int index)
        {
            if (Phase != AppPhase.Session || Palace.IsPaused || Palace.Mode != PalaceMode.Learning || Palace.WasViewed(index))
            {
                return;
            }

            Palace.View(index);
            if (Palace.AllViewed)
            {
                _testAutoStartAt = _clock.NowSeconds + _adaptiveConfig.AutoTestDelayS;
                _notices.Enqueue(Notice.AllViewed);
            }
        }

        /// <summary>Pinch, poke or select button on palace object <paramref name="index"/>.</summary>
        public void SelectObject(int index)
        {
            if (Phase != AppPhase.Session || Palace.IsPaused)
            {
                return;
            }

            if (Palace.Mode == PalaceMode.Learning)
            {
                ViewObject(index);
            }
            else if (Palace.Mode == PalaceMode.Test && Palace.Step == TestStep.Find)
            {
                Palace.Choose(index, CurrentFocus());
                _nextQuestionAt = _clock.NowSeconds + _adaptiveConfig.Transition(SpeedUp);
            }
        }

        /// <summary>"Nie pamiętam" during a question.</summary>
        public void GiveUp()
        {
            if (Phase == AppPhase.Session && !Palace.IsPaused && Palace.Step == TestStep.Find)
            {
                Palace.GiveUp(CurrentFocus());
                _nextQuestionAt = _clock.NowSeconds + _adaptiveConfig.Transition(SpeedUp);
            }
        }

        /// <summary>"Zacznij test" (also automatic after everything was viewed).</summary>
        public void StartTest()
        {
            if (Phase == AppPhase.Session && Palace.Mode == PalaceMode.Learning)
            {
                _testAutoStartAt = double.PositiveInfinity;
                Palace.StartTest();
                OnTestStarted();
            }
        }

        public void Pause()
        {
            if (Phase == AppPhase.Session)
            {
                Palace.Pause();
                Rules.Pause();
            }
        }

        public void Resume()
        {
            if (Phase == AppPhase.Session && !Rules.OnBreak)
            {
                Palace.Resume();
                Rules.Resume();
                RestartTestTimers();
            }
        }

        /// <summary>Answer to "Może czas na krótką przerwę?".</summary>
        public void AnswerBreakPrompt(bool takeBreak)
        {
            Rules.AnswerBreakPrompt(takeBreak);
            if (takeBreak)
            {
                Palace.Pause();
                _pausedForBreak = true;
            }
        }

        public double BreakTimeLeft => Rules != null && Rules.OnBreak ? Math.Max(0, Rules.BreakEndsAt - _clock.NowSeconds) : 0;

        /// <summary>"Wróć do nauki" during a break.</summary>
        public void EndBreak()
        {
            if (Rules != null && Rules.OnBreak)
            {
                Rules.EndBreak();
                FinishBreak();
            }
        }

        /// <summary>"Zakończ", or accepting the 25-minute suggestion.</summary>
        public void EndSession()
        {
            if (Phase != AppPhase.Session)
            {
                return;
            }

            Palace.Finish();
            BuildSummary();
            Phase = AppPhase.Summary;
            _notices.Enqueue(Notice.SessionFinished);
        }

        // ---------- Summary ----------

        public SessionSummary Summary { get; private set; }
        public SummaryAnalytics Analytics { get; private set; }

        /// <summary>"Rozpocznij nową sesję": keeps the material, the headband and its calibration.</summary>
        public void NewSession()
        {
            Palace = null;
            Rules = null;
            Summary = null;
            Analytics = null;
            Phase = AppPhase.Setup;
        }

        // ---------- Loop ----------

        public bool TryTakeNotice(out Notice notice)
        {
            if (_notices.Count > 0)
            {
                notice = _notices.Dequeue();
                return true;
            }

            notice = default;
            return false;
        }

        /// <summary>Call every frame (main thread). Cheap when nothing changed.</summary>
        public void Tick()
        {
            var now = _clock.NowSeconds;
            PollConnection();
            DrainFrames(now);

            if (Focus.HasValue && now - _focusAt > FocusStaleS)
            {
                Focus = null;
            }

            switch (Phase)
            {
                case AppPhase.Calibration:
                    TickCalibration(now);
                    break;
                case AppPhase.Session:
                    TickSession(now);
                    break;
            }
        }

        public void Dispose()
        {
            Worker?.Dispose();
            Connection?.Dispose();
            Connection?.Source.Dispose();
        }

        private void PollConnection()
        {
            if (Connection == null)
            {
                return;
            }

            if (Connection.TakeConnectionLostAlert())
            {
                Focus = null;
                ContactOk = null;
                _notices.Enqueue(Notice.ConnectionLost);
                if (Phase == AppPhase.Calibration)
                {
                    AbortCalibration();
                    _notices.Enqueue(Notice.CalibrationFailed);
                }
            }

            var state = Connection.State;
            if (state == EegConnectionState.Connected && _lastConnectionState == EegConnectionState.Reconnecting)
            {
                _notices.Enqueue(Notice.Reconnected);
            }

            _lastConnectionState = state;
        }

        private void DrainFrames(double now)
        {
            if (Worker == null)
            {
                return;
            }

            while (Worker.TryGetFrame(out var frame))
            {
                LastFrame = frame;
                FrameCount++;
                ContactOk = frame.ContactOk;
                if (frame.Calibrating)
                {
                    CalibrationProgress = frame.CalibrationProgress;
                }

                if (!frame.Focus.HasValue || !EegStreaming)
                {
                    continue;
                }

                Focus = frame.Focus;
                _focusAt = now;
                if (Phase == AppPhase.Session && !Palace.IsPaused)
                {
                    _timeline.Add(new FocusPoint { TS = (int)Math.Round(now - _sessionStart), Focus = (int)Math.Round(frame.Focus.Value) });
                }
            }
        }

        private void TickCalibration(double now)
        {
            Game.Tick(now);
            var result = Worker.LastCalibration;
            if (result == null)
            {
                return;
            }

            CalibrationResult = result;
            CalibrationProgress = 1;
            if (result.Succeeded)
            {
                BeginSession();
            }
            else
            {
                Phase = AppPhase.Setup;
                _notices.Enqueue(Notice.CalibrationFailed);
            }
        }

        private void BeginSession()
        {
            Palace = new PalaceSession(Material.Set, _clock, _seed);
            Rules = new AdaptiveRuleEngine(_adaptiveConfig, _clock);
            _eegUsed = EegStreaming;
            _timeline.Clear();
            _sessionStart = _clock.NowSeconds;
            _startedAtUtc = DateTimeOffset.UtcNow;
            _lastRulesTick = double.NegativeInfinity;
            _pausedForBreak = false;
            _testAutoStartAt = double.PositiveInfinity;
            Phase = AppPhase.Session; // Palace stays in Preparation until the app reports FinishPlacement
        }

        private void TickSession(double now)
        {
            if (now - _lastRulesTick >= 1)
            {
                _lastRulesTick = now;
                _events.Clear();
                Rules.Update(Focus, _events);
                foreach (var e in _events)
                {
                    _notices.Enqueue(ToNotice(e));
                }
            }

            if (_pausedForBreak && !Rules.OnBreak)
            {
                FinishBreak();
            }

            if (Palace.IsPaused)
            {
                return;
            }

            if (Palace.Mode == PalaceMode.Learning && now >= _testAutoStartAt)
            {
                StartTest();
            }

            if (Palace.Mode != PalaceMode.Test)
            {
                return;
            }

            if (Palace.Step != TestStep.Find && now >= _nextQuestionAt)
            {
                if (Palace.NextQuestion(SpeedUp))
                {
                    _questionStart = now;
                }
                else
                {
                    EndSession();
                }
            }
            else if (Palace.Step == TestStep.Find && AnswerTimeLeft <= 0)
            {
                GiveUp();
            }
        }

        private double? CurrentFocus() => Focus.HasValue ? Math.Round(Focus.Value) : (double?)null;

        private void FinishBreak()
        {
            _pausedForBreak = false;
            Palace.Resume();
            RestartTestTimers();
            _notices.Enqueue(Notice.BreakEnded);
        }

        private void RestartTestTimers()
        {
            _questionStart = _clock.NowSeconds;
            _nextQuestionAt = _clock.NowSeconds + _adaptiveConfig.Transition(SpeedUp);
        }

        private void OnTestStarted()
        {
            _nextQuestionAt = _clock.NowSeconds + _adaptiveConfig.Transition(SpeedUp);
            _notices.Enqueue(Notice.TestStarted);
        }

        private void BuildSummary()
        {
            var answers = Palace.Answers;
            Summary = new SessionSummary
            {
                SessionId = Guid.NewGuid().ToString(),
                StartedAt = _startedAtUtc,
                DurationS = (int)Math.Round(_clock.NowSeconds - _sessionStart),
                EegEnabled = _eegUsed,
                LearnTimeS = (int)Math.Round(Palace.LearnTimeS),
                TestTimeS = (int)Math.Round(Palace.TestTimeS),
                AccuracyPct = answers.Count == 0 ? 0 : Math.Round(100.0 * answers.Count(a => a.Correct) / answers.Count, 1),
                FocusTimeline = new List<FocusPoint>(_timeline),
                Answers = answers.Select(a => new AnswerPoint
                {
                    PairId = a.PairId,
                    Correct = a.Correct,
                    FocusAtAnswer = a.FocusAtAnswer.HasValue ? (int?)Math.Round(a.FocusAtAnswer.Value) : null,
                }).ToList(),
            };
            Analytics = SummaryAnalytics.From(Summary, Palace.Objects.ToDictionary(p => p.Id, p => p.Prompt));
        }

        private static Notice ToNotice(AdaptiveEvent e)
        {
            switch (e)
            {
                case AdaptiveEvent.SuggestBreak:
                    return Notice.SuggestBreak;
                case AdaptiveEvent.SpeedUpOn:
                    return Notice.SpeedUpOn;
                case AdaptiveEvent.SpeedUpOff:
                    return Notice.SpeedUpOff;
                default:
                    return Notice.SuggestEndSession;
            }
        }
    }
}

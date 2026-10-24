using System.Collections.Generic;

namespace NeuroScholAR.Core.Adaptive
{
    /// <summary>
    /// Every adaptive threshold and timing in one object (spec requirement). Tune here, nowhere else.
    /// The HUD colours use the same low/high thresholds so what the user sees matches what the rules do.
    /// </summary>
    public sealed class AdaptiveConfig
    {
        /// <summary>Focus below this (%) counts as low. HUD: red below, yellow up to <see cref="HighFocusThreshold"/>.</summary>
        public double LowFocusThreshold = 35;

        /// <summary>Continuous low focus for this long triggers "Może czas na krótką przerwę?".</summary>
        public double LowFocusDurationS = 20;

        /// <summary>Focus above this (%) counts as high. HUD: green above.</summary>
        public double HighFocusThreshold = 70;

        /// <summary>Continuous high focus for this long turns the test speed-up on.</summary>
        public double HighFocusDurationS = 10;

        /// <summary>Speed-up turns off after focus stays at or below the high threshold this long (hysteresis, no flapping).</summary>
        public double SpeedUpExitDurationS = 10;

        /// <summary>After "Kontynuuj mimo wszystko", don't ask again for this long.</summary>
        public double BreakPromptCooldownS = 180;

        /// <summary>"5-minutowa przerwa".</summary>
        public double BreakDurationS = 5 * 60;

        /// <summary>Suggest ending the session (and show the summary) after this much active time.</summary>
        public double SessionLimitS = 25 * 60;

        /// <summary>Test answer time: seconds to walk to the right object before it counts as wrong. Normal / sped-up.</summary>
        public double AnswerTimeS = 45;
        public double FastAnswerTimeS = 30;

        /// <summary>Feedback shown between questions (right object highlighted). Normal / sped-up.</summary>
        public double TransitionS = 3;
        public double FastTransitionS = 1.5;

        /// <summary>Learning → Test switches automatically this long after the last object has been viewed.</summary>
        public double AutoTestDelayS = 10;

        public double AnswerTime(bool speedUp) => speedUp ? FastAnswerTimeS : AnswerTimeS;
        public double Transition(bool speedUp) => speedUp ? FastTransitionS : TransitionS;
    }

    public enum AdaptiveEvent
    {
        SuggestBreak,
        SpeedUpOn,
        SpeedUpOff,
        SuggestEndSession,
    }

    public enum FocusLevel
    {
        Unknown,
        Low,
        Medium,
        High,
    }

    /// <summary>
    /// State machine for the adaptive rules. Feed it the current focus (null = no EEG / no clean signal)
    /// roughly once a second; it reports transitions. Missing focus breaks the low/high streaks, so a user
    /// without EEG never gets focus-based prompts, but still gets the session-length suggestion.
    /// </summary>
    public sealed class AdaptiveRuleEngine
    {
        private readonly AdaptiveConfig _config;
        private readonly IClock _clock;
        private double _lowSince = -1;
        private double _highSince = -1;
        private double _notHighSince = -1;
        private double _promptAllowedAt;
        private double _activeS;
        private double _lastTick = -1;
        private bool _endSuggested;

        public AdaptiveRuleEngine(AdaptiveConfig config, IClock clock)
        {
            _config = config ?? new AdaptiveConfig();
            _clock = clock;
        }

        public AdaptiveConfig Config => _config;
        public bool SpeedUp { get; private set; }
        public bool BreakPromptOpen { get; private set; }
        public bool OnBreak => BreakEndsAt > 0;
        public double BreakEndsAt { get; private set; } = -1;
        public bool IsPaused { get; private set; }

        /// <summary>Session time that counts toward the limit (pauses and breaks excluded).</summary>
        public double ActiveSeconds => _activeS;

        public FocusLevel Classify(double? focus) =>
            !focus.HasValue ? FocusLevel.Unknown
            : focus.Value < _config.LowFocusThreshold ? FocusLevel.Low
            : focus.Value > _config.HighFocusThreshold ? FocusLevel.High
            : FocusLevel.Medium;

        /// <summary>Advances the rules. New events are appended to <paramref name="events"/> (caller reuses the list).</summary>
        public void Update(double? focus, List<AdaptiveEvent> events)
        {
            var now = _clock.NowSeconds;
            if (OnBreak && now >= BreakEndsAt)
            {
                BreakEndsAt = -1;
                Resume();
            }

            if (_lastTick >= 0 && !IsPaused)
            {
                _activeS += now - _lastTick;
            }

            _lastTick = now;
            if (IsPaused)
            {
                return;
            }

            if (!_endSuggested && _activeS >= _config.SessionLimitS)
            {
                _endSuggested = true;
                events.Add(AdaptiveEvent.SuggestEndSession);
            }

            UpdateLow(focus, now, events);
            UpdateHigh(focus, now, events);
        }

        /// <summary>User answered the break prompt.</summary>
        public void AnswerBreakPrompt(bool takeBreak)
        {
            if (!BreakPromptOpen)
            {
                return;
            }

            BreakPromptOpen = false;
            _lowSince = -1;
            if (takeBreak)
            {
                Pause();
                BreakEndsAt = _clock.NowSeconds + _config.BreakDurationS;
            }
            else
            {
                _promptAllowedAt = _clock.NowSeconds + _config.BreakPromptCooldownS;
            }
        }

        /// <summary>Ends a break early ("Wróć do nauki").</summary>
        public void EndBreak()
        {
            BreakEndsAt = -1;
            Resume();
        }

        public void Pause()
        {
            if (!IsPaused)
            {
                AccumulateUntilNow();
                IsPaused = true;
            }
        }

        public void Resume()
        {
            if (IsPaused)
            {
                IsPaused = false;
                _lastTick = _clock.NowSeconds;

                // Focus before the pause says nothing about focus after it: start the streaks over.
                _lowSince = _highSince = _notHighSince = -1;
            }
        }

        private void AccumulateUntilNow()
        {
            var now = _clock.NowSeconds;
            if (_lastTick >= 0)
            {
                _activeS += now - _lastTick;
            }

            _lastTick = now;
        }

        private void UpdateLow(double? focus, double now, List<AdaptiveEvent> events)
        {
            if (Classify(focus) != FocusLevel.Low)
            {
                _lowSince = -1;
                return;
            }

            if (_lowSince < 0)
            {
                _lowSince = now;
            }

            if (!BreakPromptOpen && now >= _promptAllowedAt && now - _lowSince >= _config.LowFocusDurationS)
            {
                BreakPromptOpen = true;
                events.Add(AdaptiveEvent.SuggestBreak);
            }
        }

        private void UpdateHigh(double? focus, double now, List<AdaptiveEvent> events)
        {
            if (Classify(focus) == FocusLevel.High)
            {
                _notHighSince = -1;
                if (_highSince < 0)
                {
                    _highSince = now;
                }

                if (!SpeedUp && now - _highSince >= _config.HighFocusDurationS)
                {
                    SpeedUp = true;
                    events.Add(AdaptiveEvent.SpeedUpOn);
                }

                return;
            }

            _highSince = -1;
            if (!SpeedUp)
            {
                return;
            }

            if (_notHighSince < 0)
            {
                _notHighSince = now;
            }

            if (now - _notHighSince >= _config.SpeedUpExitDurationS)
            {
                SpeedUp = false;
                _notHighSince = -1;
                events.Add(AdaptiveEvent.SpeedUpOff);
            }
        }
    }
}

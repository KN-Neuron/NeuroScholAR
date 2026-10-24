using System;
using System.Collections.Generic;
using NeuroScholAR.Core.Adaptive;
using NUnit.Framework;

namespace NeuroScholAR.Core.Tests
{
    public class AdaptiveRuleEngineTests
    {
        private ManualClock _clock;
        private AdaptiveRuleEngine _engine;
        private List<AdaptiveEvent> _events;

        [SetUp]
        public void SetUp()
        {
            _clock = new ManualClock();
            _engine = new AdaptiveRuleEngine(new AdaptiveConfig(), _clock);
            _events = new List<AdaptiveEvent>();
        }

        /// <summary>Feeds <paramref name="focus"/> once per second for <paramref name="seconds"/>; returns all events.</summary>
        private List<AdaptiveEvent> Feed(double? focus, int seconds)
        {
            var all = new List<AdaptiveEvent>();
            for (var i = 0; i < seconds; i++)
            {
                _events.Clear();
                _engine.Update(focus, _events);
                all.AddRange(_events);
                _clock.Advance(1);
            }

            return all;
        }

        [Test]
        public void LowFocus_For20s_SuggestsBreak_NotBefore()
        {
            Assert.That(Feed(30, 20), Is.Empty); // t = 0..19
            Assert.That(Feed(30, 1), Is.EqualTo(new[] { AdaptiveEvent.SuggestBreak })); // t = 20
            Assert.That(_engine.BreakPromptOpen, Is.True);
            Assert.That(Feed(30, 30), Is.Empty, "prompt is already open; never stack prompts");
        }

        [Test]
        public void LowStreak_IsBrokenByOneGoodSecond_OrMissingSignal()
        {
            Feed(30, 15);
            Feed(50, 1);
            Assert.That(Feed(30, 15), Is.Empty);
            Feed(null, 1);
            Assert.That(Feed(30, 20), Is.Empty);
            Assert.That(Feed(30, 1), Is.EqualTo(new[] { AdaptiveEvent.SuggestBreak }));
        }

        [Test]
        public void ExactlyAtThreshold_IsNotLow() => Assert.That(Feed(35, 60), Is.Empty);

        [Test]
        public void Continue_StartsCooldown_ThenCanAskAgain()
        {
            Feed(20, 21);
            _engine.AnswerBreakPrompt(takeBreak: false);
            Assert.That(Feed(20, (int)_engine.Config.BreakPromptCooldownS - 1), Is.Empty);
            Assert.That(Feed(20, 2), Does.Contain(AdaptiveEvent.SuggestBreak));
        }

        [Test]
        public void TakingBreak_PausesRules_ThenResumesAfterBreakDuration()
        {
            Feed(20, 21);
            _engine.AnswerBreakPrompt(takeBreak: true);
            Assert.That(_engine.OnBreak, Is.True);
            var activeBefore = _engine.ActiveSeconds;

            Assert.That(Feed(20, 300), Is.Empty); // break began at t=21, so t=21..320 are still on break
            Assert.That(_engine.ActiveSeconds, Is.EqualTo(activeBefore).Within(1.0), "break time does not count");
            Feed(20, 1);
            Assert.That(_engine.OnBreak, Is.False);
            Assert.That(_engine.IsPaused, Is.False);
        }

        [Test]
        public void HighFocus_For10s_SpeedsUp_ThenSlowsDownWithHysteresis()
        {
            Assert.That(Feed(80, 10), Is.Empty);
            Assert.That(Feed(80, 1), Is.EqualTo(new[] { AdaptiveEvent.SpeedUpOn }));
            Assert.That(_engine.SpeedUp, Is.True);
            Assert.That(_engine.Config.AnswerTime(true), Is.LessThan(_engine.Config.AnswerTime(false)));

            Assert.That(Feed(60, 10), Is.Empty, "dip shorter than exit duration keeps the speed-up");
            Assert.That(Feed(60, 1), Is.EqualTo(new[] { AdaptiveEvent.SpeedUpOff }));
        }

        [Test]
        public void SessionLimit_SuggestsEndOnce_ExcludingPauses()
        {
            Feed(null, 10 * 60);
            _engine.Pause();
            _clock.Advance(60 * 60);
            _engine.Resume();
            Assert.That(Feed(null, 15 * 60 - 10), Is.Empty);
            var events = Feed(null, 30);
            Assert.That(events, Is.EqualTo(new[] { AdaptiveEvent.SuggestEndSession }));
            Assert.That(Feed(null, 600), Is.Empty);
        }

        [TestCase(null, FocusLevel.Unknown)]
        [TestCase(10.0, FocusLevel.Low)]
        [TestCase(35.0, FocusLevel.Medium)]
        [TestCase(70.0, FocusLevel.Medium)]
        [TestCase(70.1, FocusLevel.High)]
        public void Classify_MatchesHudColours(double? focus, FocusLevel level) =>
            Assert.That(_engine.Classify(focus), Is.EqualTo(level));
    }
}

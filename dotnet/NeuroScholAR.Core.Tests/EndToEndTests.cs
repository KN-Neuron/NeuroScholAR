using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NeuroScholAR.Core.Eeg;
using NeuroScholAR.Core.Learning;
using NeuroScholAR.Core.Session;
using NUnit.Framework;

namespace NeuroScholAR.Core.Tests
{
    /// <summary>
    /// Scripted end-to-end run on <see cref="SimulatedEegSource"/>, no hardware: calibration → learning →
    /// test with a low-focus period → headband disconnect and reconnect → summary → export (offline, then retry).
    /// Drives the same <see cref="SessionController"/> the Unity app uses, with a manual clock.
    /// </summary>
    public class EndToEndTests
    {
        private const double Dt = 0.1;

        private ManualClock _clock;
        private SimulatedEegSource _sim;
        private SessionController _app;
        private readonly List<Notice> _notices = new List<Notice>();

        /// <summary>Simulated focus by app time: normal, a low dip during the test, then recovered.</summary>
        private double FocusScript(double _)
        {
            var t = _clock.NowSeconds;
            return t >= 75 && t < 110 ? 0.1 : 0.55;
        }

        [SetUp]
        public void SetUp()
        {
            _clock = new ManualClock();
            _sim = new SimulatedEegSource(new SimulatedEegOptions { RealTime = false, Focus = FocusScript, Seed = 42 });

            // Reconnect backoff measured on the manual clock, so the outage lasts a predictable app time.
            Func<TimeSpan, CancellationToken, Task> clockDelay = async (d, ct) =>
            {
                var until = _clock.NowSeconds + d.TotalSeconds;
                while (_clock.NowSeconds < until)
                {
                    await Task.Delay(1, ct);
                }
            };
            _app = new SessionController(_clock, () => _sim, threadedEeg: false, seed: 3, reconnectDelay: clockDelay);
        }

        [TearDown]
        public void TearDown() => _app.Dispose();

        private void Step(double seconds, Action each = null)
        {
            for (var t = 0.0; t < seconds - 1e-9; t += Dt)
            {
                if (_app.Connection?.State == EegConnectionState.Reconnecting)
                {
                    Thread.Sleep(2); // let the background reconnect loop observe the manual clock
                }

                _clock.Advance(Dt);
                _sim.Advance(Dt);
                _app.Worker?.Pump();
                _app.Tick();
                while (_app.TryTakeNotice(out var n))
                {
                    _notices.Add(n);
                    if (n == Notice.SuggestBreak)
                    {
                        _app.AnswerBreakPrompt(takeBreak: false); // "Kontynuuj mimo wszystko"
                    }
                }

                each?.Invoke();
            }
        }

        private void AnswerLikeAStudent()
        {
            var palace = _app.Palace;
            if (palace.Step == TestStep.Find && _app.AnswerTimeLeft < 40)
            {
                // Walked over and picked an object: 75 % of the time the right one.
                var correct = palace.Answers.Count % 4 != 1;
                _app.SelectObject(correct ? palace.CurrentObject : (palace.CurrentObject + 1) % palace.Objects.Count);
            }
        }

        [Test]
        public async Task FullSession_OnSimulator()
        {
            // Setup: predefined set, connect, start (3 steps).
            _app.SelectMaterial(new ImportResult(PredefinedSets.All[0], new string[0]));
            Assert.That(await _app.ConnectEegAsync(), Is.True);
            _app.Start();
            Assert.That(_app.Phase, Is.EqualTo(AppPhase.Calibration));

            // Calibration mini-game: 30 s baseline (+ window fill), well under the 5-minute budget.
            Step(40, () =>
            {
                if (_app.Phase == AppPhase.Calibration && _app.Game.IsGreen)
                {
                    _app.Game.Press(_clock.NowSeconds);
                }
            });
            Assert.That(_app.CalibrationResult?.Succeeded, Is.True, _app.CalibrationResult?.Outcome.ToString());
            Assert.That(_app.Phase, Is.EqualTo(AppPhase.Session));
            Assert.That(_app.Game.Hits, Is.GreaterThan(0));

            // Placement: the app reports every object is on a surface.
            Assert.That(_app.Palace.Mode, Is.EqualTo(PalaceMode.Preparation));
            Step(5);
            _app.FinishPlacement();
            Assert.That(_app.Palace.Mode, Is.EqualTo(PalaceMode.Learning));

            // Learning: walk around and look at every object. Test starts on its own shortly after the last one.
            for (var i = 0; i < _app.Palace.Objects.Count; i++)
            {
                Step(2);
                _app.ViewObject(i);
            }

            Step(Dt);
            Assert.That(_notices, Does.Contain(Notice.AllViewed));
            Assert.That(_app.TestStartsIn, Is.Not.Null);
            Step(_app.AdaptiveConfig.AutoTestDelayS + Dt);
            Assert.That(_app.Palace.Mode, Is.EqualTo(PalaceMode.Test));
            Assert.That(_notices, Does.Contain(Notice.TestStarted));
            Assert.That(_app.Focus, Is.Not.Null);

            // Test with a low-focus dip (t = 75..110 s): expect the break prompt.
            Step(115 - _clock.NowSeconds, AnswerLikeAStudent);
            Assert.That(_notices, Does.Contain(Notice.SuggestBreak));

            // Headband drops; the next 2 reconnect attempts fail; the session carries on without EEG.
            _sim.SimulateLinkLoss(failedReconnects: 2);
            Step(1, AnswerLikeAStudent);
            Assert.That(_notices, Does.Contain(Notice.ConnectionLost));
            Assert.That(_app.Focus, Is.Null);
            Assert.That(_app.Light, Is.EqualTo(ConnectionLight.Yellow), "reconnecting");
            var answersBefore = _app.Palace.Answers.Count;

            Step(25, AnswerLikeAStudent); // backoff 1 + 2 + 4 s, then a fresh 2.5 s window
            Assert.That(_notices, Does.Contain(Notice.Reconnected));
            Assert.That(_app.Palace.Answers.Count, Is.GreaterThan(answersBefore), "learning continued during the outage");
            Assert.That(_app.Focus, Is.Not.Null, "focus is back without recalibrating");

            // Summary.
            _app.EndSession();
            Assert.That(_app.Phase, Is.EqualTo(AppPhase.Summary));
            var summary = _app.Summary;
            Assert.That(summary.EegEnabled, Is.True);
            Assert.That(summary.DurationS, Is.InRange(100, (int)_clock.NowSeconds - 30), "calibration time is not session time");
            Assert.That(summary.LearnTimeS + summary.TestTimeS, Is.EqualTo(summary.DurationS).Within(2));
            Assert.That(summary.Answers, Is.Not.Empty);
            Assert.That(summary.Answers.Any(a => a.FocusAtAnswer == null), Is.True, "answers during the outage have no focus");
            Assert.That(summary.Answers.Any(a => a.FocusAtAnswer != null), Is.True);
            Assert.That(summary.FocusTimeline.Min(p => p.Focus), Is.LessThan(35), "the low-focus dip is in the timeline");
            Assert.That(summary.FocusTimeline.All(p => p.Focus >= 0 && p.Focus <= 100));
            var gap = summary.FocusTimeline.Zip(summary.FocusTimeline.Skip(1), (a, b) => b.TS - a.TS).Max();
            Assert.That(gap, Is.GreaterThanOrEqualTo(5), "the disconnect leaves a hole in the timeline");

            var analytics = _app.Analytics;
            Assert.That(analytics.AverageFocus, Is.Not.Null);
            Assert.That(analytics.LowestFocus.Focus, Is.LessThan(analytics.HighestFocus.Focus));
            Assert.That(analytics.HardestTerms, Is.Not.Empty);
            Assert.That(analytics.CorrelationText, Is.Not.Empty);
            Assert.That(analytics.AccuracyPct, Is.EqualTo(summary.AccuracyPct));

            // Export: backend down → kept in outbox; backend back → retry delivers.
            var dir = Path.Combine(Path.GetTempPath(), "nsar-e2e-" + Guid.NewGuid());
            var backend = new FakeBackend { Online = false };
            using (var exporter = new SummaryExporter(dir, "http://backend.test", backend))
            {
                Assert.That((await exporter.ExportAsync(summary)).Status, Is.EqualTo(ExportStatus.QueuedForRetry));
                Assert.That(exporter.PendingCount, Is.EqualTo(1));
                backend.Online = true;
                Assert.That(await exporter.RetryPendingAsync(), Is.EqualTo(1));
                Assert.That(exporter.PendingCount, Is.EqualTo(0));
                Assert.That(backend.Received.Single(), Does.Contain(summary.SessionId));
            }

            Directory.Delete(dir, true);
        }
    }

    public class SessionControllerTests
    {
        private static ImportResult Bio => new ImportResult(PredefinedSets.All[0], new string[0]);

        [Test]
        public void UserWhoNeverConnectsEeg_StillGetsTheFullFlow()
        {
            var clock = new ManualClock();
            using var app = new SessionController(clock, () => new SimulatedEegSource(new SimulatedEegOptions { RealTime = false }));
            Assert.Throws<InvalidOperationException>(app.Start, "no material yet");
            app.SelectMaterial(Bio);
            app.Start();
            Assert.That(app.Phase, Is.EqualTo(AppPhase.Session));
            Assert.That(app.Light, Is.EqualTo(ConnectionLight.Red));

            app.FinishPlacement();
            app.StartTest();
            var notices = new List<Notice>();
            for (var t = 0; t < 26 * 60 * 10 && app.Phase == AppPhase.Session; t++)
            {
                clock.Advance(0.1);
                app.Tick();
                while (app.TryTakeNotice(out var n))
                {
                    notices.Add(n);
                }

                if (app.Palace?.Step == TestStep.Find)
                {
                    app.SelectObject(app.Palace.CurrentObject);
                }
            }

            Assert.That(notices, Does.Not.Contain(Notice.SuggestBreak));
            Assert.That(app.Phase, Is.EqualTo(AppPhase.Summary), "test ends when everything is mastered");
            Assert.That(app.Summary.EegEnabled, Is.False);
            Assert.That(app.Summary.FocusTimeline, Is.Empty);
            Assert.That(app.Summary.Answers.All(a => a.FocusAtAnswer == null));
            Assert.That(app.Analytics.CorrelationText, Does.Contain("bez EEG"));
        }

        [Test]
        public void SessionLimit_IsSuggested_WithoutEeg()
        {
            var clock = new ManualClock();
            using var app = new SessionController(clock, null);
            app.SelectMaterial(Bio);
            app.Start();
            var suggested = false;
            for (var t = 0; t < 26 * 60 && !suggested; t++)
            {
                clock.Advance(1);
                app.Tick();
                while (app.TryTakeNotice(out var n))
                {
                    suggested |= n == Notice.SuggestEndSession;
                }
            }

            Assert.That(suggested, Is.True);
            Assert.That(clock.NowSeconds, Is.EqualTo(25 * 60).Within(2));
        }

        [Test]
        public async Task BluetoothOff_ReportsError_AndStartSkipsCalibration()
        {
            var sim = new SimulatedEegSource(new SimulatedEegOptions { RealTime = false, FailConnectWith = EegErrorKind.BluetoothUnavailable });
            using var app = new SessionController(new ManualClock(), () => sim, threadedEeg: false);
            Assert.That(await app.ConnectEegAsync(), Is.False);
            Assert.That(app.Connection.LastError.Kind, Is.EqualTo(EegErrorKind.BluetoothUnavailable));
            app.SelectMaterial(Bio);
            app.Start();
            Assert.That(app.Phase, Is.EqualTo(AppPhase.Session));
            Assert.That(app.EegEnabledForSession, Is.False);
        }

        [Test]
        public async Task AbortedCalibration_ReturnsToSetup_ThenCanLearnWithoutEeg()
        {
            var clock = new ManualClock();
            var sim = new SimulatedEegSource(new SimulatedEegOptions { RealTime = false });
            using var app = new SessionController(clock, () => sim, threadedEeg: false);
            app.SelectMaterial(Bio);
            await app.ConnectEegAsync();
            app.Start();
            for (var i = 0; i < 50; i++)
            {
                clock.Advance(0.1);
                sim.Advance(0.1);
                app.Worker.Pump();
                app.Tick();
            }

            app.AbortCalibration();
            Assert.That(app.Phase, Is.EqualTo(AppPhase.Setup));
            Assert.That(app.CalibrationResult.Outcome, Is.EqualTo(Focus.CalibrationOutcome.Aborted));

            app.ContinueWithoutEeg();
            Assert.That(app.Phase, Is.EqualTo(AppPhase.Session));
            Assert.That(app.EegEnabledForSession, Is.False);
        }

        [Test]
        public void BreakTaken_PausesLearningTime_AndEndsByItself()
        {
            var clock = new ManualClock();
            using var app = new SessionController(clock, null);
            app.SelectMaterial(Bio);
            app.Start();
            app.FinishPlacement();
            clock.Advance(10);
            app.Tick();

            // Force the prompt state as if focus had been low, then take the break.
            app.Rules.Update(10, new List<Adaptive.AdaptiveEvent>());
            clock.Advance(21);
            app.Rules.Update(10, new List<Adaptive.AdaptiveEvent>());
            Assert.That(app.Rules.BreakPromptOpen, Is.True);
            app.AnswerBreakPrompt(takeBreak: true);
            Assert.That(app.IsPaused, Is.True);

            clock.Advance(app.AdaptiveConfig.BreakDurationS + 1);
            app.Tick();
            Assert.That(app.TryTakeNotice(out var n) && n == Notice.BreakEnded, Is.True);
            Assert.That(app.IsPaused, Is.False);
            app.EndSession();
            Assert.That(app.Summary.LearnTimeS, Is.EqualTo(31).Within(1), "break time is not learning time");
        }
    }

    public class ExporterTests
    {
        [Test]
        public async Task NotConfigured_KeepsDataLocally()
        {
            var dir = Path.Combine(Path.GetTempPath(), "nsar-" + Guid.NewGuid());
            using (var exporter = new SummaryExporter(dir, null))
            {
                var result = await exporter.ExportAsync(new SessionSummary { SessionId = "s1" });
                Assert.That(result.Status, Is.EqualTo(ExportStatus.NotConfigured));
                Assert.That(File.Exists(Path.Combine(dir, "results", "s1.json")), Is.True);
            }

            Directory.Delete(dir, true);
        }

        [Test]
        public async Task Online_UploadsAndSavesCsv()
        {
            var dir = Path.Combine(Path.GetTempPath(), "nsar-" + Guid.NewGuid());
            var backend = new FakeBackend { Online = true };
            using (var exporter = new SummaryExporter(dir, "http://backend.test/api", backend))
            {
                var result = await exporter.ExportAsync(new SessionSummary { SessionId = "s2" });
                Assert.That(result.Status, Is.EqualTo(ExportStatus.Exported));
                Assert.That(File.ReadAllText(result.CsvPath), Does.StartWith("session_id"));
                Assert.That(backend.Urls, Has.Some.EqualTo("http://backend.test/api/sessions"));
            }

            Directory.Delete(dir, true);
        }

        /// <summary>Contract test against the real FastAPI service. Run with NSAR_BACKEND_URL=http://127.0.0.1:8000.</summary>
        [Test]
        public async Task LiveBackend_AcceptsOurJson_AndReturnsCsv()
        {
            var url = Environment.GetEnvironmentVariable("NSAR_BACKEND_URL");
            Assume.That(url, Is.Not.Null.And.Not.Empty, "NSAR_BACKEND_URL not set; skipping live backend test");
            var dir = Path.Combine(Path.GetTempPath(), "nsar-live-" + Guid.NewGuid());
            using (var exporter = new SummaryExporter(dir, url))
            {
                var summary = new SessionSummary
                {
                    SessionId = Guid.NewGuid().ToString(), StartedAt = DateTimeOffset.UtcNow, DurationS = 60, EegEnabled = true,
                    LearnTimeS = 20, TestTimeS = 40, AccuracyPct = 50,
                    FocusTimeline = { new FocusPoint { TS = 0, Focus = 40 } },
                    Answers = { new AnswerPoint { PairId = "p1", Correct = true, FocusAtAnswer = 40 }, new AnswerPoint { PairId = "p2", Correct = false } },
                };
                var result = await exporter.ExportAsync(summary);
                Assert.That(result.Status, Is.EqualTo(ExportStatus.Exported), result.Error);
                var lines = File.ReadAllLines(result.CsvPath);
                Assert.That(lines, Has.Length.EqualTo(4));
                Assert.That(lines[3], Does.Contain(",answer,,,p2,false,"));
            }

            Directory.Delete(dir, true);
        }

        [Test]
        public void Summary_RoundTripsSpecJson()
        {
            var json = new SessionSummary
            {
                SessionId = "x", FocusTimeline = { new FocusPoint { TS = 1, Focus = 50 } },
                Answers = { new AnswerPoint { PairId = "p1", Correct = true, FocusAtAnswer = null } },
            }.ToJson();
            foreach (var key in new[] { "session_id", "started_at", "duration_s", "eeg_enabled", "learn_time_s", "test_time_s", "accuracy_pct", "focus_timeline", "t_s", "answers", "pair_id", "focus_at_answer" })
            {
                Assert.That(json, Does.Contain($"\"{key}\""));
            }

            Assert.That(SessionSummary.FromJson(json).Answers[0].FocusAtAnswer, Is.Null);
        }

        [Test]
        public void Correlation_IsPositiveWhenFocusPredictsCorrectness_AndNullWhenUndefined()
        {
            Assert.That(SummaryAnalytics.PointBiserial(new[] { 80.0, 75, 30, 20 }, new[] { true, true, false, false }), Is.GreaterThan(0.9));
            Assert.That(SummaryAnalytics.PointBiserial(new[] { 80.0, 75 }, new[] { true, false }), Is.Null);
            Assert.That(SummaryAnalytics.PointBiserial(new[] { 80.0, 75, 30 }, new[] { true, true, true }), Is.Null);
        }
    }

    /// <summary>Stands in for the FastAPI backend: can be switched off to test the outbox.</summary>
    internal sealed class FakeBackend : HttpMessageHandler
    {
        public bool Online;
        public readonly List<string> Received = new List<string>();
        public readonly List<string> Urls = new List<string>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Online)
            {
                throw new HttpRequestException("Connection refused");
            }

            Urls.Add(request.RequestUri.ToString());
            if (request.Method == HttpMethod.Post)
            {
                Received.Add(await request.Content.ReadAsStringAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("session_id,duration_s\nx,1\n") };
        }
    }
}

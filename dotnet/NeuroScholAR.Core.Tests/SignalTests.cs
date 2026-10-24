using System;
using System.Linq;
using NeuroScholAR.Core.Eeg;
using NeuroScholAR.Core.Focus;
using NeuroScholAR.Core.Signal;
using NUnit.Framework;

namespace NeuroScholAR.Core.Tests
{
    public class BandPowerTests
    {
        private const int Fs = 250;

        private static float[] Sine(double hz, double amplitude, int n = 512) =>
            Enumerable.Range(0, n).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Fs))).ToArray();

        [TestCase(2.5, "Delta")]
        [TestCase(6, "Theta")]
        [TestCase(10, "Alpha")]
        [TestCase(20, "Beta")]
        [TestCase(38, "Gamma")]
        public void Sine_PowerLandsInItsBand_AndEqualsHalfAmplitudeSquared(double hz, string band)
        {
            var welch = new WelchBandPower(256, Fs);
            var p = welch.Compute(Sine(hz, 10), 512);
            var bands = new[] { ("Delta", p.Delta), ("Theta", p.Theta), ("Alpha", p.Alpha), ("Beta", p.Beta), ("Gamma", p.Gamma) };

            var (name, power) = bands.OrderByDescending(b => b.Item2).First();
            Assert.That(name, Is.EqualTo(band));
            Assert.That(power, Is.EqualTo(50).Within(10).Percent); // A²/2 = 100/2
        }

        [Test]
        public void FocusRatio_IsBetaOverThetaPlusAlpha()
        {
            var welch = new WelchBandPower(256, Fs);
            var mixed = Sine(20, 10).Zip(Sine(10, 10), (a, b) => a + b).ToArray();
            Assert.That(welch.Compute(mixed, 512).FocusRatio, Is.EqualTo(1).Within(0.15));
        }

        [Test]
        public void FlatSignal_RatioIsNaN_NotInfinity()
        {
            var welch = new WelchBandPower(256, Fs);
            Assert.That(welch.Compute(new float[512], 512).FocusRatio, Is.NaN);
        }

        [Test]
        public void Fft_RejectsNonPowerOfTwo() => Assert.Throws<ArgumentException>(() => new Fft(250));

        [Test]
        public void RingBuffer_KeepsNewestOldestFirst()
        {
            var ring = new RingBuffer(3);
            foreach (var v in new[] { 1f, 2f, 3f, 4f, 5f })
            {
                ring.Push(v);
            }

            var copy = new float[3];
            ring.CopyTo(copy);
            Assert.That(copy, Is.EqualTo(new[] { 3f, 4f, 5f }));
        }
    }

    public class FocusNormalizationTests
    {
        private readonly FocusConfig _config = new FocusConfig();

        [TestCase(1.0, 50)]
        [TestCase(2.0, 75)]
        [TestCase(0.5, 25)]
        [TestCase(4.0, 100)]
        [TestCase(1000.0, 100)]
        [TestCase(0.0, 0)]
        [TestCase(-1.0, 0)]
        [TestCase(double.PositiveInfinity, 100)]
        public void Normalize_MapsRatioToClampedPercent(double ratio, double expected)
        {
            var normalizer = new FocusNormalizer(1.0, _config);
            Assert.That(normalizer.Normalize(ratio), Is.EqualTo(expected).Within(1e-9));
        }

        [Test]
        public void Normalize_NaN_IsNull() => Assert.That(new FocusNormalizer(1, _config).Normalize(double.NaN), Is.Null);

        [TestCase(0.0)]
        [TestCase(1e-12)]
        [TestCase(-3.0)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void ZeroOrInvalidBaseline_IsRejected(double baseline) =>
            Assert.Throws<ArgumentException>(() => new FocusNormalizer(baseline, _config));

        [Test]
        public void Calibration_TooManyArtifacts_Fails()
        {
            var calibrator = new BaselineCalibrator(_config);
            for (var s = 0; s <= 30; s++)
            {
                calibrator.Add(new EegFrame { TimestampMs = s * 1000, Ratio = double.NaN, Artifact = ArtifactKind.Blink });
            }

            Assert.That(calibrator.Result.Outcome, Is.EqualTo(CalibrationOutcome.TooFewCleanWindows));
        }

        [Test]
        public void Calibration_ZeroRatios_FailsWithZeroBaseline()
        {
            var calibrator = new BaselineCalibrator(_config);
            for (var s = 0; s <= 30; s++)
            {
                calibrator.Add(new EegFrame { TimestampMs = s * 1000, Ratio = 0 });
            }

            Assert.That(calibrator.Result.Outcome, Is.EqualTo(CalibrationOutcome.ZeroBaseline));
        }

        [Test]
        public void Calibration_TakesMedian_AndAbortIsReported()
        {
            var calibrator = new BaselineCalibrator(_config);
            for (var s = 0; s <= 30; s++)
            {
                calibrator.Add(new EegFrame { TimestampMs = s * 1000, Ratio = s == 3 ? 500 : 2 });
            }

            Assert.That(calibrator.Result.BaselineRatio, Is.EqualTo(2));

            var aborted = new BaselineCalibrator(_config);
            aborted.Add(new EegFrame { TimestampMs = 0, Ratio = 1 });
            Assert.That(aborted.Abort().Outcome, Is.EqualTo(CalibrationOutcome.Aborted));
        }

        [Test]
        public void Estimator_SmoothsHoldsThroughArtifacts_ThenGoesStale()
        {
            var estimator = new FocusEstimator(_config);
            estimator.SetBaseline(1);
            Assert.That(estimator.Process(new EegFrame { TimestampMs = 0, Ratio = 1 }).Focus, Is.EqualTo(50));
            Assert.That(estimator.Process(new EegFrame { TimestampMs = 1000, Ratio = 4 }).Focus, Is.EqualTo(65).Within(1e-9));

            var held = estimator.Process(new EegFrame { TimestampMs = 2000, Ratio = double.NaN, Artifact = ArtifactKind.Blink });
            Assert.That(held.Focus, Is.EqualTo(65).Within(1e-9));

            var stale = estimator.Process(new EegFrame { TimestampMs = 7000, Ratio = double.NaN, Artifact = ArtifactKind.Blink });
            Assert.That(stale.Focus, Is.Null);
        }
    }

    public class ArtifactDetectorTests
    {
        private const int N = 512;
        private static readonly bool[] AllUsable = { true, true, true, true };

        private static float[][] Background() =>
            Enumerable.Range(0, 4).Select(ch => Enumerable.Range(0, N)
                .Select(i => (float)(10 * Math.Sin(2 * Math.PI * 10 * i / 250.0 + ch))).ToArray()).ToArray();

        private static void AddBump(float[] data, double amplitude)
        {
            for (var i = 0; i < N; i++)
            {
                var d = (i - 256) / 30.0;
                data[i] += (float)(amplitude * Math.Exp(-0.5 * d * d));
            }
        }

        private static ArtifactDetector Detector() => new ArtifactDetector(new EegPipelineConfig(), 2, 3);

        [Test]
        public void CleanSignal_IsNone() => Assert.That(Detector().Detect(Background(), N, AllUsable), Is.EqualTo(ArtifactKind.None));

        [Test]
        public void SymmetricSpikeOnTemples_IsBlink()
        {
            var w = Background();
            AddBump(w[2], 150);
            AddBump(w[3], 150);
            Assert.That(Detector().Detect(w, N, AllUsable), Is.EqualTo(ArtifactKind.Blink));
        }

        [Test]
        public void OppositePolarityOnTemples_IsEyeMovement()
        {
            var w = Background();
            AddBump(w[2], 120);
            AddBump(w[3], -120);
            Assert.That(Detector().Detect(w, N, AllUsable), Is.EqualTo(ArtifactKind.EyeMovement));
        }

        [Test]
        public void SpikeOnOccipital_IsHighAmplitude()
        {
            var w = Background();
            AddBump(w[0], 300);
            Assert.That(Detector().Detect(w, N, AllUsable), Is.EqualTo(ArtifactKind.HighAmplitude));
        }

        [Test]
        public void ManyNaNs_IsCorrupted()
        {
            var w = Background();
            for (var i = 0; i < 100; i++)
            {
                w[1][i] = float.NaN;
            }

            Assert.That(Detector().Detect(w, N, AllUsable), Is.EqualTo(ArtifactKind.Corrupted));
        }
    }

    public class PipelineOnSimulatorTests
    {
        private static double MeanFocus(double simFocus, double baseline, double seconds = 30)
        {
            var source = new SimulatedEegSource(new SimulatedEegOptions
            {
                RealTime = false, Focus = FocusProfiles.Constant(simFocus), BlinksPerMinute = 0, EyeMovementsPerMinute = 0,
            });
            using var worker = new EegProcessingWorker(source);
            worker.SetBaseline(baseline);
            source.ConnectAsync(default).Wait();
            Stream(source, worker, seconds);

            var focus = new System.Collections.Generic.List<double>();
            while (worker.TryGetFrame(out var f))
            {
                if (f.Focus.HasValue)
                {
                    focus.Add(f.Focus.Value);
                }
            }

            Assert.That(focus, Is.Not.Empty);
            return focus.Skip(focus.Count / 2).Average();
        }

        [Test]
        public void HigherSimulatedFocus_GivesHigherIndex()
        {
            var calibrated = Calibrate(0.5);
            Assert.That(calibrated.Succeeded, Is.True, $"{calibrated.Outcome} {calibrated.CleanWindows}/{calibrated.TotalWindows}");
            var low = MeanFocus(0.2, calibrated.BaselineRatio);
            var mid = MeanFocus(0.5, calibrated.BaselineRatio);
            var high = MeanFocus(0.8, calibrated.BaselineRatio);
            Assert.That(low, Is.LessThan(35));
            Assert.That(mid, Is.InRange(40, 60));
            Assert.That(high, Is.GreaterThan(70));
        }

        [Test]
        public void Calibration_WithBlinks_StillSucceeds_AndArtifactsAreFlagged()
        {
            var source = new SimulatedEegSource(new SimulatedEegOptions { RealTime = false, BlinksPerMinute = 12, EyeMovementsPerMinute = 4 });
            using var worker = new EegProcessingWorker(source);
            worker.StartCalibration();
            source.ConnectAsync(default).Wait();
            Stream(source, worker, 35);

            var kinds = new System.Collections.Generic.HashSet<ArtifactKind>();
            while (worker.TryGetFrame(out var f))
            {
                kinds.Add(f.Artifact);
            }

            Assert.That(worker.LastCalibration.Succeeded, Is.True, $"{worker.LastCalibration.Outcome} {worker.LastCalibration.CleanWindows}/{worker.LastCalibration.TotalWindows} {string.Join(",", kinds)}");
            Assert.That(kinds, Does.Contain(ArtifactKind.Blink));
            Assert.That(kinds, Does.Contain(ArtifactKind.EyeMovement));
        }

        [Test]
        public void BadContactChannel_IsReportedWeak_OthersOk()
        {
            var source = new SimulatedEegSource(new SimulatedEegOptions
            {
                RealTime = false, BadContactChannels = new[] { 1 }, BlinksPerMinute = 0, EyeMovementsPerMinute = 0,
            });
            using var worker = new EegProcessingWorker(source);
            source.ConnectAsync(default).Wait();
            Stream(source, worker, 5);
            Assert.That(worker.TryGetFrame(out var f), Is.True);
            Assert.That(f.ContactOk, Is.EqualTo(new[] { true, false, true, true }));
        }

        [Test]
        public void NaNDropouts_AreTolerated()
        {
            var source = new SimulatedEegSource(new SimulatedEegOptions { RealTime = false, NanProbability = 0.01, BlinksPerMinute = 0, EyeMovementsPerMinute = 0 });
            using var worker = new EegProcessingWorker(source);
            worker.SetBaseline(1);
            source.ConnectAsync(default).Wait();
            Stream(source, worker, 10);
            var frames = 0;
            while (worker.TryGetFrame(out var f))
            {
                frames++;
                Assert.That(f.Focus.HasValue, Is.True);
                Assert.That(double.IsNaN(f.Focus.Value), Is.False);
            }

            Assert.That(frames, Is.GreaterThan(5));
        }

        [Test]
        public void FirstFocusArrives_WithinThreeSecondsOfStream()
        {
            var source = new SimulatedEegSource(new SimulatedEegOptions { RealTime = false });
            using var worker = new EegProcessingWorker(source);
            worker.SetBaseline(1);
            source.ConnectAsync(default).Wait();
            Assert.That(Stream(source, worker, 3), Is.GreaterThan(0));
        }

        /// <summary>Advances the simulator second by second, pumping like the worker thread would.</summary>
        private static int Stream(SimulatedEegSource source, EegProcessingWorker worker, double seconds)
        {
            var frames = 0;
            for (var t = 0; t < seconds; t++)
            {
                source.Advance(1);
                frames += worker.Pump();
            }

            return frames;
        }

        private static CalibrationResult Calibrate(double simFocus)
        {
            var source = new SimulatedEegSource(new SimulatedEegOptions { RealTime = false, Focus = FocusProfiles.Constant(simFocus) });
            using var worker = new EegProcessingWorker(source);
            worker.StartCalibration();
            source.ConnectAsync(default).Wait();
            Stream(source, worker, 35);
            return worker.LastCalibration;
        }
    }
}

using System;
using System.Collections.Generic;
using NeuroScholAR.Core.Signal;

namespace NeuroScholAR.Core.Focus
{
    /// <summary>Focus-index thresholds and calibration rules.</summary>
    public sealed class FocusConfig
    {
        /// <summary>Spec: 30-second baseline.</summary>
        public double CalibrationSeconds = 30;

        /// <summary>Calibration fails if fewer than this share of windows were artifact-free.</summary>
        public double MinCleanFraction = 0.3; // people blink ~15-20/min, so many 2 s windows are rejected

        /// <summary>Baselines below this are treated as zero (flat signal): the division guard.</summary>
        public double MinBaselineRatio = 1e-6;

        /// <summary>
        /// Focus % gained per doubling of the ratio vs baseline. Baseline maps to 50 %, 2× to 75 %, 4× to 100 %,
        /// ½× to 25 %. The log scale matches how band-power ratios spread (multiplicatively, not additively).
        /// </summary>
        public double PercentPerDoubling = 25;

        /// <summary>EMA weight of the newest value (per 1 s update). 0.3 ≈ 3 s time constant.</summary>
        public double EmaAlpha = 0.3;

        /// <summary>Hold the last focus through artifact windows for at most this long, then report "no data".</summary>
        public double MaxHoldSeconds = 5;
    }

    public enum CalibrationOutcome
    {
        Completed,
        Aborted,
        TooFewCleanWindows,
        ZeroBaseline,
    }

    public sealed class CalibrationResult
    {
        public CalibrationResult(CalibrationOutcome outcome, double baselineRatio, int cleanWindows, int totalWindows)
        {
            Outcome = outcome;
            BaselineRatio = baselineRatio;
            CleanWindows = cleanWindows;
            TotalWindows = totalWindows;
        }

        public CalibrationOutcome Outcome { get; }
        public double BaselineRatio { get; }
        public int CleanWindows { get; }
        public int TotalWindows { get; }
        public bool Succeeded => Outcome == CalibrationOutcome.Completed;
    }

    /// <summary>Collects β/(θ+α) for the baseline period and takes the median (robust to stray windows).</summary>
    public sealed class BaselineCalibrator
    {
        private readonly FocusConfig _config;
        private readonly List<double> _ratios = new List<double>();
        private long _startMs = -1;
        private int _total;

        public BaselineCalibrator(FocusConfig config) => _config = config;

        public double Progress { get; private set; }
        public CalibrationResult Result { get; private set; }

        /// <summary>Feeds a frame; returns true once the result is ready.</summary>
        public bool Add(in EegFrame frame)
        {
            if (Result != null)
            {
                return true;
            }

            if (_startMs < 0)
            {
                _startMs = frame.TimestampMs;
            }

            _total++;
            if (frame.Artifact == ArtifactKind.None && IsFinite(frame.Ratio))
            {
                _ratios.Add(frame.Ratio);
            }

            var elapsed = (frame.TimestampMs - _startMs) / 1000.0;
            Progress = Math.Min(1, elapsed / _config.CalibrationSeconds);
            if (elapsed + 1e-9 < _config.CalibrationSeconds)
            {
                return false;
            }

            Result = Finish();
            return true;
        }

        public CalibrationResult Abort()
        {
            if (Result == null)
            {
                Result = new CalibrationResult(CalibrationOutcome.Aborted, double.NaN, _ratios.Count, _total);
            }

            return Result;
        }

        private CalibrationResult Finish()
        {
            if (_ratios.Count == 0 || _ratios.Count < _config.MinCleanFraction * _total)
            {
                return new CalibrationResult(CalibrationOutcome.TooFewCleanWindows, double.NaN, _ratios.Count, _total);
            }

            _ratios.Sort();
            var mid = _ratios.Count / 2;
            var median = _ratios.Count % 2 == 1 ? _ratios[mid] : (_ratios[mid - 1] + _ratios[mid]) / 2;
            var outcome = median < _config.MinBaselineRatio ? CalibrationOutcome.ZeroBaseline : CalibrationOutcome.Completed;
            return new CalibrationResult(outcome, median, _ratios.Count, _total);
        }

        private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    }

    /// <summary>Maps a raw ratio to 0–100 % against the calibrated baseline (see <see cref="FocusConfig.PercentPerDoubling"/>).</summary>
    public sealed class FocusNormalizer
    {
        private readonly double _baseline;
        private readonly double _percentPerDoubling;

        /// <exception cref="ArgumentException">Baseline is zero, negative or not finite: dividing by it would explode.</exception>
        public FocusNormalizer(double baselineRatio, FocusConfig config)
        {
            if (double.IsNaN(baselineRatio) || double.IsInfinity(baselineRatio) || baselineRatio < config.MinBaselineRatio)
            {
                throw new ArgumentException($"Invalid baseline ratio {baselineRatio}", nameof(baselineRatio));
            }

            _baseline = baselineRatio;
            _percentPerDoubling = config.PercentPerDoubling;
        }

        public double BaselineRatio => _baseline;

        /// <summary>Focus in %, or null when the ratio is unusable (NaN).</summary>
        public double? Normalize(double ratio)
        {
            if (double.IsNaN(ratio))
            {
                return null;
            }

            if (ratio <= 0)
            {
                return 0;
            }

            if (double.IsPositiveInfinity(ratio))
            {
                return 100;
            }

            var percent = 50 + _percentPerDoubling * Math.Log(ratio / _baseline, 2);
            return Math.Max(0, Math.Min(100, percent));
        }
    }

    /// <summary>What the UI and the adaptive rules see, once per second.</summary>
    public struct FocusFrame
    {
        public long TimestampMs;

        /// <summary>Smoothed focus 0–100, or null when there is no trustworthy signal (not calibrated, long artifact, no contact).</summary>
        public double? Focus;

        public ArtifactKind Artifact;
        public bool[] ContactOk;

        /// <summary>True while the baseline is being recorded.</summary>
        public bool Calibrating;

        public double CalibrationProgress;

        /// <summary>Raw inputs, for the dev overlay: band powers and β/(θ+α) of this window (NaN when rejected).</summary>
        public BandPowers Bands;
        public double Ratio;
    }

    /// <summary>
    /// Turns <see cref="EegFrame"/>s into <see cref="FocusFrame"/>s: either feeds the calibrator or normalizes
    /// and EMA-smooths. Artifact windows hold the last value briefly instead of making the HUD flicker.
    /// </summary>
    public sealed class FocusEstimator
    {
        private readonly FocusConfig _config;
        private BaselineCalibrator _calibrator;
        private FocusNormalizer _normalizer;
        private double? _ema;
        private long _lastValidMs;

        public FocusEstimator(FocusConfig config = null) => _config = config ?? new FocusConfig();

        public FocusConfig Config => _config;
        public bool IsCalibrated => _normalizer != null;
        public bool IsCalibrating => _calibrator != null && _calibrator.Result == null;
        public CalibrationResult LastCalibration { get; private set; }

        public void StartCalibration()
        {
            _calibrator = new BaselineCalibrator(_config);
            LastCalibration = null;
        }

        public void AbortCalibration()
        {
            if (_calibrator != null)
            {
                LastCalibration = _calibrator.Abort();
                _calibrator = null;
            }
        }

        /// <summary>Skips calibration with a known baseline (tests, or a future cross-session profile).</summary>
        public void SetBaseline(double baselineRatio)
        {
            _normalizer = new FocusNormalizer(baselineRatio, _config);
            _ema = null;
        }

        public FocusFrame Process(in EegFrame frame)
        {
            var output = new FocusFrame
            {
                TimestampMs = frame.TimestampMs,
                Artifact = frame.Artifact,
                ContactOk = frame.ContactOk,
                Bands = frame.Bands,
                Ratio = frame.Ratio,
            };

            if (_calibrator != null)
            {
                var done = _calibrator.Add(frame);
                output.Calibrating = !done;
                output.CalibrationProgress = _calibrator.Progress;
                if (done)
                {
                    LastCalibration = _calibrator.Result;
                    _calibrator = null;
                    if (LastCalibration.Succeeded)
                    {
                        SetBaseline(LastCalibration.BaselineRatio);
                    }
                }

                return output;
            }

            if (_normalizer == null)
            {
                return output;
            }

            var focus = frame.Artifact == ArtifactKind.None ? _normalizer.Normalize(frame.Ratio) : null;
            if (focus.HasValue)
            {
                _ema = _ema.HasValue ? _ema + _config.EmaAlpha * (focus.Value - _ema.Value) : focus;
                _lastValidMs = frame.TimestampMs;
            }
            else if (frame.TimestampMs - _lastValidMs > _config.MaxHoldSeconds * 1000)
            {
                _ema = null; // stale: say "no data" rather than show an old number
            }

            output.Focus = _ema;
            return output;
        }

        /// <summary>Forget the smoothed value (after a disconnect the old value no longer describes the user).</summary>
        public void ResetSmoothing() => _ema = null;
    }
}

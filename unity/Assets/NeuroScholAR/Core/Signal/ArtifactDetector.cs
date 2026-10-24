using System;

namespace NeuroScholAR.Core.Signal
{
    public enum ArtifactKind
    {
        None,
        Blink,
        EyeMovement,

        /// <summary>Large amplitude on a non-ocular channel: motion, jaw clench, electrode pop.</summary>
        HighAmplitude,

        /// <summary>Too many NaN samples to trust the window.</summary>
        Corrupted,
    }

    /// <summary>
    /// Flags windows that must not feed the focus index. BrainBit has no frontal electrodes, so ocular
    /// activity is read off the temporal pair (T3/T4): both swing the same way on a blink (vertical EOG
    /// spreads symmetrically), opposite ways on a horizontal eye movement (the eye is a dipole between the temples).
    /// </summary>
    public sealed class ArtifactDetector
    {
        private readonly EegPipelineConfig _config;
        private readonly int _left;
        private readonly int _right;

        /// <param name="leftEye">Index of the left ocular channel (T3), or -1 if absent.</param>
        /// <param name="rightEye">Index of the right ocular channel (T4), or -1 if absent.</param>
        public ArtifactDetector(EegPipelineConfig config, int leftEye, int rightEye)
        {
            _config = config;
            _left = leftEye;
            _right = rightEye;
        }

        /// <param name="channels">Filtered window per channel, NaN where samples were lost.</param>
        /// <param name="length">Samples per channel to inspect.</param>
        /// <param name="usable">Channels with acceptable contact. Bad channels are ignored, not treated as artifacts.</param>
        public ArtifactKind Detect(float[][] channels, int length, bool[] usable)
        {
            for (var ch = 0; ch < channels.Length; ch++)
            {
                if (usable[ch] && NanFraction(channels[ch], length) > _config.MaxNanFraction)
                {
                    return ArtifactKind.Corrupted;
                }
            }

            var ocular = Ocular(channels, length, usable);
            if (ocular != ArtifactKind.None)
            {
                return ocular;
            }

            for (var ch = 0; ch < channels.Length; ch++)
            {
                if (ch == _left || ch == _right || !usable[ch])
                {
                    continue;
                }

                if (PeakAbs(channels[ch], length) > _config.HighAmplitudeUv)
                {
                    return ArtifactKind.HighAmplitude;
                }
            }

            return ArtifactKind.None;
        }

        private ArtifactKind Ocular(float[][] channels, int length, bool[] usable)
        {
            if (_left < 0 || _right < 0 || !usable[_left] || !usable[_right])
            {
                return ArtifactKind.None;
            }

            // Find the moment where the ocular pair deviates the most, then judge its polarity.
            var left = channels[_left];
            var right = channels[_right];
            var best = 0.0;
            var bestIndex = -1;
            for (var i = 0; i < length; i++)
            {
                var a = Math.Abs(left[i]) + Math.Abs(right[i]);
                if (!double.IsNaN(a) && a > best)
                {
                    best = a;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0 || best < 2 * _config.OcularThresholdUv)
            {
                return ArtifactKind.None;
            }

            var l = left[bestIndex];
            var r = right[bestIndex];
            if (Math.Abs(l) < _config.OcularThresholdUv * 0.5 || Math.Abs(r) < _config.OcularThresholdUv * 0.5)
            {
                // Only one side moved: not an eye, but still too big to trust.
                return ArtifactKind.HighAmplitude;
            }

            return Math.Sign(l) == Math.Sign(r) ? ArtifactKind.Blink : ArtifactKind.EyeMovement;
        }

        internal static double NanFraction(float[] data, int length)
        {
            var nan = 0;
            for (var i = 0; i < length; i++)
            {
                if (float.IsNaN(data[i]))
                {
                    nan++;
                }
            }

            return length == 0 ? 1 : (double)nan / length;
        }

        private static double PeakAbs(float[] data, int length)
        {
            var peak = 0.0;
            for (var i = 0; i < length; i++)
            {
                if (!float.IsNaN(data[i]))
                {
                    peak = Math.Max(peak, Math.Abs(data[i]));
                }
            }

            return peak;
        }
    }
}

using System;

namespace NeuroScholAR.Core.Signal
{
    /// <summary>
    /// In-place iterative radix-2 FFT for one fixed size. Twiddles and bit-reversal are precomputed once,
    /// so each transform is O(n log n) with no allocations.
    /// </summary>
    public sealed class Fft
    {
        private readonly double[] _cos;
        private readonly double[] _sin;
        private readonly int[] _reversed;

        public Fft(int size)
        {
            if (size < 2 || (size & (size - 1)) != 0)
            {
                throw new ArgumentException("FFT size must be a power of two", nameof(size));
            }

            Size = size;
            _cos = new double[size / 2];
            _sin = new double[size / 2];
            for (var i = 0; i < size / 2; i++)
            {
                _cos[i] = Math.Cos(-2 * Math.PI * i / size);
                _sin[i] = Math.Sin(-2 * Math.PI * i / size);
            }

            var bits = (int)Math.Round(Math.Log(size, 2));
            _reversed = new int[size];
            for (var i = 0; i < size; i++)
            {
                var r = 0;
                for (var b = 0; b < bits; b++)
                {
                    r |= ((i >> b) & 1) << (bits - 1 - b);
                }

                _reversed[i] = r;
            }
        }

        public int Size { get; }

        public void Transform(double[] re, double[] im)
        {
            for (var i = 0; i < Size; i++)
            {
                var j = _reversed[i];
                if (j > i)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (var len = 2; len <= Size; len <<= 1)
            {
                var half = len / 2;
                var step = Size / len;
                for (var start = 0; start < Size; start += len)
                {
                    for (var k = 0; k < half; k++)
                    {
                        var wr = _cos[k * step];
                        var wi = _sin[k * step];
                        var a = start + k;
                        var b = a + half;
                        var tr = re[b] * wr - im[b] * wi;
                        var ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
        }
    }
}

using System;

namespace NeuroScholAR.Core.Signal
{
    /// <summary>Fixed-size float ring buffer. Never reallocates, so the DSP loop stays allocation-free.</summary>
    public sealed class RingBuffer
    {
        private readonly float[] _data;
        private int _next;

        public RingBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _data = new float[capacity];
        }

        public int Capacity => _data.Length;
        public int Count { get; private set; }
        public bool IsFull => Count == _data.Length;

        public void Push(float value)
        {
            _data[_next] = value;
            _next = (_next + 1) % _data.Length;
            if (Count < _data.Length)
            {
                Count++;
            }
        }

        /// <summary>Copies the contents oldest-first into <paramref name="destination"/> (length ≥ Count).</summary>
        public void CopyTo(float[] destination)
        {
            var start = (_next - Count + _data.Length) % _data.Length;
            var firstPart = Math.Min(Count, _data.Length - start);
            Array.Copy(_data, start, destination, 0, firstPart);
            Array.Copy(_data, 0, destination, firstPart, Count - firstPart);
        }

        public void Clear()
        {
            Count = 0;
            _next = 0;
        }
    }
}

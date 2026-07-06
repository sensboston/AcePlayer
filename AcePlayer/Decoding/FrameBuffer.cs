using System;
using System.Collections.Generic;
using System.Threading;

namespace AcePlayer.Decoding
{
    /// <summary>A decoded BGRA video frame plus its presentation time (seconds).</summary>
    public sealed class DecodedFrame
    {
        public byte[] Bgra;     // may be larger than Width*Height*4 (pooled)
        public int Width;
        public int Height;
        public double Pts;      // seconds
    }

    /// <summary>
    /// Bounded, PTS-ordered queue of decoded video frames with an internal buffer pool.
    ///
    /// The decoder pushes frames (blocking when full — that backpressure paces the decode loop
    /// so a local file cannot race ahead). The presenter pulls the frame whose PTS matches the
    /// playback clock, recycling the ones it skipped past. Big pixel buffers are pooled so the
    /// 100+ MB/s of frame data never hits the GC.
    /// </summary>
    public sealed class FrameBuffer
    {
        private readonly object _gate = new object();
        private readonly Queue<DecodedFrame> _ready = new Queue<DecodedFrame>();
        private readonly Stack<byte[]> _pool = new Stack<byte[]>();
        private readonly int _capacity;
        private volatile bool _flushing;
        private volatile bool _closed;

        public FrameBuffer(int capacityFrames = 24)
        {
            _capacity = Math.Max(2, capacityFrames);
        }

        public int Count { get { lock (_gate) return _ready.Count; } }

        /// <summary>Total frames pushed by the decoder (for decode-rate stats).</summary>
        public long TotalPushed { get; private set; }

        /// <summary>Producer. Copies <paramref name="src"/> into a pooled buffer. Blocks while full.</summary>
        public void Push(byte[] src, int width, int height, double pts)
        {
            int needed = width * height * 4;
            lock (_gate)
            {
                while (_ready.Count >= _capacity && !_flushing && !_closed)
                    Monitor.Wait(_gate);
                if (_closed) return;

                byte[] buf = null;
                while (_pool.Count > 0)
                {
                    var candidate = _pool.Pop();
                    if (candidate.Length >= needed) { buf = candidate; break; }
                }
                if (buf == null) buf = new byte[needed];

                Buffer.BlockCopy(src, 0, buf, 0, needed);
                _ready.Enqueue(new DecodedFrame { Bgra = buf, Width = width, Height = height, Pts = pts });
                TotalPushed++;
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>
        /// Presenter. Returns the newest frame with Pts &lt;= <paramref name="clock"/>, recycling any
        /// earlier frames it skips. Returns null when the next frame is still in the future (keep
        /// showing the current one) or the buffer is empty.
        /// </summary>
        public DecodedFrame Take(double clock)
        {
            lock (_gate)
            {
                DecodedFrame chosen = null;
                while (_ready.Count > 0 && _ready.Peek().Pts <= clock)
                {
                    if (chosen != null) Recycle(chosen);
                    chosen = _ready.Dequeue();
                }
                if (chosen != null) Monitor.PulseAll(_gate);
                return chosen;
            }
        }

        /// <summary>Peek the PTS of the oldest queued frame (for clock priming). NaN if empty.</summary>
        public double OldestPts()
        {
            lock (_gate) return _ready.Count > 0 ? _ready.Peek().Pts : double.NaN;
        }

        /// <summary>
        /// Drop everything but the newest frame and return it (for the Live button). The caller
        /// should resync the clock to the returned frame's PTS. Null if empty.
        /// </summary>
        public DecodedFrame DropToNewest()
        {
            lock (_gate)
            {
                DecodedFrame newest = null;
                while (_ready.Count > 0)
                {
                    if (newest != null) Recycle(newest);
                    newest = _ready.Dequeue();
                }
                Monitor.PulseAll(_gate);
                return newest;
            }
        }

        /// <summary>Return a frame's buffer to the pool once the presenter is done with it.</summary>
        public void Recycle(DecodedFrame frame)
        {
            if (frame?.Bgra == null) return;
            lock (_gate)
            {
                if (_pool.Count < _capacity + 4)
                    _pool.Push(frame.Bgra);
            }
            frame.Bgra = null;
        }

        public void Flush()
        {
            lock (_gate)
            {
                _flushing = true;
                while (_ready.Count > 0) Recycle(_ready.Dequeue());
                Monitor.PulseAll(_gate);
                _flushing = false;
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                Monitor.PulseAll(_gate);
            }
        }
    }
}

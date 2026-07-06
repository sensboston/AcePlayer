using System;
using System.Threading;
using NAudio.Wave;

namespace AcePlayer.Decoding
{
    /// <summary>
    /// Bounded, thread-safe PCM byte buffer exposed to NAudio as an <see cref="IWaveProvider"/>.
    ///
    /// The producer (decoder) blocks in <see cref="Write"/> when the buffer is full. Because the
    /// audio device drains this buffer at exactly real time, that block is what paces the entire
    /// decode loop — video is then presented "newest wins", audio stays continuous. For a genuine
    /// live network source the buffer rarely fills (data arrives at real time anyway); for a local
    /// file it prevents the decoder from racing ahead and ballooning memory.
    /// </summary>
    public sealed class PcmQueue : IWaveProvider
    {
        private readonly object _gate = new object();
        private readonly byte[] _buffer;
        private int _head;      // read position
        private int _count;     // valid bytes
        private volatile bool _flushing;
        private volatile bool _closed;

        public WaveFormat WaveFormat { get; }

        public PcmQueue(WaveFormat format, int capacityBytes)
        {
            WaveFormat = format;
            _buffer = new byte[capacityBytes];
        }

        public int BufferedBytes { get { lock (_gate) return _count; } }
        public int Capacity => _buffer.Length;

        /// <summary>Producer side. Blocks while full unless flushing/closed. Returns false if closed.</summary>
        public bool Write(byte[] data, int offset, int length)
        {
            int written = 0;
            while (written < length)
            {
                lock (_gate)
                {
                    while (_count == _buffer.Length && !_flushing && !_closed)
                        Monitor.Wait(_gate);

                    if (_closed) return false;
                    if (_flushing) { return true; } // drop the rest, presenter is reseeking to live

                    int free = _buffer.Length - _count;
                    int chunk = Math.Min(free, length - written);
                    int tail = (_head + _count) % _buffer.Length;
                    int toEnd = Math.Min(chunk, _buffer.Length - tail);
                    Buffer.BlockCopy(data, offset + written, _buffer, tail, toEnd);
                    if (toEnd < chunk)
                        Buffer.BlockCopy(data, offset + written + toEnd, _buffer, 0, chunk - toEnd);
                    _count += chunk;
                    written += chunk;
                    Monitor.PulseAll(_gate);
                }
            }
            return true;
        }

        /// <summary>Consumer side (NAudio). Never blocks; missing bytes are filled with silence.</summary>
        public int Read(byte[] destination, int offset, int count)
        {
            lock (_gate)
            {
                int available = Math.Min(count, _count);
                int read = 0;
                while (read < available)
                {
                    int toEnd = Math.Min(available - read, _buffer.Length - _head);
                    Buffer.BlockCopy(_buffer, _head, destination, offset + read, toEnd);
                    _head = (_head + toEnd) % _buffer.Length;
                    read += toEnd;
                }
                _count -= read;
                if (read > 0) Monitor.PulseAll(_gate);

                // Pad with silence so the audio graph keeps a steady clock on underflow.
                if (read < count)
                    Array.Clear(destination, offset + read, count - read);
                return count;
            }
        }

        /// <summary>Drop everything buffered (used by the Live button / seek). Wakes a blocked producer.</summary>
        public void Flush()
        {
            lock (_gate)
            {
                _flushing = true;
                _head = 0;
                _count = 0;
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

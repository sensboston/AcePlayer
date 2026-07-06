using System;
using NAudio.Wave;
using AcePlayer.Decoding;

namespace AcePlayer.Audio
{
    /// <summary>Pulls PCM from a <see cref="PcmQueue"/> and plays it via NAudio (WaveOut).</summary>
    public sealed class AudioPlayer : IDisposable
    {
        private WaveOutEvent _out;
        private readonly PcmQueue _queue;

        public AudioPlayer(PcmQueue queue)
        {
            _queue = queue;
        }

        public void Start()
        {
            _out = new WaveOutEvent
            {
                DesiredLatency = 120,   // ms; keep audio latency low but stable
                NumberOfBuffers = 3,
            };
            _out.Init(_queue);
            _out.Play();
        }

        public void Dispose()
        {
            try { _out?.Stop(); } catch { }
            try { _out?.Dispose(); } catch { }
            _out = null;
        }
    }
}

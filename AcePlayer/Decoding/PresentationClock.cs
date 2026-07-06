using System.Diagnostics;

namespace AcePlayer.Decoding
{
    /// <summary>
    /// A smooth, fine-grained playback clock in stream-PTS seconds, driven by a wall-clock
    /// Stopwatch. Audio plays at real time, so wall time tracks it closely; the presenter samples
    /// this clock at vsync (60 Hz) to pick the right video frame — decoupling presentation
    /// smoothness from the audio device's coarse buffer-read cadence.
    ///
    /// SyncTo() re-anchors the clock (first frame, or the Live button snapping to the newest PTS).
    /// </summary>
    public sealed class PresentationClock
    {
        private readonly Stopwatch _sw = new Stopwatch();
        private double _anchorPts;   // stream PTS that corresponds to _sw == 0
        private bool _started;

        public bool IsStarted => _started;

        /// <summary>Anchor the clock so that "now" maps to <paramref name="pts"/> seconds.</summary>
        public void SyncTo(double pts)
        {
            _anchorPts = pts;
            _sw.Restart();
            _started = true;
        }

        public double NowSeconds => _started ? _anchorPts + _sw.Elapsed.TotalSeconds : 0.0;

        public void Stop()
        {
            _sw.Reset();
            _started = false;
        }
    }
}

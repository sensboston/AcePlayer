using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AcePlayer.Decoding;

namespace AcePlayer.Rendering
{
    /// <summary>
    /// Presents decoded frames from a <see cref="FrameBuffer"/> using a <see cref="PresentationClock"/>.
    ///
    /// Playout model tuned for "freshest frames, but smooth": we keep a small prebuffer cushion and
    /// only run the clock once it is filled. On any underrun or PTS discontinuity (a stall, or a
    /// reopen that jumps to the live edge) we re-enter priming — rebuild the cushion, then resume —
    /// so neither startup nor a live-jump stutters. Latency stays bounded by the cushion size.
    /// </summary>
    public sealed class VideoView : Image
    {
        // Cushion before (re)starting playout. ~0.4-0.5 s at broadcast frame rates.
        private const int PrebufferFrames = 10;
        private const double PrimeTimeoutSeconds = 1.5;   // resume anyway if frames trickle in slowly
        private const double DiscontinuitySeconds = 2.0;  // |bufferedPts - clock| beyond this = reanchor

        private FrameBuffer _buffer;
        private PresentationClock _clock;
        private WriteableBitmap _bitmap;
        private int _bmpW, _bmpH;
        private bool _hooked;

        private bool _priming = true;
        private readonly Stopwatch _primeTimer = new Stopwatch();

        // Accumulated lag behind live: total wall time spent frozen on underruns since the last
        // jump-to-live. In a no-skip live model that freeze time is exactly how far we fell behind.
        private bool _hasStarted;
        private bool _inUnderrun;
        private bool _ignoreUnderrun;      // the refill right after a deliberate jump-to-live isn't lag
        private double _bankedLag;
        private readonly Stopwatch _underrunTimer = new Stopwatch();
        /// <summary>Total freeze time behind live, including any freeze in progress right now.</summary>
        public double LagSeconds => _bankedLag + (_inUnderrun ? _underrunTimer.Elapsed.TotalSeconds : 0);
        public void ResetLag() { _bankedLag = 0; _inUnderrun = false; _ignoreUnderrun = true; }

        public VideoView()
        {
            Stretch = Stretch.Uniform;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
            SnapsToDevicePixels = true;
            Loaded += (s, e) => Hook();
            Unloaded += (s, e) => Unhook();
        }

        public long PresentedFrames { get; private set; }
        public long RenderTicks { get; private set; }

        public void Attach(FrameBuffer buffer, PresentationClock clock)
        {
            _buffer = buffer;
            _clock = clock;
            _priming = true;
            _primeTimer.Restart();
            Hook();
        }

        public void Detach()
        {
            _buffer = null;
            _clock = null;
        }

        private void Hook()
        {
            if (_hooked) return;
            CompositionTarget.Rendering += OnRendering;
            _hooked = true;
        }

        private void Unhook()
        {
            if (!_hooked) return;
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }

        private void OnRendering(object sender, EventArgs e)
        {
            RenderTicks++;
            var buffer = _buffer;
            var clock = _clock;
            if (buffer == null || clock == null) return;

            // Priming: hold on the current frame until the cushion is built, then anchor the clock.
            if (_priming)
            {
                if (buffer.Count >= PrebufferFrames ||
                    (buffer.Count > 0 && _primeTimer.Elapsed.TotalSeconds > PrimeTimeoutSeconds))
                {
                    clock.SyncTo(buffer.OldestPts());
                    _priming = false;
                    // A recovered underrun is time we fell behind live — bank it as lag.
                    if (_inUnderrun) { _bankedLag += _underrunTimer.Elapsed.TotalSeconds; _inUnderrun = false; }
                }
                else return;
            }

            // Discontinuity (a jump-to-live, or a PTS gap): rebuild the cushion cleanly, no lag added.
            double oldest = buffer.OldestPts();
            if (!double.IsNaN(oldest) && Math.Abs(oldest - clock.NowSeconds) > DiscontinuitySeconds)
            {
                _priming = true;
                _primeTimer.Restart();
                return;
            }

            var frame = buffer.Take(clock.NowSeconds);
            if (frame == null)
            {
                // Underrun: nothing to show. If the buffer ran dry, re-prime so the resume is smooth
                // and start counting the freeze as accumulated lag behind live.
                if (buffer.Count == 0)
                {
                    _priming = true;
                    _primeTimer.Restart();
                    if (_hasStarted && !_inUnderrun && !_ignoreUnderrun)
                    { _inUnderrun = true; _underrunTimer.Restart(); }
                }
                return;
            }

            try { Present(frame); PresentedFrames++; _hasStarted = true; _ignoreUnderrun = false; }
            finally { buffer.Recycle(frame); }
        }

        private void Present(DecodedFrame frame)
        {
            int w = frame.Width, h = frame.Height;
            if (w <= 0 || h <= 0) return;

            if (_bitmap == null || _bmpW != w || _bmpH != h)
            {
                _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                _bmpW = w; _bmpH = h;
                Source = _bitmap;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, w, h), frame.Bgra, w * 4, 0);
        }
    }
}

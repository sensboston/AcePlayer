using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using AcePlayer.Engine;

namespace AcePlayer.Decoding
{
    /// <summary>
    /// Opens a media URL (Ace Stream playback URL, plain HTTP MPEG-TS, or a local file),
    /// demuxes and decodes it on a background thread, pushing video into a
    /// <see cref="FrameBuffer"/> (with PTS) and audio into a <see cref="PcmQueue"/>.
    ///
    /// Presentation is clock-driven downstream; here we just decode as fast as pacing allows.
    /// </summary>
    public sealed unsafe class MediaDecoder : IDisposable
    {
        public const int AudioSampleRate = 48000;
        public const int AudioChannels = 2;               // interleaved s16

        private readonly string _url;
        private readonly FrameBuffer _video;
        private readonly PcmQueue _audio;
        private AVRational _videoStreamTb;                // video stream time_base
        private readonly bool _deinterlace;

        private Thread _thread;
        private volatile bool _running;

        // interrupt-callback plumbing so a blocked network read can be aborted on Stop().
        private int* _abort;                              // unmanaged flag, read by the callback
        private AVIOInterruptCB_callback _interruptDelegate;

        // Live-edge control. Jump-to-live drains the buffered backlog on the SAME connection to reach
        // the live tail — no HTTP reopen (which pushed the engine back into prebuffering / stutter).
        private volatile bool _catchupRequested;
        public bool AutoLive { get; set; } = true;        // (unused; live is driven by the host)

        // Seeking (timeshift / VOD). Applied at the top of the read loop.
        private volatile bool _seekRequested;
        private double _seekTargetSeconds;

        public event Action<int, int> VideoSizeChanged;   // (width, height)
        public event Action<string> Failed;               // fatal error message
        public event Action Ended;                        // stream finished cleanly
        public event Action<int> Reconnecting;            // (attempt number) transient reconnect

        public int VideoWidth { get; private set; }
        public int VideoHeight { get; private set; }

        public MediaDecoder(string url, FrameBuffer video, PcmQueue audio, bool deinterlace = true)
        {
            _url = url;
            _video = video;
            _audio = audio;
            _deinterlace = deinterlace;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _abort = (int*)Marshal.AllocHGlobal(sizeof(int));
            *_abort = 0;
            _thread = new Thread(Run) { IsBackground = true, Name = "MediaDecoder" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_abort != null) *_abort = 1;              // unblock any pending av_read_frame
            var t = _thread;
            if (t != null && t.IsAlive && t != Thread.CurrentThread)
                t.Join(3000);
        }

        public void Dispose()
        {
            Stop();
            if (_abort != null)
            {
                Marshal.FreeHGlobal((IntPtr)_abort);
                _abort = null;
            }
        }

        private int InterruptCallback(void* opaque) => *_abort;

        /// <summary>Jump to the live edge: drop the buffered backlog on the current connection.</summary>
        public void GoLive() => _catchupRequested = true;

        /// <summary>Seek to a content position (seconds) — timeshift rewind / jump-to-live on the cache.</summary>
        public void Seek(double seconds)
        {
            _seekTargetSeconds = seconds;
            _seekRequested = true;
        }

        private enum ConnResult { Stopped, EndedClean, Error, GoLive }

        private long _connReads;   // packets read in the most recent connection (for reconnect reset)

        private void Run()
        {
            AVPacket* pkt = ffmpeg.av_packet_alloc();
            AVFrame* frame = ffmpeg.av_frame_alloc();
            _interruptDelegate = InterruptCallback;

            const int MaxReconnect = 5;
            int attempts = 0;
            try
            {
                while (_running)
                {
                    ConnResult result;
                    try { result = RunOneConnection(pkt, frame); }
                    catch (Exception ex) { Fail("Исключение в декодере: " + ex.Message); return; }

                    if (!_running || result == ConnResult.Stopped) break;
                    if (result == ConnResult.EndedClean) { Ended?.Invoke(); break; }
                    if (result == ConnResult.GoLive) { attempts = 0; continue; }   // reopen at live edge, no backoff

                    // Read error / stall: reconnect with a short interruptible backoff.
                    if (_connReads > 0) attempts = 0;            // stream had been flowing -> fresh budget
                    if (++attempts > MaxReconnect) { Fail("Поток прервался и не восстановился."); break; }
                    Reconnecting?.Invoke(attempts);
                    for (int i = 0; i < 12 && _running; i++) Thread.Sleep(100);   // ~1.2s
                }
            }
            finally
            {
                if (frame != null) ffmpeg.av_frame_free(&frame);
                if (pkt != null) ffmpeg.av_packet_free(&pkt);
                _running = false;
            }
        }

        /// <summary>Opens the input, decodes until it stops, errors, or ends. Frees its own contexts.</summary>
        private ConnResult RunOneConnection(AVPacket* pkt, AVFrame* frame)
        {
            AVFormatContext* fmt = null;
            AVCodecContext* vCtx = null;
            AVCodecContext* aCtx = null;
            SwrContext* swr = null;
            VideoConverter vconv = null;

            byte[] pcm = null;
            int swrInRate = 0; AVSampleFormat swrInFmt = AVSampleFormat.AV_SAMPLE_FMT_NONE;

            _connReads = 0;
            try
            {
                fmt = ffmpeg.avformat_alloc_context();
                fmt->interrupt_callback.callback =
                    new AVIOInterruptCB_callback_func { Pointer = Marshal.GetFunctionPointerForDelegate(_interruptDelegate) };
                fmt->interrupt_callback.opaque = null;

                AVDictionary* opts = null;
                ffmpeg.av_dict_set(&opts, "fflags", "nobuffer+discardcorrupt", 0);
                ffmpeg.av_dict_set(&opts, "flags", "low_delay", 0);
                ffmpeg.av_dict_set(&opts, "probesize", "500000", 0);
                ffmpeg.av_dict_set(&opts, "analyzeduration", "700000", 0);  // 0.7s: quick (re)open
                ffmpeg.av_dict_set(&opts, "max_delay", "0", 0);
                ffmpeg.av_dict_set(&opts, "reconnect", "1", 0);
                ffmpeg.av_dict_set(&opts, "reconnect_streamed", "1", 0);
                ffmpeg.av_dict_set(&opts, "reconnect_delay_max", "5", 0);
                ffmpeg.av_dict_set(&opts, "rw_timeout", "15000000", 0);      // 15s I/O timeout (us)
                // Local timeshift HLS playlist: allow file access, and start near the live edge.
                ffmpeg.av_dict_set(&opts, "protocol_whitelist", "file,crypto,data,http,https,tcp,tls,hls,pipe", 0);
                ffmpeg.av_dict_set(&opts, "allowed_extensions", "ALL", 0);
                ffmpeg.av_dict_set(&opts, "live_start_index", "-1", 0);      // last segment = live

                int openResult = ffmpeg.avformat_open_input(&fmt, _url, null, &opts);
                ffmpeg.av_dict_free(&opts);
                if (openResult < 0)
                {
                    if (!_running) return ConnResult.Stopped;
                    return ConnResult.Error;   // e.g. HTTP 500 while the engine is still prebuffering
                }

                if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                    return _running ? ConnResult.Error : ConnResult.Stopped;

                int vIdx = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                int aIdx = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
                if (vIdx < 0 && aIdx < 0) return ConnResult.Error;

                if (vIdx >= 0)
                {
                    vCtx = OpenCodec(fmt, vIdx);
                    _videoStreamTb = fmt->streams[vIdx]->time_base;
                    if (vCtx != null)
                    {
                        vconv = new VideoConverter(_video, _deinterlace);   // native resolution
                        vconv.SizeChanged += (w, h) =>
                        {
                            VideoWidth = w; VideoHeight = h;
                            VideoSizeChanged?.Invoke(w, h);
                        };
                    }
                }
                if (aIdx >= 0) aCtx = OpenCodec(fmt, aIdx);
                if (vIdx >= 0 && vCtx == null) { Fail("Не удалось открыть видеокодек."); return ConnResult.Stopped; }

                while (_running)
                {
                    if (_seekRequested)
                    {
                        _seekRequested = false;
                        long ts = (long)(Math.Max(0, _seekTargetSeconds) * ffmpeg.AV_TIME_BASE);
                        ffmpeg.av_seek_frame(fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                        if (vCtx != null) ffmpeg.avcodec_flush_buffers(vCtx);
                        if (aCtx != null) ffmpeg.avcodec_flush_buffers(aCtx);
                    }

                    if (_catchupRequested)
                    {
                        _catchupRequested = false;
                        DrainToLive(fmt, pkt, vCtx, aCtx);
                        continue;
                    }

                    int rf = ffmpeg.av_read_frame(fmt, pkt);
                    if (rf < 0)
                    {
                        if (!_running) return ConnResult.Stopped;
                        if (rf == ffmpeg.AVERROR_EOF) return ConnResult.EndedClean;
                        return ConnResult.Error;   // dropped/stalled connection -> outer loop reopens
                    }
                    _connReads++;

                    if (vCtx != null && pkt->stream_index == vIdx)
                        DecodeVideo(vCtx, pkt, frame, vconv, _videoStreamTb);
                    else if (aCtx != null && pkt->stream_index == aIdx)
                        DecodeAudio(aCtx, pkt, frame, ref swr, ref pcm, ref swrInRate, ref swrInFmt);

                    ffmpeg.av_packet_unref(pkt);
                }
                return ConnResult.Stopped;
            }
            finally
            {
                vconv?.Dispose();
                if (swr != null) ffmpeg.swr_free(&swr);
                if (vCtx != null) ffmpeg.avcodec_free_context(&vCtx);
                if (aCtx != null) ffmpeg.avcodec_free_context(&aCtx);
                if (fmt != null) ffmpeg.avformat_close_input(&fmt);
            }
        }

        /// <summary>
        /// Drop buffered packets on the current connection until reads throttle back to real time,
        /// which means the engine's backlog is drained and we are at the live edge. No reopen.
        /// </summary>
        private void DrainToLive(AVFormatContext* fmt, AVPacket* pkt, AVCodecContext* vCtx, AVCodecContext* aCtx)
        {
            var total = Stopwatch.StartNew();
            var readSw = new Stopwatch();
            int dropped = 0;
            while (_running && total.Elapsed.TotalSeconds < 3.0)
            {
                readSw.Restart();
                int r = ffmpeg.av_read_frame(fmt, pkt);
                if (r < 0) break;
                ffmpeg.av_packet_unref(pkt);
                dropped++;
                // A read that had to wait ~real time means the backlog is gone and we're live.
                if (readSw.Elapsed.TotalMilliseconds > 120 && dropped > 2) break;
            }
            if (vCtx != null) ffmpeg.avcodec_flush_buffers(vCtx);
            if (aCtx != null) ffmpeg.avcodec_flush_buffers(aCtx);
        }

        private AVCodecContext* OpenCodec(AVFormatContext* fmt, int idx)
        {
            AVStream* stream = fmt->streams[idx];
            AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec == null) return null;

            AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(codec);
            if (ffmpeg.avcodec_parameters_to_context(ctx, stream->codecpar) < 0)
            { ffmpeg.avcodec_free_context(&ctx); return null; }

            // Frame threading (the default) maximises decode throughput for 1080p; the small extra
            // latency it adds is absorbed by the presentation buffer downstream.
            ctx->thread_count = 0;                       // auto (≈ CPU cores)

            if (ffmpeg.avcodec_open2(ctx, codec, null) < 0)
            { ffmpeg.avcodec_free_context(&ctx); return null; }
            return ctx;
        }

        private void DecodeVideo(AVCodecContext* ctx, AVPacket* pkt, AVFrame* frame,
            VideoConverter conv, AVRational streamTimeBase)
        {
            if (ffmpeg.avcodec_send_packet(ctx, pkt) < 0) return;
            while (ffmpeg.avcodec_receive_frame(ctx, frame) == 0)
            {
                conv.Process(frame, streamTimeBase);          // deinterlace -> BGRA -> FrameBuffer
                ffmpeg.av_frame_unref(frame);
            }
        }

        private void DecodeAudio(AVCodecContext* ctx, AVPacket* pkt, AVFrame* frame,
            ref SwrContext* swr, ref byte[] pcm, ref int swrInRate, ref AVSampleFormat swrInFmt)
        {
            if (ffmpeg.avcodec_send_packet(ctx, pkt) < 0) return;
            while (ffmpeg.avcodec_receive_frame(ctx, frame) == 0)
            {
                int inRate = frame->sample_rate;
                var inFmt = (AVSampleFormat)frame->format;
                if (inRate <= 0) { ffmpeg.av_frame_unref(frame); continue; }

                if (swr == null || inRate != swrInRate || inFmt != swrInFmt)
                {
                    if (swr != null) { fixed (SwrContext** p = &swr) ffmpeg.swr_free(p); }
                    AVChannelLayout outLayout;
                    ffmpeg.av_channel_layout_default(&outLayout, AudioChannels);
                    SwrContext* s = null;
                    ffmpeg.swr_alloc_set_opts2(&s, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, AudioSampleRate,
                        &frame->ch_layout, inFmt, inRate, 0, null);
                    ffmpeg.swr_init(s);
                    swr = s;
                    swrInRate = inRate; swrInFmt = inFmt;
                }

                long delay = ffmpeg.swr_get_delay(swr, inRate);
                int maxOut = (int)ffmpeg.av_rescale_rnd(delay + frame->nb_samples,
                    AudioSampleRate, inRate, AVRounding.AV_ROUND_UP);
                int maxBytes = maxOut * AudioChannels * 2;
                if (pcm == null || pcm.Length < maxBytes) pcm = new byte[Math.Max(maxBytes, 4096)];

                int got;
                fixed (byte* pOut = pcm)
                {
                    byte** outArr = stackalloc byte*[1];
                    outArr[0] = pOut;
                    got = ffmpeg.swr_convert(swr, outArr, maxOut, frame->extended_data, frame->nb_samples);
                }
                if (got > 0)
                {
                    int bytes = got * AudioChannels * 2;
                    _audio.Write(pcm, 0, bytes);          // blocks when full -> paces the loop
                }
                ffmpeg.av_frame_unref(frame);
            }
        }

        private void Fail(string message)
        {
            _running = false;
            Failed?.Invoke(message);
        }
    }
}

using System;
using FFmpeg.AutoGen;

namespace AcePlayer.Decoding
{
    /// <summary>
    /// Post-decode video pipeline: optional deinterlace (bwdif) via libavfilter, then colour
    /// conversion to BGRA via swscale, then push to the <see cref="FrameBuffer"/> with PTS.
    ///
    /// bwdif is inserted with deint=interlaced so progressive frames pass through untouched, while
    /// broadcast 1080i/576i content is deinterlaced. send_field mode emits a frame per field
    /// (2x rate) for smooth motion — the sink's time_base carries the corrected timing.
    /// </summary>
    internal sealed unsafe class VideoConverter : IDisposable
    {
        private const int AV_BUFFERSRC_FLAG_KEEP_REF = 8;

        private readonly FrameBuffer _target;
        private readonly bool _deinterlace;
        private readonly int _maxWidth;      // 0 = native; otherwise downscale wider frames

        // filter graph
        private AVFilterGraph* _graph;
        private AVFilterContext* _src;
        private AVFilterContext* _sink;
        private AVFrame* _filt;
        private double _sinkTb;

        // scaler
        private SwsContext* _sws;
        private byte[] _bgra;
        private int _inW, _inH; private AVPixelFormat _inFmt = AVPixelFormat.AV_PIX_FMT_NONE;
        private int _scaleW, _scaleH; private AVPixelFormat _scaleFmt = AVPixelFormat.AV_PIX_FMT_NONE;
        private int _outW, _outH;

        public event Action<int, int> SizeChanged;
        public int Width { get; private set; }
        public int Height { get; private set; }

        public VideoConverter(FrameBuffer target, bool deinterlace, int maxWidth = 0)
        {
            _target = target;
            _deinterlace = deinterlace;
            _maxWidth = maxWidth;
            _filt = ffmpeg.av_frame_alloc();
        }

        /// <summary>Feed a freshly decoded frame; emits one or more BGRA frames to the buffer.</summary>
        public void Process(AVFrame* frame, AVRational streamTimeBase)
        {
            int w = frame->width, h = frame->height;
            var fmt = (AVPixelFormat)frame->format;
            if (w <= 0 || h <= 0) return;

            if (_graph == null || w != _inW || h != _inH || fmt != _inFmt)
                InitGraph(frame, streamTimeBase);

            // Use the input frame's PTS directly. bwdif rescales its output time_base, which yields
            // a wrong presentation cadence; since send_frame emits exactly one frame per input, the
            // decoder's PTS is the correct one to carry through.
            double inPts = PtsOf(frame->best_effort_timestamp, ffmpeg.av_q2d(streamTimeBase));

            if (_graph == null)
            {
                ScaleAndPush(frame, inPts);   // filter unavailable — direct conversion
                return;
            }

            if (ffmpeg.av_buffersrc_add_frame_flags(_src, frame, AV_BUFFERSRC_FLAG_KEEP_REF) < 0)
                return;

            while (true)
            {
                int r = ffmpeg.av_buffersink_get_frame(_sink, _filt);
                if (r < 0) break;   // EAGAIN / EOF
                ScaleAndPush(_filt, inPts);
                ffmpeg.av_frame_unref(_filt);
            }
        }

        private static double PtsOf(long ts, double tb)
            => (ts == ffmpeg.AV_NOPTS_VALUE || tb <= 0) ? double.NaN : ts * tb;

        private void InitGraph(AVFrame* frame, AVRational streamTimeBase)
        {
            DisposeGraph();
            _inW = frame->width; _inH = frame->height; _inFmt = (AVPixelFormat)frame->format;

            if (!_deinterlace) { _graph = null; return; }

            _graph = ffmpeg.avfilter_graph_alloc();
            AVFilter* bufsrc = ffmpeg.avfilter_get_by_name("buffer");
            AVFilter* bufsink = ffmpeg.avfilter_get_by_name("buffersink");
            if (_graph == null || bufsrc == null || bufsink == null) { DisposeGraph(); return; }

            int sarNum = frame->sample_aspect_ratio.num > 0 ? frame->sample_aspect_ratio.num : 1;
            int sarDen = frame->sample_aspect_ratio.den > 0 ? frame->sample_aspect_ratio.den : 1;
            string args =
                $"video_size={_inW}x{_inH}:pix_fmt={(int)_inFmt}:" +
                $"time_base={streamTimeBase.num}/{streamTimeBase.den}:pixel_aspect={sarNum}/{sarDen}";

            AVFilterContext* src = null, sink = null;
            if (ffmpeg.avfilter_graph_create_filter(&src, bufsrc, "in", args, null, _graph) < 0 ||
                ffmpeg.avfilter_graph_create_filter(&sink, bufsink, "out", null, null, _graph) < 0)
            { DisposeGraph(); return; }
            _src = src; _sink = sink;

            AVFilterInOut* outputs = ffmpeg.avfilter_inout_alloc();
            AVFilterInOut* inputs = ffmpeg.avfilter_inout_alloc();
            outputs->name = ffmpeg.av_strdup("in"); outputs->filter_ctx = _src; outputs->pad_idx = 0; outputs->next = null;
            inputs->name = ffmpeg.av_strdup("out"); inputs->filter_ctx = _sink; inputs->pad_idx = 0; inputs->next = null;

            // send_frame keeps the source frame rate (1 out per input) — half the cost of send_field
            // and enough for smooth playback given the CPU-side scaler/blit at 1080p.
            const string spec = "bwdif=mode=send_frame:parity=auto:deint=interlaced";
            int pr = ffmpeg.avfilter_graph_parse_ptr(_graph, spec, &inputs, &outputs, null);
            ffmpeg.avfilter_inout_free(&inputs);
            ffmpeg.avfilter_inout_free(&outputs);
            if (pr < 0 || ffmpeg.avfilter_graph_config(_graph, null) < 0) { DisposeGraph(); return; }

            _sinkTb = ffmpeg.av_q2d(ffmpeg.av_buffersink_get_time_base(_sink));
        }

        private void ScaleAndPush(AVFrame* frame, double pts)
        {
            int w = frame->width, h = frame->height;
            var fmt = (AVPixelFormat)frame->format;
            if (w <= 0 || h <= 0) return;

            if (_sws == null || w != _scaleW || h != _scaleH || fmt != _scaleFmt)
            {
                int outW = w, outH = h;
                if (_maxWidth > 0 && w > _maxWidth)
                {
                    outW = _maxWidth;
                    outH = (int)Math.Round(h * (double)_maxWidth / w) & ~1;   // keep even
                }
                if (_sws != null) ffmpeg.sws_freeContext(_sws);
                _sws = ffmpeg.sws_getContext(w, h, fmt, outW, outH, AVPixelFormat.AV_PIX_FMT_BGRA,
                    ffmpeg.SWS_BILINEAR, null, null, null);
                _scaleW = w; _scaleH = h; _scaleFmt = fmt;
                _outW = outW; _outH = outH;
                _bgra = new byte[outW * outH * 4];
                Width = outW; Height = outH;
                SizeChanged?.Invoke(outW, outH);
            }
            if (_sws == null) return;

            var srcData = new byte_ptrArray4();
            var srcLine = new int_array4();
            for (uint i = 0; i < 4; i++) { srcData[i] = frame->data[i]; srcLine[i] = frame->linesize[i]; }

            fixed (byte* pDst = _bgra)
            {
                var dstData = new byte_ptrArray4(); dstData[0] = pDst;
                var dstLine = new int_array4(); dstLine[0] = _outW * 4;
                ffmpeg.sws_scale(_sws, srcData, srcLine, 0, h, dstData, dstLine);
            }
            _target.Push(_bgra, _outW, _outH, double.IsNaN(pts) ? 0 : pts);
        }

        private void DisposeGraph()
        {
            if (_graph != null) { fixed (AVFilterGraph** p = &_graph) ffmpeg.avfilter_graph_free(p); }
            _graph = null; _src = null; _sink = null;
        }

        public void Dispose()
        {
            DisposeGraph();
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_filt != null) { fixed (AVFrame** p = &_filt) ffmpeg.av_frame_free(p); }
        }
    }
}

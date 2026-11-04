using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using ClipClip.Engine.Core;
using ClipClip.Engine.Media;
using FFmpeg.AutoGen;

namespace ClipClip.Engine.Editing;

public enum ExportAspect { Original, Vertical, Square }
public enum CaptionPlacement { Top, Middle, Bottom }

/// <summary>One piece of the timeline: a range of a source clip with its own speed, volume and caption.</summary>
public sealed class EditSegment
{
    public required string SourcePath { get; init; }
    public double In { get; set; }
    public double Out { get; set; }
    public double Speed { get; set; } = 1;
    public float Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public string? Caption { get; set; }
    public CaptionPlacement Placement { get; set; } = CaptionPlacement.Bottom;

    public double OutputSeconds => (Out - In) / Speed;

    public EditSegment Clone() => new()
    {
        SourcePath = SourcePath, In = In, Out = Out, Speed = Speed, Volume = Volume,
        Muted = Muted, Caption = Caption, Placement = Placement,
    };
}

public sealed class EditProject
{
    public List<EditSegment> Segments { get; } = [];
    public ExportAspect Aspect { get; set; }
    public string? MusicPath { get; set; }
    public float MusicVolume { get; set; } = 0.5f;
    public int Fps { get; set; } = 60;
}

/// <summary>
/// Renders a project to a new MP4. Unlike trimming this re-encodes, because speed changes,
/// captions, reframing and joining clips of different sizes all need new frames. Frames are
/// decoded, cropped and scaled on the CPU, captions are drawn with GDI+, and encoding uses the
/// hardware encoder when one opens. Changing speed also changes audio pitch.
/// </summary>
public static unsafe class Exporter
{
    private const int SampleRate = 48000;
    private const int AudioFrameSize = 1024;

    public static string Export(EditProject project, string directory, string baseName, IProgress<double>? progress, CancellationToken token)
    {
        if (project.Segments.Count == 0) throw new InvalidOperationException("Add at least one clip to the timeline.");
        FF.EnsureLoaded();

        string target = Path.Combine(directory, baseName + ".mp4");
        for (int i = 2; File.Exists(target); i++) target = Path.Combine(directory, $"{baseName}_{i}.mp4");
        string temp = Path.Combine(directory, $".clipclip-tmp-{Guid.NewGuid():N}.mp4");
        try
        {
            double total = new Render(project, temp, progress, token).Run();
            ClipProbe probe = ClipProbe.Read(temp, decodeFirstFrame: true);
            if (!probe.HasDecodableVideo || Math.Abs(probe.DurationSeconds - total) > 1.5)
                throw new InvalidDataException("The exported video failed verification.");
            File.Move(temp, target, false);
            return target;
        }
        catch
        {
            try { File.Delete(temp); }
            catch (IOException) { /* Removed on the next start. */ }
            throw;
        }
    }

    private sealed class Render
    {
        private readonly EditProject _project;
        private readonly string _path;
        private readonly IProgress<double>? _progress;
        private readonly CancellationToken _token;
        private readonly int _width;
        private readonly int _height;
        private readonly long _totalTicks;

        private AVFormatContext* _output;
        private AVCodecContext* _video;
        private AVCodecContext* _audio;
        private AVStream* _videoStream;
        private AVStream* _audioStream;
        private AVFrame* _videoFrame;
        private AVFrame* _audioFrame;
        private AVPacket* _packet;
        private long _tick;
        private long _audioWritten;
        private readonly List<float> _pendingAudio = new(SampleRate * 4);
        private float[] _music = [];

        public Render(EditProject project, string path, IProgress<double>? progress, CancellationToken token)
        {
            _project = project;
            _path = path;
            _progress = progress;
            _token = token;
            (_width, _height) = CanvasSize(project);
            double seconds = project.Segments.Sum(s => s.OutputSeconds);
            _totalTicks = Math.Max(1, (long)Math.Round(seconds * project.Fps));
        }

        private static (int, int) CanvasSize(EditProject project)
        {
            switch (project.Aspect)
            {
                case ExportAspect.Vertical: return (1080, 1920);
                case ExportAspect.Square: return (1080, 1080);
            }
            ClipProbe first = ClipProbe.Read(project.Segments[0].SourcePath, false);
            int height = Math.Min(1080, first.Height) & ~1;
            int width = (int)Math.Round((double)first.Width * height / first.Height) & ~1;
            return (width, height);
        }

        public double Run()
        {
            _packet = ffmpeg.av_packet_alloc();
            AVDictionary* options = null;
            bool headerWritten = false;
            try
            {
                AVFormatContext* output = null;
                FF.Check(ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", _path), "Create MP4");
                _output = output;
                OpenVideoEncoder();
                OpenAudioEncoder();
                ffmpeg.av_dict_set(&_output->metadata, "encoder", "ClipClip Studio", 0);
                FF.Check(ffmpeg.avio_open(&_output->pb, _path, ffmpeg.AVIO_FLAG_WRITE), "Open output");
                ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0);
                FF.Check(ffmpeg.avformat_write_header(_output, &options), "Write header");
                headerWritten = true;

                double totalSeconds = _project.Segments.Sum(s => s.OutputSeconds);
                if (!string.IsNullOrEmpty(_project.MusicPath))
                    _music = DecodeAudioFile(_project.MusicPath, (int)(totalSeconds * SampleRate) + SampleRate);

                double start = 0;
                foreach (EditSegment segment in _project.Segments)
                {
                    RenderSegment(segment, start);
                    start += segment.OutputSeconds;
                }

                EncodeAudio(flush: true);
                Drain(_video, _videoStream, flush: true);
                Drain(_audio, _audioStream, flush: true);
                headerWritten = false;
                FF.Check(ffmpeg.av_write_trailer(_output), "Finish MP4");
                FF.Check(_output->pb->error, "Finish MP4 file");
                _progress?.Report(1);
                return _tick / (double)_project.Fps;
            }
            finally
            {
                if (headerWritten) ffmpeg.av_write_trailer(_output);
                ffmpeg.av_dict_free(&options);
                fixed (AVCodecContext** video = &_video) ffmpeg.avcodec_free_context(video);
                fixed (AVCodecContext** audio = &_audio) ffmpeg.avcodec_free_context(audio);
                fixed (AVFrame** frame = &_videoFrame) ffmpeg.av_frame_free(frame);
                fixed (AVFrame** frame = &_audioFrame) ffmpeg.av_frame_free(frame);
                fixed (AVPacket** packet = &_packet) ffmpeg.av_packet_free(packet);
                if (_output != null)
                {
                    if (_output->pb != null) ffmpeg.avio_closep(&_output->pb);
                    ffmpeg.avformat_free_context(_output);
                }
            }
        }

        private void OpenVideoEncoder()
        {
            long bitRate = Math.Clamp((long)(_width * (double)_height * _project.Fps * 0.1), 4_000_000, 40_000_000);
            foreach (string name in new[] { "h264_nvenc", "h264_amf", "h264_qsv", "libx264", "h264_mf" })
            {
                AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(name);
                if (codec == null) continue;
                AVCodecContext* context = ffmpeg.avcodec_alloc_context3(codec);
                context->width = _width;
                context->height = _height;
                context->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
                context->time_base = new AVRational { num = 1, den = _project.Fps };
                context->framerate = new AVRational { num = _project.Fps, den = 1 };
                context->gop_size = _project.Fps * 2;
                context->max_b_frames = 0;
                context->bit_rate = bitRate;
                context->rc_max_rate = bitRate * 3 / 2;
                context->rc_buffer_size = (int)Math.Min(int.MaxValue, bitRate * 2);
                context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
                context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
                context->colorspace = AVColorSpace.AVCOL_SPC_BT709;
                context->color_range = AVColorRange.AVCOL_RANGE_MPEG;
                context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
                if (name == "libx264") FF.SetOption(context->priv_data, "preset", "veryfast");
                if (name == "h264_nvenc") FF.SetOption(context->priv_data, "preset", "p5");
                if (ffmpeg.avcodec_open2(context, codec, null) >= 0)
                {
                    _video = context;
                    Log.Info($"Studio export with {name} at {_width}x{_height}");
                    break;
                }
                ffmpeg.avcodec_free_context(&context);
            }
            if (_video == null) throw new InvalidOperationException("No H.264 encoder could be opened.");

            _videoStream = ffmpeg.avformat_new_stream(_output, null);
            FF.Check(ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _video), "Video stream");
            _videoStream->time_base = _video->time_base;
            _videoStream->avg_frame_rate = _video->framerate;

            _videoFrame = ffmpeg.av_frame_alloc();
            _videoFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            _videoFrame->width = _width;
            _videoFrame->height = _height;
            FF.Check(ffmpeg.av_frame_get_buffer(_videoFrame, 32), "Video frame");
        }

        private void OpenAudioEncoder()
        {
            AVCodec* codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
            _audio = ffmpeg.avcodec_alloc_context3(codec);
            _audio->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            _audio->sample_rate = SampleRate;
            _audio->bit_rate = 192_000;
            _audio->time_base = new AVRational { num = 1, den = SampleRate };
            _audio->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            ffmpeg.av_channel_layout_default(&_audio->ch_layout, 2);
            FF.Check(ffmpeg.avcodec_open2(_audio, codec, null), "Open AAC");

            _audioStream = ffmpeg.avformat_new_stream(_output, null);
            FF.Check(ffmpeg.avcodec_parameters_from_context(_audioStream->codecpar, _audio), "Audio stream");
            _audioStream->time_base = _audio->time_base;

            _audioFrame = ffmpeg.av_frame_alloc();
            _audioFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            _audioFrame->nb_samples = AudioFrameSize;
            _audioFrame->sample_rate = SampleRate;
            ffmpeg.av_channel_layout_default(&_audioFrame->ch_layout, 2);
            FF.Check(ffmpeg.av_frame_get_buffer(_audioFrame, 0), "Audio frame");
        }

        private void RenderSegment(EditSegment segment, double startSeconds)
        {
            AVFormatContext* input = null;
            AVCodecContext* videoDecoder = null;
            AVCodecContext* audioDecoder = null;
            AVFrame* decoded = ffmpeg.av_frame_alloc();
            AVFrame* previous = ffmpeg.av_frame_alloc();
            AVPacket* packet = ffmpeg.av_packet_alloc();
            FrameStyler? styler = null;
            AudioConverter? converter = null;
            try
            {
                FF.Check(ffmpeg.avformat_open_input(&input, segment.SourcePath, null, null), "Open " + Path.GetFileName(segment.SourcePath));
                FF.Check(ffmpeg.avformat_find_stream_info(input, null), "Read streams");
                int videoIndex = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                FF.Check(videoIndex, "Find video");
                int audioIndex = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
                videoDecoder = OpenDecoder(input->streams[videoIndex]);
                if (audioIndex >= 0) audioDecoder = OpenDecoder(input->streams[audioIndex]);

                AVRational videoBase = input->streams[videoIndex]->time_base;
                long seekTo = (long)(segment.In / ffmpeg.av_q2d(videoBase));
                ffmpeg.av_seek_frame(input, videoIndex, seekTo, ffmpeg.AVSEEK_FLAG_BACKWARD);

                int sourceFrames = Math.Max(1, (int)Math.Round((segment.Out - segment.In) * SampleRate));
                float[] segmentAudio = new float[sourceFrames * 2];
                long endTick = (long)Math.Round((startSeconds + segment.OutputSeconds) * _project.Fps);
                bool havePrevious = false, converted = false, videoDone = false, audioDone = audioIndex < 0;
                AVFormatContext* source = input;
                AVFrame* last = previous;

                void OnVideo(AVFrame* frame)
                {
                    double time = Seconds(frame, source->streams[videoIndex]);
                    if (time >= segment.Out)
                    {
                        videoDone = true;
                        return;
                    }
                    if (time >= segment.In)
                    {
                        double mapped = startSeconds + (time - segment.In) / segment.Speed;
                        if (!havePrevious)
                        {
                            ffmpeg.av_frame_ref(last, frame);
                            havePrevious = true;
                            converted = false;
                        }
                        while (_tick < endTick && _tick / (double)_project.Fps < mapped)
                            Emit(last, ref styler, segment, ref converted);
                    }
                    ffmpeg.av_frame_unref(last);
                    ffmpeg.av_frame_ref(last, frame);
                    havePrevious = true;
                    converted = false;
                }

                void OnAudio(AVFrame* frame)
                {
                    double time = Seconds(frame, source->streams[audioIndex]);
                    if (time >= segment.Out)
                    {
                        audioDone = true;
                        return;
                    }
                    converter ??= new AudioConverter();
                    int count = converter.Convert(frame, out float[] samples);
                    int offset = (int)Math.Round((time - segment.In) * SampleRate);
                    for (int i = 0; i < count; i++)
                    {
                        int slot = offset + i;
                        if (slot < 0 || slot >= sourceFrames) continue;
                        segmentAudio[slot * 2] = samples[i * 2];
                        segmentAudio[slot * 2 + 1] = samples[i * 2 + 1];
                    }
                }

                while (!(videoDone && audioDone))
                {
                    _token.ThrowIfCancellationRequested();
                    if (ffmpeg.av_read_frame(input, packet) < 0) break;
                    if (packet->stream_index == videoIndex && !videoDone) Decode(videoDecoder, packet, decoded, OnVideo);
                    else if (packet->stream_index == audioIndex && !audioDone) Decode(audioDecoder, packet, decoded, OnAudio);
                    ffmpeg.av_packet_unref(packet);
                }
                if (!videoDone) Decode(videoDecoder, null, decoded, OnVideo);

                if (!havePrevious) throw new InvalidDataException("No video in part of " + Path.GetFileName(segment.SourcePath));
                while (_tick < endTick) Emit(previous, ref styler, segment, ref converted);

                AppendAudio(segmentAudio, segment);
                EncodeAudio(flush: false);
            }
            finally
            {
                styler?.Dispose();
                converter?.Dispose();
                ffmpeg.av_packet_free(&packet);
                ffmpeg.av_frame_free(&previous);
                ffmpeg.av_frame_free(&decoded);
                ffmpeg.avcodec_free_context(&videoDecoder);
                ffmpeg.avcodec_free_context(&audioDecoder);
                ffmpeg.avformat_close_input(&input);
            }
        }

        private void Emit(AVFrame* source, ref FrameStyler? styler, EditSegment segment, ref bool converted)
        {
            if (!converted)
            {
                FF.Check(ffmpeg.av_frame_make_writable(_videoFrame), "Prepare frame");
                styler ??= new FrameStyler(source->width, source->height, _width, _height, segment.Caption, segment.Placement);
                styler.Draw(source, _videoFrame);
                converted = true;
            }
            _videoFrame->pts = _tick++;
            FF.Check(ffmpeg.avcodec_send_frame(_video, _videoFrame), "Encode frame");
            Drain(_video, _videoStream, flush: false);
            if (_tick % 15 == 0) _progress?.Report(Math.Min(0.99, _tick / (double)_totalTicks));
        }

        private void AppendAudio(float[] source, EditSegment segment)
        {
            int inFrames = source.Length / 2;
            int outFrames = (int)Math.Round(segment.OutputSeconds * SampleRate);
            float gain = segment.Muted ? 0 : segment.Volume;
            for (int i = 0; i < outFrames; i++)
            {
                // Linear resampling: faster pieces play higher, slower ones lower.
                double position = i * segment.Speed;
                int index = (int)position;
                double fraction = position - index;
                for (int channel = 0; channel < 2; channel++)
                {
                    float a = index < inFrames ? source[index * 2 + channel] : 0;
                    float b = index + 1 < inFrames ? source[(index + 1) * 2 + channel] : a;
                    _pendingAudio.Add((float)(a + (b - a) * fraction) * gain);
                }
            }
        }

        private void EncodeAudio(bool flush)
        {
            int available = _pendingAudio.Count / 2;
            int used = 0;
            while (available - used >= AudioFrameSize || (flush && available - used > 0))
            {
                FF.Check(ffmpeg.av_frame_make_writable(_audioFrame), "Prepare audio");
                float* left = (float*)_audioFrame->data[0];
                float* right = (float*)_audioFrame->data[1];
                for (int i = 0; i < AudioFrameSize; i++)
                {
                    int frame = used + i;
                    long global = _audioWritten + i;
                    float musicLeft = 0, musicRight = 0;
                    if (global * 2 + 1 < _music.Length)
                    {
                        musicLeft = _music[global * 2] * _project.MusicVolume;
                        musicRight = _music[global * 2 + 1] * _project.MusicVolume;
                    }
                    float l = frame < available ? _pendingAudio[frame * 2] : 0;
                    float r = frame < available ? _pendingAudio[frame * 2 + 1] : 0;
                    left[i] = Math.Clamp(l + musicLeft, -1f, 1f);
                    right[i] = Math.Clamp(r + musicRight, -1f, 1f);
                }
                _audioFrame->pts = _audioWritten;
                FF.Check(ffmpeg.avcodec_send_frame(_audio, _audioFrame), "Encode audio");
                Drain(_audio, _audioStream, flush: false);
                _audioWritten += AudioFrameSize;
                used += AudioFrameSize;
            }
            _pendingAudio.RemoveRange(0, Math.Min(_pendingAudio.Count, used * 2));
        }

        private void Drain(AVCodecContext* encoder, AVStream* stream, bool flush)
        {
            if (flush) ffmpeg.avcodec_send_frame(encoder, null);
            while (ffmpeg.avcodec_receive_packet(encoder, _packet) >= 0)
            {
                ffmpeg.av_packet_rescale_ts(_packet, encoder->time_base, stream->time_base);
                _packet->stream_index = stream->index;
                FF.Check(ffmpeg.av_interleaved_write_frame(_output, _packet), "Write packet");
            }
        }
    }

    private delegate void FrameHandler(AVFrame* frame);

    private static void Decode(AVCodecContext* decoder, AVPacket* packet, AVFrame* frame, FrameHandler handler)
    {
        if (ffmpeg.avcodec_send_packet(decoder, packet) < 0) return;
        while (ffmpeg.avcodec_receive_frame(decoder, frame) >= 0)
        {
            handler(frame);
            ffmpeg.av_frame_unref(frame);
        }
    }

    private static double Seconds(AVFrame* frame, AVStream* stream)
    {
        long pts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
        return pts * ffmpeg.av_q2d(stream->time_base);
    }

    private static AVCodecContext* OpenDecoder(AVStream* stream)
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null) throw new NotSupportedException("This clip uses a format ClipClip cannot decode.");
        AVCodecContext* context = ffmpeg.avcodec_alloc_context3(codec);
        FF.Check(ffmpeg.avcodec_parameters_to_context(context, stream->codecpar), "Decoder settings");
        context->pkt_timebase = stream->time_base;
        context->thread_count = 0;
        FF.Check(ffmpeg.avcodec_open2(context, codec, null), "Open decoder");
        return context;
    }

    /// <summary>Decodes any audio file FFmpeg reads into 48 kHz stereo float, up to a length.</summary>
    private static float[] DecodeAudioFile(string path, int maxFrames)
    {
        AVFormatContext* input = null;
        AVCodecContext* decoder = null;
        AVFrame* frame = ffmpeg.av_frame_alloc();
        AVPacket* packet = ffmpeg.av_packet_alloc();
        var result = new List<float>(Math.Min(maxFrames, SampleRate * 60) * 2);
        using var converter = new AudioConverter();
        try
        {
            FF.Check(ffmpeg.avformat_open_input(&input, path, null, null), "Open music");
            FF.Check(ffmpeg.avformat_find_stream_info(input, null), "Read music");
            int index = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            FF.Check(index, "Find audio in the music file");
            decoder = OpenDecoder(input->streams[index]);
            void Collect(AVFrame* decoded)
            {
                int count = converter.Convert(decoded, out float[] samples);
                for (int i = 0; i < count * 2 && result.Count < maxFrames * 2; i++) result.Add(samples[i]);
            }
            while (result.Count < maxFrames * 2 && ffmpeg.av_read_frame(input, packet) >= 0)
            {
                if (packet->stream_index == index) Decode(decoder, packet, frame, Collect);
                ffmpeg.av_packet_unref(packet);
            }
            return result.ToArray();
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&decoder);
            ffmpeg.avformat_close_input(&input);
        }
    }

    private sealed class AudioConverter : IDisposable
    {
        private SwrContext* _swr;
        private float[] _buffer = new float[SampleRate * 2];

        public int Convert(AVFrame* frame, out float[] samples)
        {
            if (_swr == null)
            {
                AVChannelLayout outLayout;
                ffmpeg.av_channel_layout_default(&outLayout, 2);
                SwrContext* swr = null;
                FF.Check(ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, SampleRate,
                    &frame->ch_layout, (AVSampleFormat)frame->format, frame->sample_rate, 0, null), "Audio converter");
                FF.Check(ffmpeg.swr_init(swr), "Audio converter");
                _swr = swr;
            }
            int capacity = (int)((long)frame->nb_samples * SampleRate / Math.Max(1, frame->sample_rate)) + 256;
            if (_buffer.Length < capacity * 2) _buffer = new float[capacity * 2];
            samples = _buffer;
            fixed (float* output = _buffer)
            {
                byte* outputBytes = (byte*)output;
                int count = ffmpeg.swr_convert(_swr, &outputBytes, capacity, frame->extended_data, frame->nb_samples);
                return Math.Max(0, count);
            }
        }

        public void Dispose()
        {
            fixed (SwrContext** swr = &_swr) ffmpeg.swr_free(swr);
        }
    }

    /// <summary>Crops a decoded frame to the output shape, scales it, and draws the caption.</summary>
    private sealed class FrameStyler : IDisposable
    {
        private readonly int _outWidth, _outHeight;
        private readonly int _cropX, _cropY, _cropWidth, _cropHeight;
        private readonly SwsContext* _toYuv;
        private readonly AVFrame* _yuv;
        private readonly SwsContext* _toOutput;
        private readonly SwsContext* _fromCanvas;
        private readonly byte* _canvas;
        private readonly int _canvasStride;
        private readonly Bitmap? _canvasBitmap;
        private readonly Graphics? _canvasGraphics;
        private readonly Bitmap? _caption;

        public FrameStyler(int sourceWidth, int sourceHeight, int outWidth, int outHeight, string? caption, CaptionPlacement placement)
        {
            _outWidth = outWidth;
            _outHeight = outHeight;

            // Cover the output: keep the centre of the source at the output's shape.
            double target = (double)outWidth / outHeight;
            if ((double)sourceWidth / sourceHeight > target)
            {
                _cropHeight = sourceHeight & ~1;
                _cropWidth = (int)Math.Round(sourceHeight * target) & ~1;
            }
            else
            {
                _cropWidth = sourceWidth & ~1;
                _cropHeight = (int)Math.Round(sourceWidth / target) & ~1;
            }
            _cropX = ((sourceWidth - _cropWidth) / 2) & ~1;
            _cropY = ((sourceHeight - _cropHeight) / 2) & ~1;

            _yuv = ffmpeg.av_frame_alloc();
            _yuv->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            _yuv->width = sourceWidth;
            _yuv->height = sourceHeight;
            FF.Check(ffmpeg.av_frame_get_buffer(_yuv, 32), "Frame buffer");
            _toYuv = null;
            _sourceWidth = sourceWidth;
            _sourceHeight = sourceHeight;

            const int flags = (int)SwsFlags.SWS_BICUBIC;
            if (string.IsNullOrWhiteSpace(caption))
            {
                _toOutput = ffmpeg.sws_getContext(_cropWidth, _cropHeight, AVPixelFormat.AV_PIX_FMT_YUV420P,
                    outWidth, outHeight, AVPixelFormat.AV_PIX_FMT_NV12, flags, null, null, null);
                return;
            }

            _canvasStride = outWidth * 4;
            _canvas = (byte*)ffmpeg.av_malloc((ulong)(_canvasStride * outHeight));
            _toOutput = ffmpeg.sws_getContext(_cropWidth, _cropHeight, AVPixelFormat.AV_PIX_FMT_YUV420P,
                outWidth, outHeight, AVPixelFormat.AV_PIX_FMT_BGRA, flags, null, null, null);
            _fromCanvas = ffmpeg.sws_getContext(outWidth, outHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
                outWidth, outHeight, AVPixelFormat.AV_PIX_FMT_NV12, flags, null, null, null);
            _canvasBitmap = new Bitmap(outWidth, outHeight, _canvasStride, PixelFormat.Format32bppArgb, (nint)_canvas);
            _canvasGraphics = Graphics.FromImage(_canvasBitmap);
            _caption = RenderCaption(caption!, placement, outWidth, outHeight);
        }

        private readonly int _sourceWidth, _sourceHeight;
        private SwsContext* _converter;

        private static Bitmap RenderCaption(string text, CaptionPlacement placement, int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
            float size = Math.Min(width, height) * 0.075f;
            FontFamily family = FontFamily.Families.FirstOrDefault(f => f.Name == "Bahnschrift") ?? FontFamily.GenericSansSerif;
            var format = new StringFormat { Alignment = StringAlignment.Center };
            RectangleF area = placement switch
            {
                CaptionPlacement.Top => new RectangleF(width * 0.05f, height * 0.06f, width * 0.9f, height * 0.3f),
                CaptionPlacement.Middle => new RectangleF(width * 0.05f, 0, width * 0.9f, height),
                _ => new RectangleF(width * 0.05f, height * 0.64f, width * 0.9f, height * 0.3f),
            };
            format.LineAlignment = placement switch
            {
                CaptionPlacement.Top => StringAlignment.Near,
                CaptionPlacement.Middle => StringAlignment.Center,
                _ => StringAlignment.Far,
            };
            using var path = new GraphicsPath();
            path.AddString(text, family, (int)FontStyle.Bold, size, area, format);
            using var outline = new Pen(Color.FromArgb(235, 0, 0, 0), size * 0.16f) { LineJoin = LineJoin.Round };
            graphics.DrawPath(outline, path);
            graphics.FillPath(Brushes.White, path);
            return bitmap;
        }

        public void Draw(AVFrame* source, AVFrame* destination)
        {
            AVFrame* yuv = source;
            if (source->format != (int)AVPixelFormat.AV_PIX_FMT_YUV420P && source->format != (int)AVPixelFormat.AV_PIX_FMT_YUVJ420P)
            {
                if (_converter == null)
                {
                    _converter = ffmpeg.sws_getContext(_sourceWidth, _sourceHeight, (AVPixelFormat)source->format,
                        _sourceWidth, _sourceHeight, AVPixelFormat.AV_PIX_FMT_YUV420P, (int)SwsFlags.SWS_BILINEAR, null, null, null);
                }
                FF.Check(ffmpeg.av_frame_make_writable(_yuv), "Frame buffer");
                ffmpeg.sws_scale(_converter, source->data, source->linesize, 0, _sourceHeight, _yuv->data, _yuv->linesize);
                yuv = _yuv;
            }

            byte*[] cropped =
            [
                yuv->data[0] + _cropY * yuv->linesize[0] + _cropX,
                yuv->data[1] + _cropY / 2 * yuv->linesize[1] + _cropX / 2,
                yuv->data[2] + _cropY / 2 * yuv->linesize[2] + _cropX / 2,
            ];
            int[] strides = [yuv->linesize[0], yuv->linesize[1], yuv->linesize[2]];

            if (_canvasBitmap == null)
            {
                ffmpeg.sws_scale(_toOutput, cropped, strides, 0, _cropHeight, destination->data, destination->linesize);
                return;
            }

            ffmpeg.sws_scale(_toOutput, cropped, strides, 0, _cropHeight, [_canvas], [_canvasStride]);
            _canvasGraphics!.DrawImageUnscaled(_caption!, 0, 0);
            ffmpeg.sws_scale(_fromCanvas, [_canvas], [_canvasStride], 0, _outHeight, destination->data, destination->linesize);
        }

        public void Dispose()
        {
            _canvasGraphics?.Dispose();
            _canvasBitmap?.Dispose();
            _caption?.Dispose();
            if (_canvas != null) ffmpeg.av_free(_canvas);
            ffmpeg.sws_freeContext(_toOutput);
            ffmpeg.sws_freeContext(_fromCanvas);
            ffmpeg.sws_freeContext(_converter);
            AVFrame* yuv = _yuv;
            ffmpeg.av_frame_free(&yuv);
        }
    }
}

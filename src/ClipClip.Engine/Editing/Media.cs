using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using ClipClip.Engine.Core;
using ClipClip.Engine.Media;
using FFmpeg.AutoGen;

namespace ClipClip.Engine.Editing;

public sealed record MediaDetails(int Width, int Height, double Duration, bool HasVideo, bool HasAudio);

/// <summary>Reads a video file frame by frame and can jump to any time. Used by preview, export and thumbnails.</summary>
public sealed unsafe class VideoSource : IDisposable
{
    private AVFormatContext* _input;
    private AVCodecContext* _decoder;
    private readonly int _index;
    private readonly AVRational _timeBase;
    private AVPacket* _packet;
    private AVFrame* _current;
    private AVFrame* _pending;
    private bool _hasCurrent, _hasPending, _endOfFile;
    private double _currentTime, _pendingTime;

    public VideoSource(string path)
    {
        FF.EnsureLoaded();
        AVFormatContext* input = null;
        FF.Check(ffmpeg.avformat_open_input(&input, path, null, null), "Open " + Path.GetFileName(path));
        _input = input;
        FF.Check(ffmpeg.avformat_find_stream_info(_input, null), "Read streams");
        _index = ffmpeg.av_find_best_stream(_input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        FF.Check(_index, "Find video");
        AVStream* stream = _input->streams[_index];
        _timeBase = stream->time_base;
        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        _decoder = ffmpeg.avcodec_alloc_context3(codec);
        FF.Check(ffmpeg.avcodec_parameters_to_context(_decoder, stream->codecpar), "Decoder settings");
        _decoder->pkt_timebase = _timeBase;
        _decoder->thread_count = 0;
        FF.Check(ffmpeg.avcodec_open2(_decoder, codec, null), "Open decoder");
        Width = stream->codecpar->width;
        Height = stream->codecpar->height;
        Duration = _input->duration > 0 ? _input->duration / (double)ffmpeg.AV_TIME_BASE : 0;
        _packet = ffmpeg.av_packet_alloc();
        _current = ffmpeg.av_frame_alloc();
        _pending = ffmpeg.av_frame_alloc();
    }

    public int Width { get; }
    public int Height { get; }
    public double Duration { get; }

    /// <summary>The frame showing at <paramref name="time"/>. Valid until the next call.</summary>
    public AVFrame* FrameAt(double time)
    {
        bool far = !_hasCurrent || time < _currentTime - 0.001 || time > _currentTime + 2.5;
        if (far) Seek(time);

        while (true)
        {
            if (_hasPending)
            {
                if (_pendingTime > time + 0.0005) break;
                ffmpeg.av_frame_unref(_current);
                AVFrame* swap = _current;
                _current = _pending;
                _pending = swap;
                _currentTime = _pendingTime;
                _hasCurrent = true;
                _hasPending = false;
                continue;
            }
            if (!DecodeNext(_pending, out _pendingTime)) break;
            _hasPending = true;
        }
        if (_hasCurrent) return _current;
        return _hasPending ? _pending : null;
    }

    private void Seek(double time)
    {
        long target = (long)(Math.Max(0, time) / ffmpeg.av_q2d(_timeBase));
        ffmpeg.av_seek_frame(_input, _index, target, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avcodec_flush_buffers(_decoder);
        ffmpeg.av_frame_unref(_current);
        ffmpeg.av_frame_unref(_pending);
        _hasCurrent = _hasPending = _endOfFile = false;
    }

    private bool DecodeNext(AVFrame* into, out double time)
    {
        time = 0;
        while (true)
        {
            int result = ffmpeg.avcodec_receive_frame(_decoder, into);
            if (result >= 0)
            {
                long pts = into->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? into->best_effort_timestamp : into->pts;
                time = pts * ffmpeg.av_q2d(_timeBase);
                return true;
            }
            if (result == ffmpeg.AVERROR_EOF || _endOfFile) return false;

            while (true)
            {
                if (ffmpeg.av_read_frame(_input, _packet) < 0)
                {
                    ffmpeg.avcodec_send_packet(_decoder, null);
                    _endOfFile = true;
                    break;
                }
                bool mine = _packet->stream_index == _index;
                if (mine) ffmpeg.avcodec_send_packet(_decoder, _packet);
                ffmpeg.av_packet_unref(_packet);
                if (mine) break;
            }
        }
    }

    public void Dispose()
    {
        AVFrame* current = _current, pending = _pending;
        ffmpeg.av_frame_free(&current);
        ffmpeg.av_frame_free(&pending);
        AVPacket* packet = _packet;
        ffmpeg.av_packet_free(&packet);
        AVCodecContext* decoder = _decoder;
        ffmpeg.avcodec_free_context(&decoder);
        AVFormatContext* input = _input;
        ffmpeg.avformat_close_input(&input);
        _current = _pending = null;
    }
}

public static unsafe class MediaInfo
{
    public static MediaDetails Probe(string path)
    {
        FF.EnsureLoaded();
        AVFormatContext* input = null;
        FF.Check(ffmpeg.avformat_open_input(&input, path, null, null), "Open " + Path.GetFileName(path));
        try
        {
            FF.Check(ffmpeg.avformat_find_stream_info(input, null), "Read streams");
            int video = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            int audio = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            int width = video >= 0 ? input->streams[video]->codecpar->width : 0;
            int height = video >= 0 ? input->streams[video]->codecpar->height : 0;
            double duration = input->duration > 0 ? input->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            return new MediaDetails(width, height, duration, video >= 0, audio >= 0);
        }
        finally
        {
            ffmpeg.avformat_close_input(&input);
        }
    }

    /// <summary>Small BGRA frames at the given times, for the timeline filmstrip.</summary>
    public static List<byte[]> Filmstrip(string path, IReadOnlyList<double> times, int height, out int width)
    {
        var frames = new List<byte[]>(times.Count);
        using var source = new VideoSource(path);
        width = Math.Max(2, (int)Math.Round((double)source.Width * height / source.Height));
        SwsContext* scaler = null;
        try
        {
            foreach (double time in times)
            {
                AVFrame* frame = source.FrameAt(time);
                if (frame == null) break;
                scaler = ffmpeg.sws_getCachedContext(scaler, frame->width, frame->height, (AVPixelFormat)frame->format,
                    width, height, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);
                byte[] pixels = new byte[width * height * 4];
                fixed (byte* destination = pixels)
                {
                    ffmpeg.sws_scale(scaler, frame->data, frame->linesize, 0, frame->height, [destination], [width * 4]);
                }
                frames.Add(pixels);
            }
        }
        finally
        {
            ffmpeg.sws_freeContext(scaler);
        }
        return frames;
    }

    /// <summary>Loudest sample in each 1/<paramref name="perSecond"/> of a second, for waveforms.</summary>
    public static float[] Peaks(string path, int perSecond)
    {
        float[] samples = AudioFile.Decode(path, 0, 0);
        int bucket = AudioFile.Rate / perSecond;
        int count = samples.Length / 2 / bucket + 1;
        var peaks = new float[count];
        for (int i = 0; i < samples.Length / 2; i++)
        {
            float value = Math.Max(Math.Abs(samples[i * 2]), Math.Abs(samples[i * 2 + 1]));
            int index = i / bucket;
            if (value > peaks[index]) peaks[index] = value;
        }
        return peaks;
    }
}

/// <summary>Decodes the audio of any file FFmpeg reads into 48 kHz stereo float.</summary>
public static unsafe class AudioFile
{
    public const int Rate = 48000;

    /// <summary><paramref name="seconds"/> of 0 or less reads to the end.</summary>
    public static float[] Decode(string path, double from, double seconds)
    {
        FF.EnsureLoaded();
        AVFormatContext* input = null;
        AVCodecContext* decoder = null;
        AVFrame* frame = ffmpeg.av_frame_alloc();
        AVPacket* packet = ffmpeg.av_packet_alloc();
        SwrContext* swr = null;
        bool bounded = seconds > 0;
        int frames = bounded ? (int)Math.Round(seconds * Rate) : 0;
        float[] fixedBuffer = bounded ? new float[frames * 2] : [];
        var open = new List<float>(bounded ? 0 : Rate * 2 * 60);
        float[] scratch = new float[Rate * 2];
        try
        {
            if (ffmpeg.avformat_open_input(&input, path, null, null) < 0) return fixedBuffer;
            ffmpeg.avformat_find_stream_info(input, null);
            int index = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (index < 0) return fixedBuffer;
            AVStream* stream = input->streams[index];
            AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec == null) return fixedBuffer;
            decoder = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(decoder, stream->codecpar);
            decoder->pkt_timebase = stream->time_base;
            if (ffmpeg.avcodec_open2(decoder, codec, null) < 0) return fixedBuffer;
            if (from > 0) ffmpeg.av_seek_frame(input, index, (long)(from / ffmpeg.av_q2d(stream->time_base)), ffmpeg.AVSEEK_FLAG_BACKWARD);

            bool done = false;
            while (!done && ffmpeg.av_read_frame(input, packet) >= 0)
            {
                if (packet->stream_index == index && ffmpeg.avcodec_send_packet(decoder, packet) >= 0)
                {
                    while (ffmpeg.avcodec_receive_frame(decoder, frame) >= 0)
                    {
                        long pts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
                        double time = pts == ffmpeg.AV_NOPTS_VALUE ? from : pts * ffmpeg.av_q2d(stream->time_base);
                        if (swr == null)
                        {
                            AVChannelLayout outLayout;
                            ffmpeg.av_channel_layout_default(&outLayout, 2);
                            FF.Check(ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, Rate,
                                &frame->ch_layout, (AVSampleFormat)frame->format, frame->sample_rate, 0, null), "Audio converter");
                            FF.Check(ffmpeg.swr_init(swr), "Audio converter");
                        }
                        int capacity = (int)((long)frame->nb_samples * Rate / Math.Max(1, frame->sample_rate)) + 256;
                        if (scratch.Length < capacity * 2) scratch = new float[capacity * 2];
                        int count;
                        fixed (float* output = scratch)
                        {
                            byte* outputBytes = (byte*)output;
                            count = ffmpeg.swr_convert(swr, &outputBytes, capacity, frame->extended_data, frame->nb_samples);
                        }
                        int offset = (int)Math.Round((time - from) * Rate);
                        if (bounded)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                int slot = offset + i;
                                if (slot < 0) continue;
                                if (slot >= frames)
                                {
                                    done = true;
                                    break;
                                }
                                fixedBuffer[slot * 2] = scratch[i * 2];
                                fixedBuffer[slot * 2 + 1] = scratch[i * 2 + 1];
                            }
                        }
                        else
                        {
                            for (int i = 0; i < count * 2; i++) open.Add(scratch[i]);
                        }
                        ffmpeg.av_frame_unref(frame);
                    }
                }
                ffmpeg.av_packet_unref(packet);
            }
            return bounded ? fixedBuffer : open.ToArray();
        }
        catch (Exception ex)
        {
            Log.Warn($"Audio from {Path.GetFileName(path)} unreadable: {ex.Message}");
            return bounded ? fixedBuffer : open.ToArray();
        }
        finally
        {
            ffmpeg.swr_free(&swr);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&decoder);
            ffmpeg.avformat_close_input(&input);
        }
    }
}

/// <summary>Built-in gamer stickers, drawn once and kept as PNG files.</summary>
public static class StickerLibrary
{
    private static readonly (string Text, Color Fill, Color Ink, float Tilt)[] Designs =
    [
        ("GG", Color.FromArgb(124, 58, 237), Color.White, -6),
        ("LOL", Color.FromArgb(250, 204, 21), Color.Black, 5),
        ("WOW", Color.FromArgb(236, 72, 153), Color.White, -4),
        ("CLUTCH", Color.FromArgb(34, 197, 94), Color.Black, 3),
        ("EZ", Color.FromArgb(14, 165, 233), Color.White, -8),
        ("OOF", Color.FromArgb(239, 68, 68), Color.White, 6),
        ("NO WAY", Color.FromArgb(249, 115, 22), Color.Black, -3),
        ("W", Color.FromArgb(255, 255, 255), Color.FromArgb(124, 58, 237), 0),
    ];

    public static IReadOnlyList<string> EnsureBuiltIns()
    {
        string directory = Path.Combine(AppPaths.DataDir, "stickers");
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        foreach ((string text, Color fill, Color ink, float tilt) in Designs)
        {
            string path = Path.Combine(directory, text.Replace(' ', '_') + ".png");
            if (!File.Exists(path)) Draw(text, fill, ink, tilt, path);
            paths.Add(path);
        }
        return paths;
    }

    private static void Draw(string text, Color fill, Color ink, float tilt, string path)
    {
        using var bitmap = new Bitmap(512, 320, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        graphics.TranslateTransform(256, 160);
        graphics.RotateTransform(tilt);
        FontFamily family = FontFamily.Families.FirstOrDefault(f => f.Name == "Bahnschrift") ?? FontFamily.GenericSansSerif;
        float size = text.Length > 4 ? 92 : 132;
        using var textPath = new GraphicsPath();
        textPath.AddString(text, family, (int)FontStyle.Bold, size, new PointF(0, 0),
            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        RectangleF bounds = textPath.GetBounds();
        bounds.Inflate(34, 22);
        using var bubble = Rounded(bounds, 48);
        using (var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
        {
            graphics.TranslateTransform(8, 10);
            graphics.FillPath(shadow, bubble);
            graphics.TranslateTransform(-8, -10);
        }
        using (var brush = new SolidBrush(fill)) graphics.FillPath(brush, bubble);
        using (var border = new Pen(Color.White, 10)) graphics.DrawPath(border, bubble);
        using (var brush = new SolidBrush(ink)) graphics.FillPath(brush, textPath);
        bitmap.Save(path, ImageFormat.Png);
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

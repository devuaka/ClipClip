using ClipClip.Engine.Core;
using ClipClip.Engine.Media;
using FFmpeg.AutoGen;

namespace ClipClip.Engine.Editing;

/// <summary>Renders a project to MP4, and builds its audio mix for export and preview playback.</summary>
public static unsafe class Exporter
{
    private const int Rate = AudioFile.Rate;
    private const int AudioFrameSize = 1024;

    /// <summary>Output size. <paramref name="shortSide"/> caps the shorter edge: 1080 for export, less for preview.</summary>
    public static (int Width, int Height) CanvasSize(EditorProject project, int shortSide = 1080)
    {
        int width = 1920, height = 1080;
        if (project.Aspect == ExportAspect.Vertical) (width, height) = (1080, 1920);
        else if (project.Aspect == ExportAspect.Square) (width, height) = (1080, 1080);
        else if (project.Video.Count > 0)
        {
            try
            {
                MediaDetails first = MediaInfo.Probe(project.Video[0].SourcePath);
                if (first.Width > 0) (width, height) = (first.Width, first.Height);
            }
            catch (Exception)
            {
                // Fall back to 16:9.
            }
        }
        double scale = Math.Min(1.0, shortSide / (double)Math.Min(width, height));
        return ((int)Math.Round(width * scale) & ~1, (int)Math.Round(height * scale) & ~1);
    }

    /// <summary>The whole project's sound, mixed: video pieces, music, sounds and voiceovers.</summary>
    public static float[] MixAudio(EditorProject project, CancellationToken token = default)
    {
        int total = (int)Math.Round(project.Duration * Rate);
        var mix = new float[total * 2];

        foreach ((VideoClip clip, double start) in project.Sequence())
        {
            token.ThrowIfCancellationRequested();
            if (clip.Muted || clip.Volume <= 0) continue;
            float[] source = AudioFile.Decode(clip.SourcePath, clip.In, clip.Out - clip.In);
            if (Math.Abs(clip.Speed - 1) > 0.001)
                source = clip.KeepPitch ? TimeStretch(source, clip.Speed) : Resample(source, clip.Speed);

            int fadeIn = clip.Transition is TransitionKind.Fade ? (int)(clip.TransitionSeconds * Rate) : Rate / 100;
            Mix(mix, source, (int)Math.Round(start * Rate), (float)clip.Volume, fadeIn, Rate / 100);
        }

        foreach (AudioClip clip in project.Audio)
        {
            token.ThrowIfCancellationRequested();
            if (clip.Volume <= 0 || clip.Duration <= 0) continue;
            float[] source = AudioFile.Decode(clip.SourcePath, clip.In, clip.Duration);
            Mix(mix, source, (int)Math.Round(clip.Start * Rate), (float)clip.Volume,
                Math.Max(Rate / 100, (int)(clip.FadeIn * Rate)), Math.Max(Rate / 100, (int)(clip.FadeOut * Rate)));
        }
        return mix;
    }

    private static void Mix(float[] mix, float[] source, int offset, float gain, int fadeIn, int fadeOut)
    {
        int length = source.Length / 2;
        for (int i = 0; i < length; i++)
        {
            int slot = offset + i;
            if (slot < 0) continue;
            if (slot * 2 + 1 >= mix.Length) break;
            float envelope = gain;
            if (i < fadeIn) envelope *= i / (float)fadeIn;
            if (length - i < fadeOut) envelope *= (length - i) / (float)fadeOut;
            mix[slot * 2] += source[i * 2] * envelope;
            mix[slot * 2 + 1] += source[i * 2 + 1] * envelope;
        }
    }

    /// <summary>Plain resampling: faster plays higher, slower plays lower.</summary>
    private static float[] Resample(float[] source, double speed)
    {
        int inFrames = source.Length / 2;
        int outFrames = (int)(inFrames / speed);
        var output = new float[outFrames * 2];
        for (int i = 0; i < outFrames; i++)
        {
            double position = i * speed;
            int index = (int)position;
            double fraction = position - index;
            for (int c = 0; c < 2; c++)
            {
                float a = index < inFrames ? source[index * 2 + c] : 0;
                float b = index + 1 < inFrames ? source[(index + 1) * 2 + c] : a;
                output[i * 2 + c] = (float)(a + (b - a) * fraction);
            }
        }
        return output;
    }

    /// <summary>
    /// WSOLA time stretch: changes speed but keeps pitch, so voices stay normal in slow motion.
    /// Overlapping windows are taken from the source at the new rate, each nudged to where it lines up
    /// best with the previous one so the joins do not warble.
    /// </summary>
    public static float[] TimeStretch(float[] source, double speed)
    {
        const int window = 2048, hop = window / 2, search = 384;
        int inFrames = source.Length / 2;
        int outFrames = (int)(inFrames / speed);
        var output = new float[(outFrames + window) * 2];
        var weights = new float[window];
        for (int i = 0; i < window; i++) weights[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / window);

        float Mono(int index) => index >= 0 && index < inFrames ? source[index * 2] + source[index * 2 + 1] : 0;

        int previous = -1;
        for (int outPosition = 0; outPosition < outFrames; outPosition += hop)
        {
            int target = (int)(outPosition * speed);
            int best = target;
            if (previous >= 0)
            {
                int natural = previous + hop;
                double bestScore = double.MinValue;
                for (int delta = -search; delta <= search; delta += 8)
                {
                    int candidate = target + delta;
                    if (candidate < 0 || candidate + window > inFrames) continue;
                    double score = 0;
                    for (int k = 0; k < hop; k += 4) score += Mono(natural + k) * Mono(candidate + k);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = candidate;
                    }
                }
            }
            for (int k = 0; k < window; k++)
            {
                int read = best + k;
                if (read >= inFrames) break;
                int write = outPosition + k;
                output[write * 2] += source[read * 2] * weights[k];
                output[write * 2 + 1] += source[read * 2 + 1] * weights[k];
            }
            previous = best;
        }
        Array.Resize(ref output, outFrames * 2);
        return output;
    }

    public static string Export(EditorProject project, string directory, string baseName, IProgress<double>? progress, CancellationToken token)
    {
        if (project.Video.Count == 0) throw new InvalidOperationException("Add at least one video to the timeline.");
        FF.EnsureLoaded();
        string target = Path.Combine(directory, baseName + ".mp4");
        for (int i = 2; File.Exists(target); i++) target = Path.Combine(directory, $"{baseName}_{i}.mp4");
        string temp = Path.Combine(directory, $".clipclip-tmp-{Guid.NewGuid():N}.mp4");
        try
        {
            double seconds = Render(project, temp, progress, token);
            ClipProbe probe = ClipProbe.Read(temp, decodeFirstFrame: true);
            if (!probe.HasDecodableVideo || Math.Abs(probe.DurationSeconds - seconds) > 1.5)
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

    private static double Render(EditorProject project, string path, IProgress<double>? progress, CancellationToken token)
    {
        (int width, int height) = CanvasSize(project);
        int fps = project.Fps;
        long frames = Math.Max(1, (long)Math.Ceiling(project.Duration * fps));
        float[] audio = MixAudio(project, token);

        AVFormatContext* output = null;
        AVCodecContext* video = null;
        AVCodecContext* sound = null;
        AVFrame* videoFrame = ffmpeg.av_frame_alloc();
        AVFrame* audioFrame = ffmpeg.av_frame_alloc();
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVDictionary* options = null;
        SwsContext* toNv12 = null;
        bool headerWritten = false;
        using var compositor = new Compositor(width, height, highQuality: true);
        try
        {
            FF.Check(ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", path), "Create MP4");
            video = OpenVideoEncoder(width, height, fps);
            AVStream* videoStream = ffmpeg.avformat_new_stream(output, null);
            FF.Check(ffmpeg.avcodec_parameters_from_context(videoStream->codecpar, video), "Video stream");
            videoStream->time_base = video->time_base;
            videoStream->avg_frame_rate = video->framerate;

            AVCodec* aac = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
            sound = ffmpeg.avcodec_alloc_context3(aac);
            sound->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            sound->sample_rate = Rate;
            sound->bit_rate = 192_000;
            sound->time_base = new AVRational { num = 1, den = Rate };
            sound->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            ffmpeg.av_channel_layout_default(&sound->ch_layout, 2);
            FF.Check(ffmpeg.avcodec_open2(sound, aac, null), "Open AAC");
            AVStream* audioStream = ffmpeg.avformat_new_stream(output, null);
            FF.Check(ffmpeg.avcodec_parameters_from_context(audioStream->codecpar, sound), "Audio stream");
            audioStream->time_base = sound->time_base;

            videoFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            videoFrame->width = width;
            videoFrame->height = height;
            FF.Check(ffmpeg.av_frame_get_buffer(videoFrame, 32), "Video frame");
            audioFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            audioFrame->nb_samples = AudioFrameSize;
            audioFrame->sample_rate = Rate;
            ffmpeg.av_channel_layout_default(&audioFrame->ch_layout, 2);
            FF.Check(ffmpeg.av_frame_get_buffer(audioFrame, 0), "Audio frame");

            ffmpeg.av_dict_set(&output->metadata, "encoder", "ClipClip Studio", 0);
            FF.Check(ffmpeg.avio_open(&output->pb, path, ffmpeg.AVIO_FLAG_WRITE), "Open output");
            ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0);
            FF.Check(ffmpeg.avformat_write_header(output, &options), "Write header");
            headerWritten = true;

            toNv12 = ffmpeg.sws_getContext(width, height, AVPixelFormat.AV_PIX_FMT_BGRA, width, height, AVPixelFormat.AV_PIX_FMT_NV12,
                (int)SwsFlags.SWS_BICUBIC, null, null, null);
            long audioWritten = 0;
            int audioFrames = audio.Length / 2;

            AVFormatContext* muxer = output;
            AVPacket* encoded = packet;
            AVFrame* block = audioFrame;
            AVCodecContext* aacEncoder = sound;

            void Drain(AVCodecContext* encoder, AVStream* stream)
            {
                while (ffmpeg.avcodec_receive_packet(encoder, encoded) >= 0)
                {
                    ffmpeg.av_packet_rescale_ts(encoded, encoder->time_base, stream->time_base);
                    encoded->stream_index = stream->index;
                    FF.Check(ffmpeg.av_interleaved_write_frame(muxer, encoded), "Write packet");
                }
            }

            void WriteAudioUntil(long frameLimit, bool pad)
            {
                while (audioWritten + AudioFrameSize <= frameLimit || (pad && audioWritten < frameLimit))
                {
                    FF.Check(ffmpeg.av_frame_make_writable(block), "Prepare audio");
                    float* left = (float*)block->data[0];
                    float* right = (float*)block->data[1];
                    for (int i = 0; i < AudioFrameSize; i++)
                    {
                        long index = audioWritten + i;
                        left[i] = index < audioFrames ? Math.Clamp(audio[index * 2], -1f, 1f) : 0;
                        right[i] = index < audioFrames ? Math.Clamp(audio[index * 2 + 1], -1f, 1f) : 0;
                    }
                    block->pts = audioWritten;
                    FF.Check(ffmpeg.avcodec_send_frame(aacEncoder, block), "Encode audio");
                    Drain(aacEncoder, audioStream);
                    audioWritten += AudioFrameSize;
                }
            }

            for (long i = 0; i < frames; i++)
            {
                token.ThrowIfCancellationRequested();
                compositor.Render(project, i / (double)fps);
                FF.Check(ffmpeg.av_frame_make_writable(videoFrame), "Prepare frame");
                ffmpeg.sws_scale(toNv12, [compositor.Canvas], [compositor.Stride], 0, height, videoFrame->data, videoFrame->linesize);
                videoFrame->pts = i;
                FF.Check(ffmpeg.avcodec_send_frame(video, videoFrame), "Encode frame");
                Drain(video, videoStream);
                WriteAudioUntil((i + 1) * Rate / fps, false);
                if (i % 10 == 0) progress?.Report(Math.Min(0.99, i / (double)frames));
            }

            WriteAudioUntil(frames * Rate / fps, true);
            ffmpeg.avcodec_send_frame(video, null);
            Drain(video, videoStream);
            ffmpeg.avcodec_send_frame(sound, null);
            Drain(sound, audioStream);
            headerWritten = false;
            FF.Check(ffmpeg.av_write_trailer(output), "Finish MP4");
            FF.Check(output->pb->error, "Finish MP4 file");
            progress?.Report(1);
            return frames / (double)fps;
        }
        finally
        {
            if (headerWritten) ffmpeg.av_write_trailer(output);
            ffmpeg.sws_freeContext(toNv12);
            ffmpeg.av_dict_free(&options);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&videoFrame);
            ffmpeg.av_frame_free(&audioFrame);
            ffmpeg.avcodec_free_context(&video);
            ffmpeg.avcodec_free_context(&sound);
            if (output != null)
            {
                if (output->pb != null) ffmpeg.avio_closep(&output->pb);
                ffmpeg.avformat_free_context(output);
            }
        }
    }

    private static AVCodecContext* OpenVideoEncoder(int width, int height, int fps)
    {
        long bitRate = Math.Clamp((long)(width * (double)height * fps * 0.1), 4_000_000, 40_000_000);
        foreach (string name in new[] { "h264_nvenc", "h264_amf", "h264_qsv", "libx264", "h264_mf" })
        {
            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(name);
            if (codec == null) continue;
            AVCodecContext* context = ffmpeg.avcodec_alloc_context3(codec);
            context->width = width;
            context->height = height;
            context->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
            context->time_base = new AVRational { num = 1, den = fps };
            context->framerate = new AVRational { num = fps, den = 1 };
            context->gop_size = fps * 2;
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
                Log.Info($"Studio export with {name} at {width}x{height}");
                return context;
            }
            ffmpeg.avcodec_free_context(&context);
        }
        throw new InvalidOperationException("No H.264 encoder could be opened.");
    }
}

/// <summary>Renders preview frames on its own thread. Only the newest request is drawn.</summary>
public sealed class PreviewRenderer : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private (EditorProject Project, double Time)? _request;
    private bool _stop;

    public PreviewRenderer()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "ClipClip Preview" };
        _thread.Start();
    }

    /// <summary>Raised on the render thread with the BGRA pixels, size and time of a finished frame.</summary>
    public event Action<byte[], int, int, double>? FrameReady;

    public void Request(EditorProject snapshot, double time)
    {
        lock (_gate) _request = (snapshot, time);
        _signal.Set();
    }

    private void Loop()
    {
        Compositor? compositor = null;
        string? sizeKey = null;
        while (true)
        {
            _signal.WaitOne();
            (EditorProject Project, double Time)? request;
            lock (_gate)
            {
                if (_stop) break;
                request = _request;
                _request = null;
            }
            if (request == null) continue;
            try
            {
                (int width, int height) = Exporter.CanvasSize(request.Value.Project, 540);
                string key = $"{width}x{height}";
                if (key != sizeKey)
                {
                    compositor?.Dispose();
                    compositor = new Compositor(width, height, highQuality: false);
                    sizeKey = key;
                }
                compositor!.Render(request.Value.Project, request.Value.Time);
                byte[] pixels = new byte[compositor.Stride * height];
                compositor.CopyTo(pixels);
                FrameReady?.Invoke(pixels, width, height, request.Value.Time);
            }
            catch (Exception ex)
            {
                Log.Warn("Preview frame failed: " + ex.Message);
            }
        }
        compositor?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate) _stop = true;
        _signal.Set();
        _thread.Join(2000);
    }
}

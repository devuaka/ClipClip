using ClipClip.Engine.Media;
using FFmpeg.AutoGen;

namespace ClipClip.SelfTest;

internal sealed record SyncReport(List<double> Flashes, List<double> SystemBeeps, List<double> MicBeeps);

/// <summary>Decodes a saved clip and measures what is actually in the file.</summary>
internal static unsafe class ClipAnalysis
{
    private const int SampleRate = 48000;

    public static SyncReport Measure(string path)
    {
        var luma = new List<(double Time, double Value)>();
        var audio = new List<float>();
        double audioStart = double.NaN;

        Decode(path,
            (frame, time) =>
            {
                double sum = 0;
                int count = 0;
                for (int y = frame->height / 3; y < frame->height * 2 / 3; y += 8)
                {
                    byte* row = frame->data[0] + y * frame->linesize[0];
                    for (int x = frame->width / 3; x < frame->width * 2 / 3; x += 8)
                    {
                        sum += row[x];
                        count++;
                    }
                }
                luma.Add((time, sum / count));
            },
            (frame, time) =>
            {
                if (double.IsNaN(audioStart)) audioStart = time;
                float* left = (float*)frame->data[0];
                for (int i = 0; i < frame->nb_samples; i++) audio.Add(left[i]);
            });

        var flashes = new List<double>();
        for (int i = 1; i < luma.Count; i++)
        {
            if (luma[i - 1].Value < 120 && luma[i].Value >= 120) flashes.Add(luma[i].Time * 1000);
        }

        return new SyncReport(flashes, Onsets(audio, audioStart, 1000), Onsets(audio, audioStart, 3000));
    }

    /// <summary>Tone onsets from a Goertzel filter over 2.5 ms hops.</summary>
    private static List<double> Onsets(List<float> samples, double start, double frequency)
    {
        const int window = 240;
        const int hop = 120;
        double k = 2 * Math.Cos(2 * Math.PI * frequency / SampleRate);
        var result = new List<double>();
        bool active = false;
        double quietSince = 0;

        for (int offset = 0; offset + window < samples.Count; offset += hop)
        {
            double s0, s1 = 0, s2 = 0;
            for (int i = 0; i < window; i++)
            {
                s0 = samples[offset + i] + k * s1 - s2;
                s2 = s1;
                s1 = s0;
            }
            double power = (s1 * s1 + s2 * s2 - k * s1 * s2) / (window * window);
            double time = start * 1000 + offset * 1000.0 / SampleRate;
            if (power > 0.004)
            {
                if (!active) result.Add(time);
                active = true;
                quietSince = time;
            }
            else if (active && time - quietSince > 200)
            {
                active = false;
            }
        }
        return result;
    }

    /// <summary>For each event in a, the nearest event in b within half a period.</summary>
    public static List<double> Offsets(List<double> a, List<double> b)
    {
        var result = new List<double>();
        foreach (double time in a)
        {
            if (b.Count == 0) break;
            double nearest = b.MinBy(other => Math.Abs(other - time));
            if (Math.Abs(nearest - time) < 400) result.Add(nearest - time);
        }
        return result;
    }

    public static (double R, double G, double B) AverageColor(string path)
    {
        double y = 0, u = 0, v = 0;
        int frames = 0;
        Decode(path,
            (frame, _) =>
            {
                if (frames >= 30) return;
                y += frame->data[0][frame->height / 2 * frame->linesize[0] + frame->width / 2];
                u += frame->data[1][frame->height / 4 * frame->linesize[1] + frame->width / 4];
                v += frame->data[2][frame->height / 4 * frame->linesize[2] + frame->width / 4];
                frames++;
            },
            (_, _) => { });
        y = y / frames - 16;
        u = u / frames - 128;
        v = v / frames - 128;
        return (1.164 * y + 1.793 * v, 1.164 * y - 0.213 * u - 0.533 * v, 1.164 * y + 2.112 * u);
    }

    private delegate void FrameHandler(AVFrame* frame, double seconds);

    private static void Decode(string path, FrameHandler onVideo, FrameHandler onAudio)
    {
        AVFormatContext* input = null;
        FF.Check(ffmpeg.avformat_open_input(&input, path, null, null), "open");
        FF.Check(ffmpeg.avformat_find_stream_info(input, null), "info");
        int videoIndex = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        int audioIndex = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);

        AVCodecContext* video = OpenDecoder(input->streams[videoIndex]);
        AVCodecContext* audio = audioIndex >= 0 ? OpenDecoder(input->streams[audioIndex]) : null;
        AVFrame* frame = ffmpeg.av_frame_alloc();
        AVPacket* packet = ffmpeg.av_packet_alloc();

        while (ffmpeg.av_read_frame(input, packet) >= 0)
        {
            if (packet->stream_index == videoIndex) Pump(video, packet, frame, input->streams[videoIndex], onVideo);
            else if (packet->stream_index == audioIndex) Pump(audio, packet, frame, input->streams[audioIndex], onAudio);
            ffmpeg.av_packet_unref(packet);
        }
        Pump(video, null, frame, input->streams[videoIndex], onVideo);

        ffmpeg.av_packet_free(&packet);
        ffmpeg.av_frame_free(&frame);
        ffmpeg.avcodec_free_context(&video);
        ffmpeg.avcodec_free_context(&audio);
        ffmpeg.avformat_close_input(&input);
    }

    private static AVCodecContext* OpenDecoder(AVStream* stream)
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        AVCodecContext* context = ffmpeg.avcodec_alloc_context3(codec);
        FF.Check(ffmpeg.avcodec_parameters_to_context(context, stream->codecpar), "params");
        context->pkt_timebase = stream->time_base;
        FF.Check(ffmpeg.avcodec_open2(context, codec, null), "open codec");
        return context;
    }

    private static void Pump(AVCodecContext* context, AVPacket* packet, AVFrame* frame, AVStream* stream, FrameHandler handler)
    {
        ffmpeg.avcodec_send_packet(context, packet);
        while (ffmpeg.avcodec_receive_frame(context, frame) >= 0)
        {
            double seconds = frame->best_effort_timestamp * ffmpeg.av_q2d(stream->time_base);
            handler(frame, seconds);
        }
    }
}

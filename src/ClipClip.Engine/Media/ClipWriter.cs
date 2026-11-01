using ClipClip.Engine.Core;
using FFmpeg.AutoGen;

namespace ClipClip.Engine.Media;

public sealed class VideoStreamInfo
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Fps { get; init; }
    public required long BitRate { get; init; }
    public required byte[] ExtraData { get; init; }
}

public sealed class AudioStreamInfo
{
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required long BitRate { get; init; }
    public required byte[] ExtraData { get; init; }
}

/// <summary>
/// Remuxes ring packets to MP4 without re-encoding. The file is written under a temporary name,
/// then reopened and a frame is decoded from it. Only a verified file is renamed into place, so
/// a failed save can never leave a broken .mp4 in the library.
/// </summary>
public static unsafe class ClipWriter
{
    private const int MicrosecondsPerSecond = 1_000_000;
    private static readonly AVRational MicrosecondBase = new() { num = 1, den = MicrosecondsPerSecond };

    public static void Write(PacketRing ring, ClipPlan plan, VideoStreamInfo video, AudioStreamInfo? audio,
        string finalPath, string? gameName)
    {
        string directory = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $".clipclip-tmp-{Guid.NewGuid():N}.mp4");
        try
        {
            Mux(ring, plan, video, audio, tempPath, gameName);
            Verify(tempPath, plan.DurationSeconds);
            File.Move(tempPath, finalPath, false);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static void CleanTempFiles(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (string file in Directory.EnumerateFiles(directory, ".clipclip-tmp-*.mp4"))
                TryDelete(file);
        }
        catch (Exception ex)
        {
            Log.Warn("Temp clip cleanup failed: " + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* Removed on the next start. */ }
    }

    private static void Mux(PacketRing ring, ClipPlan plan, VideoStreamInfo video, AudioStreamInfo? audio,
        string path, string? gameName)
    {
        FF.EnsureLoaded();
        AVFormatContext* output = null;
        AVDictionary* options = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        bool headerWritten = false;
        try
        {
            FF.Check(ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", path), "Create MP4 context");

            byte[] extra = video.ExtraData.Length > 0 ? video.ExtraData : ExtractParameterSets(ring, plan.Video[0]);
            AVStream* videoStream = ffmpeg.avformat_new_stream(output, null);
            videoStream->time_base = new AVRational { num = 1, den = 90000 };
            videoStream->avg_frame_rate = new AVRational { num = video.Fps, den = 1 };
            AVCodecParameters* videoPar = videoStream->codecpar;
            videoPar->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            videoPar->codec_id = AVCodecID.AV_CODEC_ID_H264;
            videoPar->width = video.Width;
            videoPar->height = video.Height;
            videoPar->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            videoPar->bit_rate = video.BitRate;
            videoPar->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            videoPar->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            videoPar->color_space = AVColorSpace.AVCOL_SPC_BT709;
            videoPar->color_range = AVColorRange.AVCOL_RANGE_MPEG;
            SetExtraData(videoPar, extra);

            AVStream* audioStream = null;
            if (audio != null && plan.Audio.Length > 0)
            {
                audioStream = ffmpeg.avformat_new_stream(output, null);
                audioStream->time_base = new AVRational { num = 1, den = audio.SampleRate };
                AVCodecParameters* audioPar = audioStream->codecpar;
                audioPar->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
                audioPar->codec_id = AVCodecID.AV_CODEC_ID_AAC;
                audioPar->sample_rate = audio.SampleRate;
                audioPar->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
                audioPar->bit_rate = audio.BitRate;
                audioPar->frame_size = 1024;
                ffmpeg.av_channel_layout_default(&audioPar->ch_layout, audio.Channels);
                SetExtraData(audioPar, audio.ExtraData);
            }

            ffmpeg.av_dict_set(&output->metadata, "encoder", "ClipClip", 0);
            ffmpeg.av_dict_set(&output->metadata, "creation_time",
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"), 0);
            if (!string.IsNullOrEmpty(gameName))
                ffmpeg.av_dict_set(&output->metadata, "comment", ClipTags.Encode(gameName), 0);

            FF.Check(ffmpeg.avio_open(&output->pb, path, ffmpeg.AVIO_FLAG_WRITE), "Open clip file");
            ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0);
            FF.Check(ffmpeg.avformat_write_header(output, &options), "Write MP4 header");
            headerWritten = true;

            long baseUs = plan.StartUs;
            int v = 0, a = 0;
            int audioCount = audioStream == null ? 0 : plan.Audio.Length;
            while (v < plan.Video.Length || a < audioCount)
            {
                bool takeVideo = a >= audioCount ||
                                 (v < plan.Video.Length && plan.Video[v].PtsUs <= plan.Audio[a].PtsUs);
                if (takeVideo)
                {
                    ref PacketMeta meta = ref plan.Video[v];
                    long nextUs = v + 1 < plan.Video.Length ? plan.Video[v + 1].PtsUs : meta.PtsUs + MicrosecondsPerSecond / video.Fps;
                    WritePacket(output, packet, ring, meta, videoStream, baseUs, nextUs - meta.PtsUs);
                    v++;
                }
                else
                {
                    ref PacketMeta meta = ref plan.Audio[a];
                    long durationUs = 1024L * MicrosecondsPerSecond / audio!.SampleRate;
                    WritePacket(output, packet, ring, meta, audioStream, baseUs, durationUs);
                    a++;
                }
            }

            FF.Check(ffmpeg.av_interleaved_write_frame(output, null), "Flush clip");
            headerWritten = false;
            FF.Check(ffmpeg.av_write_trailer(output), "Finish MP4");
            FF.Check(output->pb->error, "Finish MP4 file");
        }
        finally
        {
            if (headerWritten) ffmpeg.av_write_trailer(output);
            if (output != null)
            {
                if (output->pb != null) ffmpeg.avio_closep(&output->pb);
                ffmpeg.avformat_free_context(output);
            }
            ffmpeg.av_dict_free(&options);
            ffmpeg.av_packet_free(&packet);
        }
    }

    private static void WritePacket(AVFormatContext* output, AVPacket* packet, PacketRing ring, in PacketMeta meta,
        AVStream* stream, long baseUs, long durationUs)
    {
        FF.Check(ffmpeg.av_new_packet(packet, meta.Size), "Allocate packet");
        if (!ring.TryRead(meta, packet->data))
        {
            ffmpeg.av_packet_unref(packet);
            throw new IOException("Buffered packets were overwritten before they could be saved.");
        }

        long pts = ffmpeg.av_rescale_q(Math.Max(0, meta.PtsUs - baseUs), MicrosecondBase, stream->time_base);
        packet->pts = pts;
        packet->dts = pts;
        packet->duration = ffmpeg.av_rescale_q(Math.Max(1, durationUs), MicrosecondBase, stream->time_base);
        packet->stream_index = stream->index;
        packet->flags = meta.Key ? ffmpeg.AV_PKT_FLAG_KEY : 0;
        FF.Check(ffmpeg.av_interleaved_write_frame(output, packet), "Write packet");
    }

    private static void SetExtraData(AVCodecParameters* parameters, byte[] data)
    {
        if (data.Length == 0) return;
        parameters->extradata = (byte*)ffmpeg.av_mallocz((ulong)(data.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        parameters->extradata_size = data.Length;
        data.CopyTo(new Span<byte>(parameters->extradata, data.Length));
    }

    /// <summary>Some encoders only put SPS/PPS in-band. MP4 needs them in the header, so they are
    /// lifted from the Annex B keyframe.</summary>
    private static byte[] ExtractParameterSets(PacketRing ring, in PacketMeta key)
    {
        byte[] data = new byte[key.Size];
        fixed (byte* pointer = data)
        {
            if (!ring.TryRead(key, pointer))
                throw new IOException("Keyframe was overwritten before it could be saved.");
        }

        var result = new List<byte>();
        int position = 0;
        while (TryFindStartCode(data, position, out int start, out int prefix))
        {
            bool found = TryFindStartCode(data, start + prefix, out int next, out _);
            int end = found ? next : data.Length;
            int nalType = data[start + prefix] & 0x1F;
            if (nalType is 7 or 8)
                result.AddRange(data.AsSpan(start, end - start).ToArray());
            if (nalType == 5) break;
            if (!found) break;
            position = next;
        }
        if (result.Count == 0) throw new InvalidDataException("Encoder output has no SPS/PPS.");
        return result.ToArray();
    }

    private static bool TryFindStartCode(byte[] data, int from, out int start, out int prefix)
    {
        for (int i = from; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            if (data[i + 2] == 1)
            {
                start = i;
                prefix = 3;
                return true;
            }
            if (data[i + 2] == 0 && data[i + 3] == 1)
            {
                start = i;
                prefix = 4;
                return true;
            }
        }
        start = prefix = 0;
        return false;
    }

    private static void Verify(string path, double expectedSeconds)
    {
        ClipProbe probe = ClipProbe.Read(path, decodeFirstFrame: true);
        if (!probe.HasDecodableVideo)
            throw new InvalidDataException("Saved clip failed verification: no decodable video frame.");
        if (probe.DurationSeconds < expectedSeconds - 1.5 || probe.DurationSeconds > expectedSeconds + 1.5)
            throw new InvalidDataException(
                $"Saved clip failed verification: duration {probe.DurationSeconds:F2}s, expected {expectedSeconds:F2}s.");
    }
}

/// <summary>The game name travels inside the MP4, so it survives moves and renames.</summary>
public static class ClipTags
{
    private const string Prefix = "ClipClip game=";

    public static string Encode(string game) => Prefix + game;

    public static string? Decode(string? comment) =>
        comment != null && comment.StartsWith(Prefix, StringComparison.Ordinal) ? comment[Prefix.Length..] : null;
}

public sealed class ClipProbe
{
    public double DurationSeconds { get; private init; }
    public int Width { get; private init; }
    public int Height { get; private init; }
    public bool HasAudio { get; private init; }
    public bool HasDecodableVideo { get; private init; }
    public string? Game { get; private init; }

    public static unsafe ClipProbe Read(string path, bool decodeFirstFrame)
    {
        FF.EnsureLoaded();
        AVFormatContext* input = null;
        FF.Check(ffmpeg.avformat_open_input(&input, path, null, null), "Open clip");
        try
        {
            FF.Check(ffmpeg.avformat_find_stream_info(input, null), "Read clip streams");
            int videoIndex = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            int audioIndex = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (videoIndex < 0) throw new InvalidDataException("Clip has no video stream.");

            AVCodecParameters* parameters = input->streams[videoIndex]->codecpar;
            AVDictionaryEntry* comment = ffmpeg.av_dict_get(input->metadata, "comment", null, 0);
            return new ClipProbe
            {
                DurationSeconds = input->duration > 0 ? input->duration / (double)ffmpeg.AV_TIME_BASE : 0,
                Width = parameters->width,
                Height = parameters->height,
                HasAudio = audioIndex >= 0,
                Game = comment != null ? ClipTags.Decode(FF.Utf8(comment->value)) : null,
                HasDecodableVideo = decodeFirstFrame && DecodeOneFrame(input, videoIndex),
            };
        }
        finally
        {
            ffmpeg.avformat_close_input(&input);
        }
    }

    private static unsafe bool DecodeOneFrame(AVFormatContext* input, int videoIndex)
    {
        AVCodecParameters* parameters = input->streams[videoIndex]->codecpar;
        AVCodec* decoder = ffmpeg.avcodec_find_decoder(parameters->codec_id);
        if (decoder == null) return false;

        AVCodecContext* context = ffmpeg.avcodec_alloc_context3(decoder);
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            if (ffmpeg.avcodec_parameters_to_context(context, parameters) < 0) return false;
            context->thread_count = 1;
            if (ffmpeg.avcodec_open2(context, decoder, null) < 0) return false;

            for (int read = 0; read < 240 && ffmpeg.av_read_frame(input, packet) >= 0; read++)
            {
                bool isVideo = packet->stream_index == videoIndex;
                if (isVideo) ffmpeg.avcodec_send_packet(context, packet);
                ffmpeg.av_packet_unref(packet);
                if (isVideo && ffmpeg.avcodec_receive_frame(context, frame) >= 0) return true;
            }
            ffmpeg.avcodec_send_packet(context, null);
            return ffmpeg.avcodec_receive_frame(context, frame) >= 0;
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&context);
        }
    }
}

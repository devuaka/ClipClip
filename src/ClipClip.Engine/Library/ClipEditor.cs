using ClipClip.Engine.Media;
using FFmpeg.AutoGen;

namespace ClipClip.Engine.Library;

/// <summary>
/// Saves part of a clip as a new MP4 without re-encoding, so it is instant and loses no quality.
/// Video has to start on a keyframe, so the cut starts at the last keyframe at or before the
/// requested start. ClipClip records one keyframe a second, which keeps that within a second.
/// </summary>
public static unsafe class ClipEditor
{
    private const int MicrosecondsPerSecond = 1_000_000;
    private static readonly AVRational Microseconds = new() { num = 1, den = MicrosecondsPerSecond };

    public static string Trim(string sourcePath, double startSeconds, double endSeconds, bool removeAudio)
    {
        FF.EnsureLoaded();
        string directory = Path.GetDirectoryName(sourcePath)!;
        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        string target = Path.Combine(directory, stem + "_trim.mp4");
        for (int i = 2; File.Exists(target); i++)
            target = Path.Combine(directory, $"{stem}_trim{i}.mp4");

        string temp = Path.Combine(directory, $".clipclip-tmp-{Guid.NewGuid():N}.mp4");
        try
        {
            double kept = Remux(sourcePath, temp, startSeconds, endSeconds, removeAudio);
            ClipProbe probe = ClipProbe.Read(temp, decodeFirstFrame: true);
            if (!probe.HasDecodableVideo || Math.Abs(probe.DurationSeconds - kept) > 1.5)
                throw new InvalidDataException("The trimmed clip failed verification.");
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

    private static double Remux(string sourcePath, string targetPath, double startSeconds, double endSeconds, bool removeAudio)
    {
        AVFormatContext* input = null;
        AVFormatContext* output = null;
        AVDictionary* options = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        bool headerWritten = false;
        try
        {
            FF.Check(ffmpeg.avformat_open_input(&input, sourcePath, null, null), "Open clip");
            FF.Check(ffmpeg.avformat_find_stream_info(input, null), "Read clip streams");
            int videoIndex = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            FF.Check(videoIndex, "Find video");
            int audioIndex = removeAudio ? -1 : ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);

            FF.Check(ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", targetPath), "Create MP4 context");
            AVStream* videoOut = CopyStream(output, input->streams[videoIndex]);
            AVStream* audioOut = audioIndex >= 0 ? CopyStream(output, input->streams[audioIndex]) : null;
            ffmpeg.av_dict_copy(&output->metadata, input->metadata, 0);

            FF.Check(ffmpeg.avio_open(&output->pb, targetPath, ffmpeg.AVIO_FLAG_WRITE), "Open new clip");
            ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0);
            FF.Check(ffmpeg.avformat_write_header(output, &options), "Write MP4 header");
            headerWritten = true;

            AVRational videoBase = input->streams[videoIndex]->time_base;
            long startUs = (long)(startSeconds * MicrosecondsPerSecond);
            long endUs = (long)(endSeconds * MicrosecondsPerSecond);
            FF.Check(ffmpeg.av_seek_frame(input, videoIndex, ffmpeg.av_rescale_q(startUs, Microseconds, videoBase),
                ffmpeg.AVSEEK_FLAG_BACKWARD), "Seek in clip");

            long baseUs = -1;
            long lastVideoUs = 0;
            bool videoDone = false;
            bool audioDone = audioIndex < 0;
            while (!(videoDone && audioDone) && ffmpeg.av_read_frame(input, packet) >= 0)
            {
                int index = packet->stream_index;
                AVRational sourceBase = input->streams[index]->time_base;
                long timeUs = ffmpeg.av_rescale_q(packet->pts, sourceBase, Microseconds);
                AVStream* destination = null;

                if (index == videoIndex)
                {
                    if (baseUs < 0 && (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0) baseUs = timeUs;
                    if (timeUs > endUs) videoDone = true;
                    else if (baseUs >= 0)
                    {
                        destination = videoOut;
                        lastVideoUs = timeUs;
                    }
                }
                else if (index == audioIndex)
                {
                    if (timeUs > endUs) audioDone = true;
                    else if (baseUs >= 0 && timeUs >= baseUs) destination = audioOut;
                }

                if (destination != null)
                {
                    long offset = ffmpeg.av_rescale_q(baseUs, Microseconds, sourceBase);
                    packet->pts -= offset;
                    packet->dts -= offset;
                    ffmpeg.av_packet_rescale_ts(packet, sourceBase, destination->time_base);
                    packet->pos = -1;
                    packet->stream_index = destination->index;
                    FF.Check(ffmpeg.av_interleaved_write_frame(output, packet), "Write packet");
                }
                ffmpeg.av_packet_unref(packet);
            }

            if (baseUs < 0) throw new InvalidDataException("No video found in that part of the clip.");
            headerWritten = false;
            FF.Check(ffmpeg.av_write_trailer(output), "Finish MP4");
            FF.Check(output->pb->error, "Finish MP4 file");
            return (lastVideoUs - baseUs) / (double)MicrosecondsPerSecond;
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
            ffmpeg.avformat_close_input(&input);
        }
    }

    private static AVStream* CopyStream(AVFormatContext* output, AVStream* source)
    {
        AVStream* stream = ffmpeg.avformat_new_stream(output, null);
        FF.Check(ffmpeg.avcodec_parameters_copy(stream->codecpar, source->codecpar), "Copy stream settings");
        stream->codecpar->codec_tag = 0;
        stream->time_base = source->time_base;
        stream->avg_frame_rate = source->avg_frame_rate;
        return stream;
    }
}

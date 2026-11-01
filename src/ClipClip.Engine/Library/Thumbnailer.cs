using ClipClip.Engine.Media;
using FFmpeg.AutoGen;

namespace ClipClip.Engine.Library;

/// <summary>Decodes one frame near the start of a clip and writes a small JPEG.</summary>
public static unsafe class Thumbnailer
{
    private const int ThumbWidth = 480;

    public static void Create(string videoPath, string jpegPath, double atSeconds = 0)
    {
        FF.EnsureLoaded();
        AVFormatContext* input = null;
        FF.Check(ffmpeg.avformat_open_input(&input, videoPath, null, null), "Open clip");
        AVCodecContext* decoder = null;
        AVFrame* frame = ffmpeg.av_frame_alloc();
        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            FF.Check(ffmpeg.avformat_find_stream_info(input, null), "Read streams");
            int index = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            FF.Check(index, "Find video");

            AVStream* stream = input->streams[index];
            AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            decoder = ffmpeg.avcodec_alloc_context3(codec);
            FF.Check(ffmpeg.avcodec_parameters_to_context(decoder, stream->codecpar), "Decoder parameters");
            decoder->thread_count = 2;
            FF.Check(ffmpeg.avcodec_open2(decoder, codec, null), "Open decoder");

            if (atSeconds > 0)
            {
                long target = (long)(atSeconds * ffmpeg.AV_TIME_BASE);
                ffmpeg.av_seek_frame(input, -1, target, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(decoder);
            }

            if (!ReadFrame(input, decoder, packet, frame, index))
                throw new InvalidDataException("No frame could be decoded.");

            WriteJpeg(frame, jpegPath);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&decoder);
            ffmpeg.avformat_close_input(&input);
        }
    }

    private static bool ReadFrame(AVFormatContext* input, AVCodecContext* decoder, AVPacket* packet, AVFrame* frame, int index)
    {
        while (ffmpeg.av_read_frame(input, packet) >= 0)
        {
            bool isVideo = packet->stream_index == index;
            if (isVideo) ffmpeg.avcodec_send_packet(decoder, packet);
            ffmpeg.av_packet_unref(packet);
            if (isVideo && ffmpeg.avcodec_receive_frame(decoder, frame) >= 0) return true;
        }
        ffmpeg.avcodec_send_packet(decoder, null);
        return ffmpeg.avcodec_receive_frame(decoder, frame) >= 0;
    }

    private static void WriteJpeg(AVFrame* source, string path)
    {
        int width = Math.Min(ThumbWidth, source->width);
        int height = Math.Max(2, (int)Math.Round((double)source->height * width / source->width) & ~1);
        width &= ~1;

        AVCodec* codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG);
        AVCodecContext* encoder = ffmpeg.avcodec_alloc_context3(codec);
        AVFrame* scaled = ffmpeg.av_frame_alloc();
        AVPacket* packet = ffmpeg.av_packet_alloc();
        SwsContext* scaler = null;
        try
        {
            encoder->width = width;
            encoder->height = height;
            encoder->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            encoder->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            encoder->time_base = new AVRational { num = 1, den = 25 };
            encoder->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
            encoder->global_quality = 5 * ffmpeg.FF_QP2LAMBDA;
            FF.Check(ffmpeg.avcodec_open2(encoder, codec, null), "Open JPEG encoder");

            scaled->format = (int)encoder->pix_fmt;
            scaled->width = width;
            scaled->height = height;
            scaled->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            scaled->quality = encoder->global_quality;
            FF.Check(ffmpeg.av_frame_get_buffer(scaled, 0), "Allocate thumbnail");

            scaler = ffmpeg.sws_getContext(source->width, source->height, (AVPixelFormat)source->format,
                width, height, encoder->pix_fmt, (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (scaler == null) throw new InvalidOperationException("Could not create the thumbnail scaler.");

            // Clips are studio range BT.709. JPEG expects full range.
            int* coefficients = ffmpeg.sws_getCoefficients((int)ffmpeg.SWS_CS_ITU709);
            var table = new int_array4();
            for (uint i = 0; i < 4; i++) table[i] = coefficients[i];
            ffmpeg.sws_setColorspaceDetails(scaler, table, 0, table, 1, 0, 1 << 16, 1 << 16);

            ffmpeg.sws_scale(scaler, source->data, source->linesize, 0, source->height, scaled->data, scaled->linesize);
            scaled->pts = 0;

            FF.Check(ffmpeg.avcodec_send_frame(encoder, scaled), "Encode thumbnail");
            FF.Check(ffmpeg.avcodec_receive_packet(encoder, packet), "Read thumbnail");

            string temp = path + ".tmp";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write))
                file.Write(new ReadOnlySpan<byte>(packet->data, packet->size));
            File.Move(temp, path, true);
        }
        finally
        {
            ffmpeg.sws_freeContext(scaler);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&scaled);
            ffmpeg.avcodec_free_context(&encoder);
        }
    }
}

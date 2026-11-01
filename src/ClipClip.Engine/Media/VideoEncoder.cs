using ClipClip.Engine.Capture;
using ClipClip.Engine.Core;
using FFmpeg.AutoGen;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace ClipClip.Engine.Media;

public enum EncoderPreference { Auto, Nvenc, Amf, Qsv, Software }

public sealed record EncoderSpec(string CodecName, string DisplayName, bool Hardware, bool GpuFrames, GpuVendor? Vendor)
{
    public static readonly EncoderSpec Nvenc = new("h264_nvenc", "NVIDIA NVENC", true, true, GpuVendor.Nvidia);
    public static readonly EncoderSpec Amf = new("h264_amf", "AMD AMF", true, true, GpuVendor.Amd);
    public static readonly EncoderSpec Qsv = new("h264_qsv", "Intel Quick Sync", true, false, GpuVendor.Intel);
    public static readonly EncoderSpec X264 = new("libx264", "x264 (CPU)", false, false, null);
    public static readonly EncoderSpec MediaFoundation = new("h264_mf", "Media Foundation (CPU)", false, false, null);

    /// <summary>Encoders to try, best first. Software is always last, so recording still works
    /// when no hardware encoder can open.</summary>
    public static IReadOnlyList<EncoderSpec> Candidates(EncoderPreference preference, GpuVendor adapterVendor)
    {
        var list = new List<EncoderSpec>();
        switch (preference)
        {
            case EncoderPreference.Nvenc: list.Add(Nvenc); break;
            case EncoderPreference.Amf: list.Add(Amf); break;
            case EncoderPreference.Qsv: list.Add(Qsv); break;
            case EncoderPreference.Auto:
                if (adapterVendor == GpuVendor.Nvidia) list.Add(Nvenc);
                if (adapterVendor == GpuVendor.Amd) list.Add(Amf);
                if (adapterVendor == GpuVendor.Intel) list.Add(Qsv);
                break;
        }
        list.Add(X264);
        list.Add(MediaFoundation);
        return list;
    }
}

public unsafe delegate void PacketSink(byte* data, int size, long pts, bool key);

/// <summary>
/// H.264 through libavcodec. Hardware encoders take D3D11 textures directly. Quick Sync and the
/// CPU encoders take NV12 in system memory, read back from the same GPU conversion.
/// </summary>
public sealed unsafe class VideoEncoder : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly AVCodecContext* _context;
    private readonly AVPacket* _packet;
    private AVBufferRef* _hwDevice;
    private AVBufferRef* _hwFrames;
    private ID3D11Texture2D? _renderTarget;
    private ID3D11Texture2D? _staging;

    private VideoEncoder(GpuDevice gpu, EncoderSpec spec, AVCodecContext* context, AVBufferRef* hwDevice, AVBufferRef* hwFrames, int fps)
    {
        _gpu = gpu;
        Spec = spec;
        _context = context;
        _hwDevice = hwDevice;
        _hwFrames = hwFrames;
        _packet = ffmpeg.av_packet_alloc();
        Fps = fps;

        if (!spec.GpuFrames)
        {
            var description = new Texture2DDescription
            {
                Width = (uint)context->width,
                Height = (uint)context->height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            };
            _renderTarget = gpu.Device.CreateTexture2D(description);
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            _staging = gpu.Device.CreateTexture2D(description);
        }
    }

    public EncoderSpec Spec { get; }
    public int Fps { get; }
    public int Width => _context->width;
    public int Height => _context->height;
    public long BitRate => _context->bit_rate;

    public byte[] ExtraData =>
        _context->extradata == null || _context->extradata_size <= 0
            ? []
            : new ReadOnlySpan<byte>(_context->extradata, _context->extradata_size).ToArray();

    public static VideoEncoder Open(EncoderSpec spec, GpuDevice gpu, int width, int height, int fps, long bitRate, long maxRate)
    {
        FF.EnsureLoaded();
        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(spec.CodecName);
        if (codec == null) throw new NotSupportedException($"{spec.CodecName} is not in this FFmpeg build.");

        AVCodecContext* context = ffmpeg.avcodec_alloc_context3(codec);
        AVBufferRef* hwDevice = null;
        AVBufferRef* hwFrames = null;
        try
        {
            context->width = width;
            context->height = height;
            context->time_base = new AVRational { num = 1, den = fps };
            context->framerate = new AVRational { num = fps, den = 1 };
            context->sample_aspect_ratio = new AVRational { num = 1, den = 1 };
            // One keyframe per second keeps a clip within a second of the requested length.
            context->gop_size = fps;
            context->max_b_frames = 0;
            context->bit_rate = bitRate;
            context->rc_max_rate = maxRate;
            context->rc_buffer_size = (int)Math.Min(int.MaxValue, maxRate);
            context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            context->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            context->color_range = AVColorRange.AVCOL_RANGE_MPEG;

            if (spec.GpuFrames)
            {
                CreateHardwareContexts(gpu, width, height, &hwDevice, &hwFrames);
                context->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
                context->hw_frames_ctx = ffmpeg.av_buffer_ref(hwFrames);
            }
            else
            {
                context->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
            }

            ApplyEncoderOptions(spec, context);
            FF.Check(ffmpeg.avcodec_open2(context, codec, null), $"Open {spec.CodecName}");
            Log.Info($"Encoder {spec.DisplayName}: {width}x{height}@{fps} {bitRate / 1_000_000.0:F1} Mbps");
            return new VideoEncoder(gpu, spec, context, hwDevice, hwFrames, fps);
        }
        catch
        {
            ffmpeg.avcodec_free_context(&context);
            ffmpeg.av_buffer_unref(&hwFrames);
            ffmpeg.av_buffer_unref(&hwDevice);
            throw;
        }
    }

    private static void ApplyEncoderOptions(EncoderSpec spec, AVCodecContext* context)
    {
        void* options = context->priv_data;
        switch (spec.CodecName)
        {
            case "h264_nvenc":
                FF.SetOption(options, "preset", "p4");
                FF.SetOption(options, "tune", "hq");
                FF.SetOption(options, "profile", "high");
                FF.SetOption(options, "rc", "vbr");
                FF.SetOption(options, "multipass", "disabled");
                FF.SetOption(options, "delay", "0");
                FF.SetOption(options, "rc-lookahead", "0");
                FF.SetOption(options, "spatial-aq", "1");
                FF.SetOption(options, "forced-idr", "1");
                FF.SetOption(options, "b_ref_mode", "disabled");
                break;
            case "h264_amf":
                FF.SetOption(options, "usage", "transcoding");
                FF.SetOption(options, "profile", "high");
                FF.SetOption(options, "quality", "balanced");
                FF.SetOption(options, "rc", "vbr_peak");
                FF.SetOption(options, "enforce_hrd", "1");
                FF.SetOption(options, "header_insertion_mode", "idr");
                break;
            case "h264_qsv":
                FF.SetOption(options, "preset", "veryfast");
                FF.SetOption(options, "profile", "high");
                FF.SetOption(options, "look_ahead", "0");
                FF.SetOption(options, "async_depth", "1");
                FF.SetOption(options, "forced_idr", "1");
                break;
            case "libx264":
                FF.SetOption(options, "preset", "veryfast");
                FF.SetOption(options, "profile", "high");
                FF.SetOption(options, "tune", "zerolatency");
                FF.SetOption(options, "forced-idr", "1");
                // Leave cores for the game.
                context->thread_count = Math.Clamp(Environment.ProcessorCount / 3, 2, 8);
                break;
            case "h264_mf":
                FF.SetOption(options, "rate_control", "u_vbr");
                FF.SetOption(options, "scenario", "display_remoting");
                break;
        }
    }

    private static void CreateHardwareContexts(GpuDevice gpu, int width, int height, AVBufferRef** device, AVBufferRef** frames)
    {
        *device = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (*device == null) throw new InvalidOperationException("D3D11VA device context unavailable.");

        var deviceContext = (AVHWDeviceContext*)(*device)->data;
        var d3d = (AVD3D11VADeviceContext*)deviceContext->hwctx;
        gpu.Device.AddRef(); // FFmpeg releases this reference when the context is freed.
        d3d->Device = gpu.Device.NativePointer;
        FF.Check(ffmpeg.av_hwdevice_ctx_init(*device), "Init D3D11 hardware device");

        *frames = ffmpeg.av_hwframe_ctx_alloc(*device);
        if (*frames == null) throw new InvalidOperationException("D3D11 frame pool unavailable.");

        var framesContext = (AVHWFramesContext*)(*frames)->data;
        framesContext->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        framesContext->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        framesContext->width = width;
        framesContext->height = height;
        // Size 0 gives standalone textures, which the video processor can render into directly.
        framesContext->initial_pool_size = 0;
        var d3dFrames = (AVD3D11VAFramesContext*)framesContext->hwctx;
        d3dFrames->BindFlags = (uint)BindFlags.RenderTarget;
        FF.Check(ffmpeg.av_hwframe_ctx_init(*frames), "Init D3D11 frame pool");
    }

    /// <summary>Allocates a frame and the texture the converter should render into.</summary>
    public AVFrame* NewFrame(out ID3D11Texture2D target)
    {
        AVFrame* frame = ffmpeg.av_frame_alloc();
        if (Spec.GpuFrames)
        {
            int result = ffmpeg.av_hwframe_get_buffer(_hwFrames, frame, 0);
            if (result < 0)
            {
                ffmpeg.av_frame_free(&frame);
                FF.Check(result, "Get encoder surface");
            }
            target = new ID3D11Texture2D((nint)frame->data[0]);
            return frame;
        }

        frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        frame->width = Width;
        frame->height = Height;
        int allocated = ffmpeg.av_frame_get_buffer(frame, 0);
        if (allocated < 0)
        {
            ffmpeg.av_frame_free(&frame);
            FF.Check(allocated, "Allocate frame");
        }
        target = _renderTarget!;
        return frame;
    }

    /// <summary>Software path: pulls the converted NV12 back to system memory.</summary>
    public void FinishFrame(AVFrame* frame)
    {
        if (Spec.GpuFrames)
        {
            _gpu.Context.Flush();
            return;
        }

        _gpu.Context.CopyResource(_staging!, _renderTarget!);
        MappedSubresource mapped = _gpu.Context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* source = (byte*)mapped.DataPointer;
            int rowPitch = (int)mapped.RowPitch;
            CopyPlane(source, rowPitch, frame->data[0], frame->linesize[0], Width, Height);
            CopyPlane(source + rowPitch * Height, rowPitch, frame->data[1], frame->linesize[1], Width, Height / 2);
        }
        finally
        {
            _gpu.Context.Unmap(_staging!, 0);
        }
    }

    private static void CopyPlane(byte* source, int sourceStride, byte* destination, int destinationStride, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            Buffer.MemoryCopy(source, destination, destinationStride, width);
            source += sourceStride;
            destination += destinationStride;
        }
    }

    public void Encode(AVFrame* frame, long pts, PacketSink sink)
    {
        frame->pts = pts;
        frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
        FF.Check(ffmpeg.avcodec_send_frame(_context, frame), "Send frame to encoder");
        Drain(sink);
    }

    public void Flush(PacketSink sink)
    {
        if (ffmpeg.avcodec_send_frame(_context, null) >= 0) Drain(sink);
    }

    private void Drain(PacketSink sink)
    {
        while (true)
        {
            int result = ffmpeg.avcodec_receive_packet(_context, _packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return;
            FF.Check(result, "Receive encoded packet");
            sink(_packet->data, _packet->size, _packet->pts, (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0);
            ffmpeg.av_packet_unref(_packet);
        }
    }

    public void Dispose()
    {
        AVCodecContext* context = _context;
        AVPacket* packet = _packet;
        ffmpeg.avcodec_free_context(&context);
        ffmpeg.av_packet_free(&packet);
        ffmpeg.av_buffer_unref(ref _hwFrames);
        ffmpeg.av_buffer_unref(ref _hwDevice);
        _staging?.Dispose();
        _renderTarget?.Dispose();
    }
}

internal static unsafe class AvBufferExtensions
{
    extension(ffmpeg)
    {
        public static void av_buffer_unref(ref AVBufferRef* buffer)
        {
            fixed (AVBufferRef** pointer = &buffer) ffmpeg.av_buffer_unref(pointer);
        }
    }
}

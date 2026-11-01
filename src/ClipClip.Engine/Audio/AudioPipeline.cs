using ClipClip.Engine.Core;
using ClipClip.Engine.Media;
using FFmpeg.AutoGen;
using NAudio.CoreAudioApi;

namespace ClipClip.Engine.Audio;

public sealed record AudioConfig(
    bool SystemEnabled,
    float SystemVolume,
    bool MicEnabled,
    float MicVolume,
    bool MicMuted,
    string? OutputDeviceId = null);

/// <summary>
/// Mixes system audio and the mic on the shared timeline and encodes one AAC track into the ring.
/// The mixer runs a fixed delay behind real time so a late packet still lands in the right place.
/// With both sources off it writes silence, so clips always have a valid audio track.
/// </summary>
public sealed unsafe class AudioPipeline : IDisposable
{
    public const int SampleRate = 48000;
    private const int FrameSize = 1024;
    private const int LatencyFrames = SampleRate * 150 / 1000;
    private const long BitRate = 192_000;

    private readonly PacketRing _ring;
    private readonly MicHub _mic;
    private readonly TimelineBuffer _systemBuffer = new();
    private readonly TimelineBuffer _micBuffer = new();
    private readonly WasapiSource _system;
    private readonly AudioSink _micSink;

    private AudioConfig _config;
    private AVCodecContext* _encoder;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private Thread? _thread;
    private volatile bool _stop;
    private bool _micSubscribed;
    private float _systemPeak;

    public AudioPipeline(PacketRing ring, MicHub mic, AudioConfig config)
    {
        _ring = ring;
        _mic = mic;
        _config = config;
        _micSink = OnMicSamples;
        _system = new WasapiSource("System", true,
            () => AudioDevices.Resolve(_config.OutputDeviceId, DataFlow.Render), OnSystemSamples);
    }

    public AudioStreamInfo? Info { get; private set; }
    public AudioSourceState SystemState => _system.State;
    public string? SystemDeviceName => _system.DeviceName;

    public float TakeSystemPeak()
    {
        float peak = _systemPeak;
        _systemPeak = 0;
        return peak;
    }

    public void Start()
    {
        if (_thread != null) return;
        OpenEncoder();
        _stop = false;
        ApplyConfig(_config);
        _thread = new Thread(MixLoop) { IsBackground = true, Name = "ClipClip Mixer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        Thread? thread = _thread;
        if (thread == null) return;
        _stop = true;
        thread.Join();
        _thread = null;
        _system.Stop();
        if (_micSubscribed)
        {
            _mic.Unsubscribe(_micSink);
            _micSubscribed = false;
        }
        CloseEncoder();
    }

    public void Configure(AudioConfig config)
    {
        bool outputChanged = config.OutputDeviceId != _config.OutputDeviceId;
        _config = config;
        if (_thread == null) return;
        ApplyConfig(config);
        if (outputChanged) _system.Restart();
    }

    public void OnDevicesChanged()
    {
        if (_thread == null) return;
        _system.Restart();
    }

    private void ApplyConfig(AudioConfig config)
    {
        if (config.SystemEnabled) _system.Start();
        else _system.Stop();

        if (config.MicEnabled && !_micSubscribed)
        {
            _micBuffer.Reset();
            _mic.Subscribe(_micSink);
            _micSubscribed = true;
        }
        else if (!config.MicEnabled && _micSubscribed)
        {
            _mic.Unsubscribe(_micSink);
            _micSubscribed = false;
            _micBuffer.Reset();
        }
    }

    private static long NowFrames() => Clock.NowUs() * 48 / 1000;

    private void OnSystemSamples(float* stereo, int frames, long timeUs)
    {
        float peak = _systemPeak;
        for (int i = 0; i < frames * 2; i++)
        {
            float value = Math.Abs(stereo[i]);
            if (value > peak) peak = value;
        }
        _systemPeak = peak;
        _systemBuffer.Write(stereo, frames, timeUs * 48 / 1000);
    }

    private void OnMicSamples(float* stereo, int frames, long timeUs) =>
        _micBuffer.Write(stereo, frames, timeUs * 48 / 1000);

    private void OpenEncoder()
    {
        FF.EnsureLoaded();
        AVCodec* codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec == null) throw new NotSupportedException("AAC encoder is missing from this FFmpeg build.");

        _encoder = ffmpeg.avcodec_alloc_context3(codec);
        _encoder->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _encoder->sample_rate = SampleRate;
        _encoder->bit_rate = BitRate;
        _encoder->time_base = new AVRational { num = 1, den = SampleRate };
        _encoder->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        ffmpeg.av_channel_layout_default(&_encoder->ch_layout, 2);
        FF.Check(ffmpeg.avcodec_open2(_encoder, codec, null), "Open AAC encoder");

        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _frame->nb_samples = FrameSize;
        _frame->sample_rate = SampleRate;
        ffmpeg.av_channel_layout_default(&_frame->ch_layout, 2);
        FF.Check(ffmpeg.av_frame_get_buffer(_frame, 0), "Allocate audio frame");
        _packet = ffmpeg.av_packet_alloc();

        Info = new AudioStreamInfo
        {
            SampleRate = SampleRate,
            Channels = 2,
            BitRate = BitRate,
            ExtraData = new ReadOnlySpan<byte>(_encoder->extradata, _encoder->extradata_size).ToArray(),
        };
    }

    private void CloseEncoder()
    {
        if (ffmpeg.avcodec_send_frame(_encoder, null) >= 0)
        {
            while (ffmpeg.avcodec_receive_packet(_encoder, _packet) >= 0)
                ffmpeg.av_packet_unref(_packet);
        }
        fixed (AVCodecContext** encoder = &_encoder) ffmpeg.avcodec_free_context(encoder);
        fixed (AVFrame** frame = &_frame) ffmpeg.av_frame_free(frame);
        fixed (AVPacket** packet = &_packet) ffmpeg.av_packet_free(packet);
    }

    private void MixLoop()
    {
        float[] system = new float[FrameSize * 2];
        float[] mic = new float[FrameSize * 2];
        long position = NowFrames() - LatencyFrames;

        while (!_stop)
        {
            long target = NowFrames() - LatencyFrames;
            // After a system sleep there is nothing to catch up on.
            if (target - position > SampleRate * 2) position = target - FrameSize;

            while (position + FrameSize <= target && !_stop)
            {
                AudioConfig config = _config;
                if (config.SystemEnabled) _systemBuffer.Read(system, position);
                else Array.Clear(system);
                if (config.MicEnabled && !config.MicMuted) _micBuffer.Read(mic, position);
                else Array.Clear(mic);

                try
                {
                    EncodeBlock(system, mic, config, position);
                }
                catch (Exception ex)
                {
                    Log.Error("Audio encode failed", ex);
                }
                position += FrameSize;
            }
            Thread.Sleep(10);
        }
    }

    private void EncodeBlock(float[] system, float[] mic, AudioConfig config, long position)
    {
        FF.Check(ffmpeg.av_frame_make_writable(_frame), "Prepare audio frame");
        float* left = (float*)_frame->data[0];
        float* right = (float*)_frame->data[1];
        for (int i = 0; i < FrameSize; i++)
        {
            left[i] = Limit(system[i * 2] * config.SystemVolume + mic[i * 2] * config.MicVolume);
            right[i] = Limit(system[i * 2 + 1] * config.SystemVolume + mic[i * 2 + 1] * config.MicVolume);
        }

        _frame->pts = position;
        FF.Check(ffmpeg.avcodec_send_frame(_encoder, _frame), "Send audio");
        while (ffmpeg.avcodec_receive_packet(_encoder, _packet) >= 0)
        {
            long ptsUs = _packet->pts * 1000 / 48;
            _ring.Append(StreamKind.Audio, _packet->data, _packet->size, ptsUs, true, 0);
            ffmpeg.av_packet_unref(_packet);
        }
    }

    /// <summary>Soft knee above -2 dB so game audio plus voice saturates instead of clipping.</summary>
    private static float Limit(float sample)
    {
        const float knee = 0.8f;
        float magnitude = Math.Abs(sample);
        if (magnitude <= knee) return sample;
        float over = (magnitude - knee) / (1 - knee);
        float limited = knee + (1 - knee) * (over / (1 + over));
        return sample < 0 ? -limited : limited;
    }

    public void Dispose()
    {
        Stop();
        _system.Dispose();
    }
}

using ClipClip.Engine.Core;
using ClipClip.Engine.Media;
using FFmpeg.AutoGen;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClipClip.Engine.Audio;

public enum AudioSourceState { Stopped, Running, Disconnected }

public unsafe delegate void AudioSink(float* stereo, int frames, long timeUs);

/// <summary>
/// One shared-mode WASAPI stream, loopback or capture, normalised to 48 kHz stereo float.
/// Device loss is an expected state here: the thread drops to Disconnected and keeps retrying,
/// so unplugging a mic degrades the clip to no mic audio instead of stopping anything.
/// </summary>
public sealed unsafe class WasapiSource : IDisposable
{
    private const int TargetRate = 48000;
    private const long BufferHns = 2_000_000; // 200 ms

    private readonly string _name;
    private readonly bool _loopback;
    private readonly Func<MMDevice?> _resolveDevice;
    private readonly AudioSink _sink;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _restart;

    public WasapiSource(string name, bool loopback, Func<MMDevice?> resolveDevice, AudioSink sink)
    {
        _name = name;
        _loopback = loopback;
        _resolveDevice = resolveDevice;
        _sink = sink;
    }

    public AudioSourceState State { get; private set; } = AudioSourceState.Stopped;
    public string? DeviceName { get; private set; }

    public void Start()
    {
        if (_thread != null) return;
        _stop = false;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "ClipClip " + _name, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        Thread? thread = _thread;
        if (thread == null) return;
        _stop = true;
        thread.Join();
        _thread = null;
        State = AudioSourceState.Stopped;
    }

    /// <summary>Reopen on the current device, used when the default device changes.</summary>
    public void Restart() => _restart = true;

    private void ThreadMain()
    {
        bool logged = false;
        while (!_stop)
        {
            _restart = false;
            try
            {
                using MMDevice? device = _resolveDevice();
                if (device == null)
                {
                    State = AudioSourceState.Disconnected;
                    DeviceName = null;
                }
                else
                {
                    DeviceName = device.FriendlyName;
                    Run(device);
                    logged = false;
                    if (_stop || _restart) continue;
                }
            }
            catch (Exception ex)
            {
                State = AudioSourceState.Disconnected;
                if (!logged) Log.Warn($"{_name} audio lost, will keep retrying: {ex.Message}");
                logged = true;
            }

            for (int waited = 0; waited < 1000 && !_stop && !_restart; waited += 50)
                Thread.Sleep(50);
        }
        State = AudioSourceState.Stopped;
    }

    private void Run(MMDevice device)
    {
        using AudioClient client = device.AudioClient;
        WaveFormat format = client.MixFormat;
        client.Initialize(AudioClientShareMode.Shared, _loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None,
            BufferHns, 0, format, Guid.Empty);

        using AudioCaptureClient capture = client.AudioCaptureClient;
        SwrContext* resampler = CreateResampler(format);
        float[] output = new float[TargetRate * 2];
        int bytesPerFrame = format.BlockAlign;
        int channels = format.Channels;
        try
        {
            client.Start();
            State = AudioSourceState.Running;
            Log.Info($"{_name} audio: {device.FriendlyName} ({format.SampleRate} Hz, {channels} ch)");

            while (!_stop && !_restart)
            {
                Thread.Sleep(5);
                while (!_stop && capture.GetNextPacketSize() > 0)
                {
                    nint data = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags, out _, out long qpcHns);
                    long timeUs = (flags & AudioClientBufferFlags.TimestampError) != 0
                        ? Clock.NowUs() - frames * 1_000_000L / format.SampleRate
                        : qpcHns / 10;

                    fixed (float* outputPointer = output)
                    {
                        int converted;
                        if ((flags & AudioClientBufferFlags.Silent) != 0)
                        {
                            // The buffer contents are undefined here, and the timeline only needs the length.
                            converted = Math.Min((int)((long)frames * TargetRate / format.SampleRate), TargetRate);
                            new Span<float>(outputPointer, converted * 2).Clear();
                        }
                        else
                        {
                            byte* input = (byte*)data;
                            byte* outputBytes = (byte*)outputPointer;
                            converted = ffmpeg.swr_convert(resampler, &outputBytes, TargetRate, &input, frames);
                        }
                        if (converted > 0) _sink(outputPointer, converted, timeUs);
                    }
                    capture.ReleaseBuffer(frames);
                }
            }
            client.Stop();
        }
        finally
        {
            ffmpeg.swr_free(&resampler);
        }
    }

    private static SwrContext* CreateResampler(WaveFormat format)
    {
        FF.EnsureLoaded();
        AVSampleFormat sampleFormat = SampleFormat(format);
        AVChannelLayout inputLayout;
        AVChannelLayout outputLayout;
        ffmpeg.av_channel_layout_default(&inputLayout, format.Channels);
        ffmpeg.av_channel_layout_default(&outputLayout, 2);

        SwrContext* resampler = null;
        FF.Check(ffmpeg.swr_alloc_set_opts2(&resampler, &outputLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, TargetRate,
            &inputLayout, sampleFormat, format.SampleRate, 0, null), "Configure resampler");
        FF.Check(ffmpeg.swr_init(resampler), "Start resampler");
        return resampler;
    }

    private static AVSampleFormat SampleFormat(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (format is WaveFormatExtensible extensible &&
                        extensible.SubFormat == NAudio.MediaFoundation.AudioSubtypes.MFAudioFormat_Float);
        if (isFloat) return AVSampleFormat.AV_SAMPLE_FMT_FLT;
        return format.BitsPerSample switch
        {
            16 => AVSampleFormat.AV_SAMPLE_FMT_S16,
            32 => AVSampleFormat.AV_SAMPLE_FMT_S32,
            _ => throw new NotSupportedException($"Unsupported audio format: {format}"),
        };
    }

    public void Dispose() => Stop();
}

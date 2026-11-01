using System.Collections.Concurrent;
using ClipClip.Engine.Capture;
using ClipClip.Engine.Core;
using FFmpeg.AutoGen;
using Vortice.Direct3D11;
using Windows.Graphics;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace ClipClip.Engine.Media;

public enum ResolutionMode { Native, P1440, P1080, P720 }
public enum QualityPreset { Low, Medium, High, Ultra }

public sealed record VideoConfig(int Fps, ResolutionMode Resolution, QualityPreset Quality, EncoderPreference Encoder, bool IncludeCursor)
{
    public static (int Width, int Height) Canvas(DisplayInfo display, ResolutionMode mode)
    {
        int maxHeight = mode switch
        {
            ResolutionMode.P1440 => 1440,
            ResolutionMode.P1080 => 1080,
            ResolutionMode.P720 => 720,
            _ => int.MaxValue,
        };
        int height = Math.Min(display.Height, maxHeight);
        int width = (int)Math.Round((double)display.Width * height / display.Height);
        return (width & ~1, height & ~1);
    }

    /// <summary>Target bitrate from pixels per second, plus the peak the buffer is sized for.</summary>
    public (long Target, long Peak) Bitrates(int width, int height)
    {
        double bitsPerPixel = Quality switch
        {
            QualityPreset.Low => 0.07,
            QualityPreset.Medium => 0.11,
            QualityPreset.Ultra => 0.25,
            _ => 0.16,
        };
        long target = Math.Clamp((long)(width * (double)height * Fps * bitsPerPixel), 2_000_000, 80_000_000);
        return (target, target * 3 / 2);
    }
}

/// <summary>
/// The video half of the engine. One thread owns the device, the encoder and the frame clock.
/// Ticks are on a fixed grid so output stays constant frame rate: a tick with no new frame repeats
/// the last one, and a missed tick leaves a gap in the timestamps instead of drifting from audio.
/// </summary>
public sealed unsafe class VideoPipeline : IDisposable
{
    private readonly PacketRing _ring;
    private readonly ConcurrentDictionary<int, VideoStreamInfo> _streams = new();
    private readonly HashSet<string> _failedEncoders = [];
    private readonly HashSet<nint> _uncapturableWindows = [];
    private readonly object _gate = new();

    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile bool _restartRequested;
    private volatile bool _injectFailure;
    private VideoConfig _config;
    private CaptureTarget _target;
    private CaptureTarget? _pendingTarget;
    private int _epoch;

    private long _framesEncoded;
    private long _framesDropped;

    public VideoPipeline(PacketRing ring, VideoConfig config, CaptureTarget target)
    {
        _ring = ring;
        _config = config;
        _target = target;
    }

    public event Action<string>? Error;

    public bool IsRunning => _thread != null;
    public bool IsEncoding { get; private set; }
    public string? EncoderName { get; private set; }
    public bool EncoderIsHardware { get; private set; }
    public string? GpuName { get; private set; }
    public (int Width, int Height) OutputSize { get; private set; }
    public long FramesEncoded => Interlocked.Read(ref _framesEncoded);
    public long FramesDropped => Interlocked.Read(ref _framesDropped);
    public long PeakBitrate { get; private set; }
    public CaptureTarget CurrentTarget { get { lock (_gate) return _target; } }

    /// <summary>Set when a window target can no longer be captured and monitor capture took over.</summary>
    public event Action<CaptureTarget>? TargetFellBack;

    public VideoStreamInfo? StreamFor(int epoch) => _streams.GetValueOrDefault(epoch);

    public void Start()
    {
        if (_thread != null) return;
        _stopRequested = false;
        _restartRequested = false;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "ClipClip Video", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        Thread? thread = _thread;
        if (thread == null) return;
        _stopRequested = true;
        thread.Join();
        _thread = null;
    }

    /// <summary>Makes the running session fail as a lost device would. For recovery tests.</summary>
    public void InjectFailure() => _injectFailure = true;

    public void SetTarget(CaptureTarget target)
    {
        lock (_gate)
        {
            if (target.Kind == CaptureTargetKind.Window && _uncapturableWindows.Contains(target.Handle)) return;
            if (target.Kind == _target.Kind && target.Handle == _target.Handle && _pendingTarget == null) return;
            _pendingTarget = target;
        }
    }

    public void Reconfigure(VideoConfig config)
    {
        lock (_gate)
        {
            if (config == _config) return;
            _config = config;
            _failedEncoders.Clear();
        }
        _restartRequested = true;
    }

    private void ThreadMain()
    {
        int consecutiveFailures = 0;
        string? lastAttempt = null;
        while (!_stopRequested)
        {
            _restartRequested = false;
            long startedUs = Clock.NowUs();
            try
            {
                RunSession(ref lastAttempt);
                consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                IsEncoding = false;
                bool stable = Clock.NowUs() - startedUs > 30_000_000;
                consecutiveFailures = stable ? 1 : consecutiveFailures + 1;
                Log.Error($"Video session failed (attempt {consecutiveFailures})", ex);
                Error?.Invoke(ex.Message);

                // A repeat failure on the same encoder demotes it to the next candidate.
                if (consecutiveFailures >= 2 && lastAttempt != null)
                {
                    Log.Warn($"Giving up on encoder {lastAttempt} for this run");
                    _failedEncoders.Add(lastAttempt);
                    consecutiveFailures = 0;
                }

                for (int waited = 0; waited < 1500 && !_stopRequested; waited += 50)
                    Thread.Sleep(50);
            }
        }
        IsEncoding = false;
    }

    private void RunSession(ref string? attempt)
    {
        VideoConfig config;
        CaptureTarget target;
        lock (_gate)
        {
            config = _config;
            target = _target;
        }

        DisplayInfo display = target.Kind == CaptureTargetKind.Window
            ? Displays.ForWindow(target.Handle)
            : Displays.Enumerate().FirstOrDefault(d => d.Handle == target.Handle) ?? Displays.Primary();
        (int width, int height) = VideoConfig.Canvas(display, config.Resolution);
        (long bitRate, long peak) = config.Bitrates(width, height);

        GpuVendor? requiredVendor = config.Encoder switch
        {
            EncoderPreference.Nvenc => GpuVendor.Nvidia,
            EncoderPreference.Amf => GpuVendor.Amd,
            EncoderPreference.Qsv => GpuVendor.Intel,
            _ => null,
        };

        using GpuDevice gpu = GpuDevice.Create(display.Handle, requiredVendor);
        GpuName = gpu.Info.Name;

        VideoEncoder? encoder = null;
        foreach (EncoderSpec spec in EncoderSpec.Candidates(config.Encoder, gpu.Info.Vendor))
        {
            if (_failedEncoders.Contains(spec.CodecName)) continue;
            attempt = spec.CodecName;
            try
            {
                encoder = VideoEncoder.Open(spec, gpu, width, height, config.Fps, bitRate, peak);
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Encoder {spec.CodecName} unavailable: {ex.Message}");
            }
        }
        if (encoder == null) throw new InvalidOperationException("No H.264 encoder could be opened.");

        using (encoder)
        using (var converter = new FrameConverter(gpu, width, height, config.Fps))
        {
            int epoch = Interlocked.Increment(ref _epoch);
            _streams[epoch] = new VideoStreamInfo
            {
                Width = width, Height = height, Fps = config.Fps, BitRate = bitRate, ExtraData = encoder.ExtraData,
            };
            foreach (int old in _streams.Keys.Where(e => e < epoch - 4)) _streams.TryRemove(old, out _);

            EncoderName = encoder.Spec.DisplayName;
            EncoderIsHardware = encoder.Spec.Hardware;
            OutputSize = (width, height);
            PeakBitrate = peak;

            WgcCapture? capture = null;
            try
            {
                capture = new WgcCapture(gpu, target, config.IncludeCursor, config.Fps);
                TickLoop(ref capture, gpu, encoder, converter, config, epoch);
            }
            finally
            {
                capture?.Dispose();
            }
        }
    }

    private void TickLoop(ref WgcCapture capture, GpuDevice gpu, VideoEncoder encoder, FrameConverter converter,
        VideoConfig config, int epoch)
    {
        long periodUs = 1_000_000L / config.Fps;
        long originUs = Clock.NowUs();
        long tick = 0;
        AVFrame* last = null;
        PacketSink sink = (data, size, pts, key) =>
            _ring.Append(StreamKind.Video, data, size, originUs + pts * 1_000_000L / config.Fps, key, epoch);

        using var timer = new PrecisionTimer();
        try
        {
            while (!_stopRequested && !_restartRequested)
            {
                long dueUs = originUs + tick * 1_000_000L / config.Fps;
                long waitUs = dueUs - Clock.NowUs();
                if (waitUs > 0) timer.SleepUs(waitUs);

                long nowUs = Clock.NowUs();
                long actualTick = (nowUs - originUs) * config.Fps / 1_000_000L;
                if (actualTick > tick)
                {
                    Interlocked.Add(ref _framesDropped, actualTick - tick);
                    tick = actualTick;
                }

                if (_injectFailure)
                {
                    _injectFailure = false;
                    throw new InvalidOperationException("Injected device failure");
                }

                capture = ServiceTarget(capture, gpu, config);

                using (var frame = capture.TakeLatest())
                {
                    if (frame != null)
                    {
                        SizeInt32 size = frame.ContentSize;
                        AVFrame* next = encoder.NewFrame(out ID3D11Texture2D renderTarget);
                        try
                        {
                            using ID3D11Texture2D source = WgcCapture.TextureOf(frame);
                            converter.Convert(source, size.Width, size.Height, renderTarget);
                            encoder.FinishFrame(next);
                        }
                        catch
                        {
                            ffmpeg.av_frame_free(&next);
                            throw;
                        }
                        ffmpeg.av_frame_free(&last);
                        last = next;
                    }
                }

                if (last != null)
                {
                    // The encoder keeps its own reference, so the frame can be sent again next tick.
                    AVFrame* reference = ffmpeg.av_frame_clone(last);
                    try
                    {
                        encoder.Encode(reference, tick, sink);
                    }
                    finally
                    {
                        ffmpeg.av_frame_free(&reference);
                    }
                    Interlocked.Increment(ref _framesEncoded);
                    IsEncoding = true;
                }

                tick++;
            }
            encoder.Flush(sink);
        }
        finally
        {
            ffmpeg.av_frame_free(&last);
            IsEncoding = false;
        }
    }

    /// <summary>Swaps capture source between ticks. The encoder keeps running on the old frame, so
    /// moving between desktop and game never breaks the buffer.</summary>
    private WgcCapture ServiceTarget(WgcCapture capture, GpuDevice gpu, VideoConfig config)
    {
        CaptureTarget? next;
        lock (_gate)
        {
            next = _pendingTarget;
            _pendingTarget = null;
            if (next != null) _target = next;
        }

        bool isWindow = capture.Target.Kind == CaptureTargetKind.Window;
        bool neverDelivered = isWindow && !capture.HasReceivedFrame && Clock.NowUs() - capture.StartedUs > 3_000_000;
        if (next == null && (capture.ItemClosed || neverDelivered))
        {
            // Exclusive fullscreen, a closed window or an unplugged monitor. A monitor is still capturable.
            DisplayInfo display = isWindow ? Displays.ForWindow(capture.Target.Handle) : Displays.Primary();
            next = CaptureTarget.ForMonitor(display);
            lock (_gate)
            {
                _target = next;
                if (neverDelivered) _uncapturableWindows.Add(capture.Target.Handle);
            }
            Log.Warn($"Capture of {capture.Target.Label} stopped delivering, using {display.DeviceName}");
            TargetFellBack?.Invoke(next);
        }

        if (next == null) return capture;

        WgcCapture replacement;
        try
        {
            replacement = new WgcCapture(gpu, next, config.IncludeCursor, config.Fps);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not switch capture to {next.Label}: {ex.Message}");
            return capture;
        }
        capture.Dispose();
        return replacement;
    }

    public void Dispose() => Stop();
}

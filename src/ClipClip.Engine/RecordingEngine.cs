using System.Collections.Concurrent;
using System.Text;
using ClipClip.Engine.Audio;
using ClipClip.Engine.Capture;
using ClipClip.Engine.Core;
using ClipClip.Engine.Media;

namespace ClipClip.Engine;

public enum ClipTrigger { Hotkey, Voice, Button, Tray, Automation }

public sealed record ClipResult(bool Success, string? Path, double Seconds, string? Error, string? Game, ClipTrigger Trigger);

/// <summary>
/// Owns the replay buffer and its pipelines. Saving a clip only reads the ring on a worker thread,
/// so capture and encoding never pause, and any number of clips can be queued while recording runs.
/// </summary>
public sealed class RecordingEngine : IDisposable
{
    private const int RetentionMarginSeconds = 4;
    private const long AudioSettleUs = 300_000;
    private const long DebounceUs = 750_000;

    private readonly PacketRing _ring;
    private readonly VideoPipeline _video;
    private readonly AudioPipeline _audio;
    private readonly BlockingCollection<SaveRequest> _saves = new();
    private readonly Thread _saveThread;
    private readonly object _gate = new();

    private VideoConfig _videoConfig;
    private int _clipSeconds;
    private long _lastClipUs;
    private bool _running;

    private sealed record SaveRequest(long EndUs, int Seconds, string Directory, string? Game, ClipTrigger Trigger,
        TaskCompletionSource<ClipResult> Completion);

    public RecordingEngine(int clipSeconds, VideoConfig video, AudioConfig audio, MicHub mic, CaptureTarget initialTarget)
    {
        FF.EnsureLoaded();
        Mic = mic;
        _clipSeconds = clipSeconds;
        _videoConfig = video;
        (long capacity, TimeSpan retention) = BufferSize(video, initialTarget, clipSeconds);
        _ring = new PacketRing(capacity, retention);
        _video = new VideoPipeline(_ring, video, initialTarget);
        _video.Error += message => VideoError?.Invoke(message);
        _audio = new AudioPipeline(_ring, mic, audio);
        _saveThread = new Thread(SaveLoop) { IsBackground = true, Name = "ClipClip Save" };
        _saveThread.Start();
    }

    public MicHub Mic { get; }
    public event Action<string>? VideoError;
    public event Action<CaptureTarget>? TargetFellBack
    {
        add => _video.TargetFellBack += value;
        remove => _video.TargetFellBack -= value;
    }

    public bool IsRunning { get { lock (_gate) return _running; } }
    public bool IsEncoding => _video.IsEncoding;
    public string? EncoderName => _video.EncoderName;
    public bool EncoderIsHardware => _video.EncoderIsHardware;
    public string? GpuName => _video.GpuName;
    public (int Width, int Height) OutputSize => _video.OutputSize;
    public long FramesEncoded => _video.FramesEncoded;
    public long FramesDropped => _video.FramesDropped;
    public double BufferedSeconds => _ring.BufferedSeconds;
    public long BufferBytes => _ring.LiveBytes;
    public long BufferCapacityBytes => _ring.CapacityBytes;
    public bool BufferOnDisk => _ring.IsDiskBacked;
    public int ClipSeconds { get { lock (_gate) return _clipSeconds; } }
    public AudioSourceState SystemAudioState => _audio.SystemState;
    public string? SystemAudioDevice => _audio.SystemDeviceName;
    public CaptureTarget CurrentTarget => _video.CurrentTarget;
    public float TakeSystemPeak() => _audio.TakeSystemPeak();

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _audio.Start();
            _video.Start();
            _running = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _video.Stop();
            _audio.Stop();
            _running = false;
        }
    }

    public void SetTarget(CaptureTarget target) => _video.SetTarget(target);

    public void InjectVideoFailure() => _video.InjectFailure();

    public void SetClipSeconds(int seconds)
    {
        lock (_gate)
        {
            if (seconds == _clipSeconds) return;
            _clipSeconds = seconds;
            ResizeBuffer();
        }
    }

    public void SetVideo(VideoConfig config)
    {
        lock (_gate)
        {
            _videoConfig = config;
            ResizeBuffer();
            _video.Reconfigure(config);
        }
    }

    public void SetAudio(AudioConfig config) => _audio.Configure(config);

    public void OnAudioDevicesChanged() => _audio.OnDevicesChanged();

    /// <summary>Drops everything buffered. Used when the mic is turned off, because the mix
    /// is already baked into buffered audio and must not reach a later clip.</summary>
    public void ClearBuffer() => _ring.Clear();

    private void ResizeBuffer()
    {
        (long capacity, TimeSpan retention) = BufferSize(_videoConfig, _video.CurrentTarget, _clipSeconds);
        _ring.Reconfigure(capacity, retention);
    }

    private static (long Capacity, TimeSpan Retention) BufferSize(VideoConfig video, CaptureTarget target, int seconds)
    {
        DisplayInfo display = target.Kind == CaptureTargetKind.Window
            ? Displays.ForWindow(target.Handle)
            : Displays.Enumerate().FirstOrDefault(d => d.Handle == target.Handle) ?? Displays.Primary();
        (int width, int height) = VideoConfig.Canvas(display, video.Resolution);
        (_, long peak) = video.Bitrates(width, height);
        int retentionSeconds = seconds + RetentionMarginSeconds;
        long bytes = (peak + 256_000) / 8 * retentionSeconds;
        return (bytes + bytes / 10 + 8 * 1024 * 1024, TimeSpan.FromSeconds(retentionSeconds));
    }

    /// <summary>
    /// Queues a save of the last clip length, ending now. Returns null for a repeat inside the
    /// debounce window, so one press can never produce two clips, whichever input it came from.
    /// </summary>
    public Task<ClipResult>? SaveClip(string directory, string? game, ClipTrigger trigger, int? seconds = null)
    {
        long nowUs = Clock.NowUs();
        int clipSeconds;
        lock (_gate)
        {
            if (nowUs - _lastClipUs < DebounceUs) return null;
            _lastClipUs = nowUs;
            clipSeconds = seconds ?? _clipSeconds;
        }

        var completion = new TaskCompletionSource<ClipResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _saves.Add(new SaveRequest(nowUs, clipSeconds, directory, game, trigger, completion));
        return completion.Task;
    }

    private void SaveLoop()
    {
        foreach (SaveRequest request in _saves.GetConsumingEnumerable())
        {
            ClipResult result;
            try
            {
                // Audio runs behind real time. Waiting lets the end of the clip reach the ring.
                long waitUs = request.EndUs + AudioSettleUs - Clock.NowUs();
                if (waitUs > 0) Thread.Sleep((int)(waitUs / 1000) + 1);
                result = Save(request);
            }
            catch (Exception ex)
            {
                Log.Error("Clip save failed", ex);
                result = new ClipResult(false, null, 0, ex.Message, request.Game, request.Trigger);
            }
            request.Completion.TrySetResult(result);
        }
    }

    private ClipResult Save(SaveRequest request)
    {
        // After a game closes the footage ends at the last frame, not at the moment of the press.
        long endUs = Math.Min(request.EndUs, _ring.NewestVideoUs);
        ClipPlan? plan = _ring.Plan(endUs, request.Seconds * 1_000_000L);
        if (plan == null)
            return new ClipResult(false, null, 0, "Nothing has been recorded yet.", request.Game, request.Trigger);

        VideoStreamInfo? stream = _video.StreamFor(plan.Epoch);
        if (stream == null)
            return new ClipResult(false, null, 0, "Encoder details for this footage are gone.", request.Game, request.Trigger);

        string path = UniquePath(request.Directory, request.Game);
        ClipWriter.Write(_ring, plan, stream, _audio.Info, path, request.Game);
        Log.Info($"Saved {Path.GetFileName(path)}: {plan.DurationSeconds:F1}s, {new FileInfo(path).Length / (1024 * 1024.0):F1} MB");
        return new ClipResult(true, path, plan.DurationSeconds, null, request.Game, request.Trigger);
    }

    private static string UniquePath(string directory, string? game)
    {
        string stem = $"{Sanitize(game ?? "Desktop")}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        string path = Path.Combine(directory, stem + ".mp4");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(directory, $"{stem}_{i}.mp4");
        return path;
    }

    public static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char c in name)
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? ' ' : c);
        string clean = builder.ToString().Trim().TrimEnd('.');
        return clean.Length == 0 ? "Clip" : clean[..Math.Min(clean.Length, 60)];
    }

    public void Dispose()
    {
        Stop();
        _saves.CompleteAdding();
        _saveThread.Join(TimeSpan.FromSeconds(30));
        _video.Dispose();
        _audio.Dispose();
        _ring.Dispose();
    }
}

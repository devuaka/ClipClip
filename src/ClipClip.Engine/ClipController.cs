using ClipClip.Engine.Audio;
using ClipClip.Engine.Capture;
using ClipClip.Engine.Core;
using ClipClip.Engine.Detection;
using ClipClip.Engine.Library;
using ClipClip.Engine.Media;
using ClipClip.Engine.Voice;

namespace ClipClip.Engine;

public enum RecordingMode
{
    /// <summary>Game-only mode with no game running. Nothing is captured.</summary>
    WaitingForGame,
    Recording,
    Paused,
    Starting,
    Error,
}

/// <summary>
/// Decides what should be happening from settings plus the detected game, and makes the engine
/// match. The UI and tests both drive the app through this class only.
/// </summary>
public sealed class ClipController : IDisposable
{
    private const int ClipHotkeySlot = 1, OpenHotkeySlot = 2;
    private static readonly TimeSpan IdleBufferLifetime = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly HotkeyManager _hotkeys = new();
    private readonly GameDetector _detector;
    private readonly AudioDeviceWatcher? _deviceWatcher;
    private readonly MicHub _mic;
    private readonly System.Threading.Timer _idleTimer;
    private readonly System.Threading.Timer _voiceWatch;
    private RecordingEngine? _engine;
    private VoiceTrigger? _voice;
    private DetectedGame? _game;
    private bool _userPaused;
    private bool _disposed;
    private string? _lastError;

    public ClipController(AppSettings settings)
    {
        Settings = settings;
        FF.EnsureLoaded();
        Directory.CreateDirectory(settings.ClipDirectory);
        ClipWriter.CleanTempFiles(settings.ClipDirectory);

        Library = new ClipLibrary(settings.ClipDirectory);
        _mic = new MicHub(settings.MicrophoneId);
        _detector = new GameDetector(BuildRules());
        _detector.Changed += OnGameChanged;
        _detector.LibraryScanned += games => InstalledGamesChanged?.Invoke(games);
        _idleTimer = new System.Threading.Timer(_ => DisposeIdleEngine());
        _voiceWatch = new System.Threading.Timer(_ => StateChanged?.Invoke());

        try
        {
            _deviceWatcher = new AudioDeviceWatcher();
            _deviceWatcher.Changed += OnAudioDevicesChanged;
        }
        catch (Exception ex)
        {
            Log.Warn("Audio device notifications unavailable: " + ex.Message);
        }
    }

    public AppSettings Settings { get; }
    public ClipLibrary Library { get; }
    public MicHub Mic => _mic;
    public RecordingEngine? Engine { get { lock (_gate) return _engine; } }
    public DetectedGame? CurrentGame { get { lock (_gate) return _game; } }
    public IReadOnlyList<InstalledGame> InstalledGames => _detector.Installed;
    public string? LastError { get { lock (_gate) return _lastError; } }
    public bool UserPaused { get { lock (_gate) return _userPaused; } }
    public VoiceState VoiceStatus { get { lock (_gate) return _voice?.State ?? VoiceState.Off; } }
    public string? VoiceFailure { get { lock (_gate) return _voice?.FailureReason; } }

    public RecordingMode Mode
    {
        get
        {
            lock (_gate)
            {
                if (_userPaused) return RecordingMode.Paused;
                if (_lastError != null && _engine is not { IsEncoding: true }) return RecordingMode.Error;
                if (_engine is { IsRunning: true })
                    return _engine.IsEncoding ? RecordingMode.Recording : RecordingMode.Starting;
                return RecordingMode.WaitingForGame;
            }
        }
    }

    /// <summary>A clip is possible only if there is footage: recording now, or a buffer kept after a game closed.</summary>
    public bool CanClip => Engine is { BufferedSeconds: > 0.5 };

    public event Action? StateChanged;
    public event Action<ClipResult>? ClipSaved;
    public event Action<DetectedGame?>? GameChanged;
    public event Action<IReadOnlyList<InstalledGame>>? InstalledGamesChanged;
    public event Action? OpenRequested;
    public event Action<VoiceHit>? VoiceHeard;
    public event Action? AudioDevicesChanged;

    public void Start()
    {
        ApplyHotkeys();
        _detector.Start();
        Task.Run(Library.Refresh);
        Sync();
    }

    public (HotkeyResult Clip, HotkeyResult Open) ApplyHotkeys()
    {
        HotkeyResult clip = _hotkeys.Register(ClipHotkeySlot, Settings.ClipHotkey, () => ClipNow(ClipTrigger.Hotkey));
        HotkeyResult open = _hotkeys.Register(OpenHotkeySlot, Settings.OpenHotkey, () => OpenRequested?.Invoke());
        ClipHotkeyStatus = clip;
        if (clip != HotkeyResult.Ok) Log.Warn($"Clip hotkey {Settings.ClipHotkey} not registered: {clip}");
        return (clip, open);
    }

    public HotkeyResult ProbeHotkey(HotkeySpec hotkey) => _hotkeys.Probe(hotkey);

    public HotkeyResult ClipHotkeyStatus { get; private set; } = HotkeyResult.Ok;

    /// <summary>Takes a clip now. Returns false if there is nothing to save or it was a repeat press.</summary>
    public bool ClipNow(ClipTrigger trigger)
    {
        RecordingEngine? engine = Engine;
        if (engine == null || engine.BufferedSeconds <= 0)
        {
            Publish(new ClipResult(false, null, 0, "ClipClip isn't recording yet, so there's nothing to save.", null, trigger));
            return false;
        }

        Task<ClipResult>? save = engine.SaveClip(Settings.ClipDirectory, CurrentGame?.Name, trigger);
        if (save == null) return false;
        save.ContinueWith(task => Publish(task.Result), TaskScheduler.Default);
        return true;
    }

    private void Publish(ClipResult result)
    {
        ClipSaved?.Invoke(result);
        if (result.Success) Task.Run(Library.Refresh);
    }

    public void SetPaused(bool paused)
    {
        lock (_gate) _userPaused = paused;
        Sync();
    }

    /// <summary>Applies changed settings to whatever is running, then saves them.</summary>
    public void ApplySettings(bool micWasIncluded = false)
    {
        SettingsStore.Save(Settings);
        _mic.SetDevice(Settings.MicrophoneId);
        _detector.UpdateRules(BuildRules());
        Library.SetDirectory(Settings.ClipDirectory);

        RecordingEngine? engine = Engine;
        if (engine != null)
        {
            engine.SetClipSeconds(Settings.ClipSeconds);
            engine.SetVideo(BuildVideoConfig());
            engine.SetAudio(BuildAudioConfig());
            if (micWasIncluded && !Settings.IncludeMicrophone)
            {
                // The mic is already mixed into buffered audio. Honour "off" for past footage too.
                engine.ClearBuffer();
                Log.Info("Microphone turned off: replay buffer cleared");
            }
        }
        Sync();
    }

    private DetectionRules BuildRules() => new(
        Settings.AutoDetectGames,
        Settings.CustomGames.ToList(),
        new HashSet<string>(Settings.IgnoredExes, StringComparer.OrdinalIgnoreCase));

    private VideoConfig BuildVideoConfig() =>
        new(Settings.Fps, Settings.Resolution, Settings.Quality, Settings.Encoder, Settings.CaptureCursor);

    private AudioConfig BuildAudioConfig() => new(
        Settings.SystemAudioEnabled, Settings.SystemVolume,
        Settings.IncludeMicrophone, Settings.MicrophoneVolume, Settings.MicrophoneMuted);

    private CaptureTarget DesiredTarget(DetectedGame? game)
    {
        // With desktop recording on, an alt-tabbed game gives way to the desktop.
        if (game != null && game.Window != 0 && (game.IsForeground || !Settings.RecordOutsideGames))
            return CaptureTarget.ForWindow(game.Window, game.Name);
        return CaptureTarget.ForMonitor(Displays.Resolve(Settings.MonitorDevice));
    }

    private void OnGameChanged(DetectedGame? game)
    {
        DetectedGame? previous;
        lock (_gate)
        {
            previous = _game;
            _game = game;
        }
        Sync();
        if (game?.Name != previous?.Name)
        {
            RememberGame(game);
            GameChanged?.Invoke(game);
        }
    }

    /// <summary>Games that no store knows about are listed once seen, so they can be switched off.</summary>
    private void RememberGame(DetectedGame? game)
    {
        if (game == null || game.Source == GameSource.Custom || game.Source == GameSource.Library) return;
        lock (Settings.SeenGames)
        {
            if (Settings.SeenGames.Any(g => string.Equals(g.ExePath, game.ExePath, StringComparison.OrdinalIgnoreCase))) return;
            Settings.SeenGames.Add(new CustomGame { Name = game.Name, ExePath = game.ExePath });
        }
        SettingsStore.Save(Settings);
    }

    /// <summary>Makes the engine and voice trigger match the desired state.</summary>
    private void Sync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            bool shouldRecord = !_userPaused && (Settings.RecordOutsideGames || _game != null);
            try
            {
                if (shouldRecord)
                {
                    _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    CaptureTarget target = DesiredTarget(_game);
                    if (_engine == null)
                    {
                        _engine = new RecordingEngine(Settings.ClipSeconds, BuildVideoConfig(), BuildAudioConfig(), _mic, target);
                        _engine.VideoError += OnVideoError;
                        _engine.TargetFellBack += OnTargetFellBack;
                    }
                    if (!_engine.IsRunning)
                    {
                        _engine.SetTarget(target);
                        _engine.Start();
                    }
                    else
                    {
                        _engine.SetTarget(target);
                    }
                    _lastError = null;
                }
                else if (_engine is { IsRunning: true })
                {
                    _engine.Stop();
                    // The buffer outlives the game briefly so "clip that" still works if it crashed.
                    if (!_userPaused) _idleTimer.Change(IdleBufferLifetime, Timeout.InfiniteTimeSpan);
                }
                SyncVoice(shouldRecord);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error("Could not start recording", ex);
            }
        }
        StateChanged?.Invoke();
    }

    /// <summary>The mic is only listened to when voice is enabled and there is something to clip.</summary>
    private void SyncVoice(bool recording)
    {
        bool want = Settings.VoiceEnabled && recording;
        if (want)
        {
            if (_voice == null)
            {
                _voice = new VoiceTrigger(_mic, Settings.VoicePhrase, Settings.VoiceSensitivity);
                _voice.Triggered += _ => ClipNow(ClipTrigger.Voice);
                _voice.Heard += hit => VoiceHeard?.Invoke(hit);
            }
            _voice.Configure(Settings.VoicePhrase, Settings.VoiceSensitivity);
            _voice.Start();
            // Model load takes a moment. This refreshes the status once it is listening.
            _voiceWatch.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        }
        else if (_voice != null)
        {
            _voice.Dispose();
            _voice = null;
        }
    }

    private void DisposeIdleEngine()
    {
        lock (_gate)
        {
            if (_engine is null or { IsRunning: true }) return;
            _engine.Dispose();
            _engine = null;
            Log.Info("Idle replay buffer released");
        }
        StateChanged?.Invoke();
    }

    private void OnVideoError(string message)
    {
        // Runs on the video thread, which Sync may be joining while it holds the lock.
        Task.Run(() =>
        {
            lock (_gate) _lastError = message;
            StateChanged?.Invoke();
        });
    }

    private void OnTargetFellBack(CaptureTarget target) => StateChanged?.Invoke();

    private void OnAudioDevicesChanged()
    {
        // Device callbacks arrive on an audio thread, which must not make audio calls itself.
        Task.Run(() =>
        {
            Engine?.OnAudioDevicesChanged();
            _mic.Refresh();
            AudioDevicesChanged?.Invoke();
            StateChanged?.Invoke();
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _idleTimer.Dispose();
        _voiceWatch.Dispose();
        _detector.Dispose();
        _deviceWatcher?.Dispose();
        _hotkeys.Dispose();
        lock (_gate)
        {
            _voice?.Dispose();
            _voice = null;
            _engine?.Dispose();
            _engine = null;
        }
        _mic.Dispose();
        Library.Dispose();
        SettingsStore.Save(Settings);
    }
}

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using ClipClip.Engine;
using ClipClip.Engine.Audio;
using ClipClip.Engine.Core;
using ClipClip.Engine.Voice;

namespace ClipClip.ViewModels;

public sealed class HomeViewModel : ObservableObject, IDisposable
{
    private readonly ClipController _controller;
    private readonly MainViewModel _main;
    private readonly Action _libraryChanged;

    private RecordingMode _mode;
    private string _statusTitle = "";
    private string _statusDetail = "";
    private double _bufferProgress;
    private string _bufferText = "";
    private bool _canClip;
    private string _encoderText = "";
    private string _encoderDetail = "";
    private string _gameText = "";
    private string _micText = "";
    private string _micDetail = "";
    private string _voiceText = "";
    private string _voiceDetail = "";

    public HomeViewModel(ClipController controller, MainViewModel main)
    {
        _controller = controller;
        _main = main;
        ClipCommand = new RelayCommand(() => _controller.ClipNow(ClipTrigger.Button));
        TogglePauseCommand = new RelayCommand(() => _controller.SetPaused(!_controller.UserPaused));
        ViewAllCommand = new RelayCommand(() => _main.Page = Page.Clips);
        OpenClipCommand = new RelayCommand(parameter =>
        {
            _main.Page = Page.Clips;
            if (parameter is ClipItem item) _main.Clips.Play(item.Info);
        });
        OpenSettingsCommand = new RelayCommand(() => _main.Page = Page.Settings);

        _libraryChanged = () => Application.Current.Dispatcher.BeginInvoke(ReloadRecent);
        controller.Library.Changed += _libraryChanged;
        ReloadRecent();
    }

    public RelayCommand ClipCommand { get; }
    public RelayCommand TogglePauseCommand { get; }
    public RelayCommand ViewAllCommand { get; }
    public RelayCommand OpenClipCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }

    public ObservableCollection<ClipItem> Recent { get; } = [];

    public RecordingMode Mode { get => _mode; private set { if (Set(ref _mode, value)) { Notify(nameof(IsLive)); Notify(nameof(IsPaused)); Notify(nameof(IsWaiting)); Notify(nameof(IsProblem)); } } }
    public bool IsLive => _mode is RecordingMode.Recording;
    public bool IsPaused => _mode == RecordingMode.Paused;
    public bool IsWaiting => _mode == RecordingMode.WaitingForGame;
    public bool IsProblem => _mode == RecordingMode.Error;

    public string StatusTitle { get => _statusTitle; private set => Set(ref _statusTitle, value); }
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }
    public double BufferProgress { get => _bufferProgress; private set => Set(ref _bufferProgress, value); }
    public string BufferText { get => _bufferText; private set => Set(ref _bufferText, value); }
    public bool CanClip { get => _canClip; private set => Set(ref _canClip, value); }
    public string EncoderText { get => _encoderText; private set => Set(ref _encoderText, value); }
    public string EncoderDetail { get => _encoderDetail; private set => Set(ref _encoderDetail, value); }
    public string GameText { get => _gameText; private set => Set(ref _gameText, value); }
    public string MicText { get => _micText; private set => Set(ref _micText, value); }
    public string MicDetail { get => _micDetail; private set => Set(ref _micDetail, value); }
    public string VoiceText { get => _voiceText; private set => Set(ref _voiceText, value); }
    public string VoiceDetail { get => _voiceDetail; private set => Set(ref _voiceDetail, value); }

    public string ClipLength => $"{_controller.Settings.ClipSeconds}s";
    public string Shortcut => _controller.Settings.ClipHotkey.ToString();
    public string ShortcutDetail => _controller.ClipHotkeyStatus == HotkeyResult.Ok
        ? "Works inside games"
        : "Taken by another app, change it in Settings";

    public bool GamesOnly
    {
        get => !_controller.Settings.RecordOutsideGames;
        set => SetOutsideGames(!value);
    }

    public bool WithDesktop
    {
        get => _controller.Settings.RecordOutsideGames;
        set => SetOutsideGames(value);
    }

    private void SetOutsideGames(bool enabled)
    {
        if (_controller.Settings.RecordOutsideGames == enabled) return;
        _controller.Settings.RecordOutsideGames = enabled;
        _controller.ApplySettings();
        SettingsChanged();
    }

    public void SettingsChanged()
    {
        Notify(nameof(GamesOnly));
        Notify(nameof(WithDesktop));
        Notify(nameof(ClipLength));
        Notify(nameof(Shortcut));
        Notify(nameof(ShortcutDetail));
    }

    public void Update()
    {
        AppSettings settings = _controller.Settings;
        RecordingEngine? engine = _controller.Engine;
        string? game = _controller.CurrentGame?.Name;
        Mode = _controller.Mode;

        (StatusTitle, StatusDetail) = Mode switch
        {
            RecordingMode.Recording => ("Recording", game != null ? $"Capturing {game}" : "Capturing your desktop"),
            RecordingMode.Starting => ("Starting up", "Getting the recorder ready"),
            RecordingMode.Paused => ("Paused", "Nothing is being recorded"),
            RecordingMode.Error => ("Recording problem", _controller.LastError ?? "ClipClip is trying again"),
            _ => ("Waiting for a game", "Launch a game and recording starts by itself"),
        };

        double buffered = engine?.BufferedSeconds ?? 0;
        BufferProgress = Math.Clamp(buffered / settings.ClipSeconds, 0, 1);
        BufferText = engine == null ? "No footage yet" :
            buffered >= settings.ClipSeconds ? $"Full {settings.ClipSeconds}s ready" : $"{Math.Floor(buffered)}s of {settings.ClipSeconds}s ready";
        CanClip = _controller.CanClip;
        GameText = game ?? (settings.RecordOutsideGames ? "None, recording desktop" : "None detected");

        if (engine is { EncoderName: not null })
        {
            EncoderText = engine.EncoderName;
            (int width, int height) = engine.OutputSize;
            EncoderDetail = $"{width} x {height}, {settings.Fps} fps";
        }
        else
        {
            EncoderText = "Idle";
            EncoderDetail = $"{settings.Fps} fps when recording";
        }

        string micName = AudioDevices.MicrophoneName(settings.MicrophoneId);
        MicText = settings.IncludeMicrophone ? (settings.MicrophoneMuted ? "Muted" : "In clips") : "Not in clips";
        MicDetail = settings.IncludeMicrophone && _controller.Mic.State == AudioSourceState.Disconnected
            ? "Microphone disconnected"
            : micName;

        (VoiceText, VoiceDetail) = _controller.VoiceStatus switch
        {
            VoiceState.Listening => ("Listening", $"Say \"{settings.VoicePhrase}\""),
            VoiceState.Loading => ("Starting", "Loading the voice model"),
            VoiceState.MicUnavailable => ("No microphone", "Reconnect it to use voice"),
            VoiceState.Failed => ("Unavailable", _controller.VoiceFailure ?? "Voice recognition failed"),
            _ => settings.VoiceEnabled ? ("Standby", "Listens while recording") : ("Off", "Turn on in Settings"),
        };
    }

    private void ReloadRecent()
    {
        Recent.Clear();
        foreach (var clip in _controller.Library.Clips.Take(4))
            Recent.Add(new ClipItem(clip));
        Notify(nameof(HasRecent));
    }

    public bool HasRecent => Recent.Count > 0;

    public void Dispose() => _controller.Library.Changed -= _libraryChanged;
}

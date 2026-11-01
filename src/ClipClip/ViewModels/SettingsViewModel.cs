using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ClipClip.Engine;
using ClipClip.Engine.Audio;
using ClipClip.Engine.Capture;
using ClipClip.Engine.Core;
using ClipClip.Engine.Media;
using ClipClip.Engine.Voice;
using Microsoft.Win32;

namespace ClipClip.ViewModels;

public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private static readonly int[] Durations = [15, 30, 45, 60, 90, 120, 180];

    private readonly ClipController _controller;
    private readonly MainViewModel _main;
    private readonly AudioSink _meterSink;
    private readonly Action _devicesChanged;
    private AppSettings S => _controller.Settings;

    private string _customSeconds = "";
    private string _hotkeyError = "";
    private string _phraseError = "";
    private string _encoderStatus = "";
    private double _micLevel;
    private bool _testing;
    private string _testResult = "";
    private bool _meterOn;
    private CancellationTokenSource? _testCancel;

    public SettingsViewModel(ClipController controller, MainViewModel main)
    {
        _controller = controller;
        _main = main;
        unsafe { _meterSink = KeepMicOpen; }

        DurationChoices = Durations.Select(d => new Choice<int>(d >= 60 ? $"{d / 60}:{d % 60:00} min" : $"{d} seconds", d)).ToList();
        FpsChoices = [new("30 fps", 30), new("60 fps", 60), new("120 fps", 120)];
        ResolutionChoices =
        [
            new("Native (monitor resolution)", ResolutionMode.Native), new("1440p", ResolutionMode.P1440),
            new("1080p", ResolutionMode.P1080), new("720p", ResolutionMode.P720),
        ];
        QualityChoices =
        [
            new("Low, smallest files", QualityPreset.Low), new("Medium", QualityPreset.Medium),
            new("High", QualityPreset.High), new("Ultra, largest files", QualityPreset.Ultra),
        ];
        EncoderChoices =
        [
            new("Automatic (recommended)", EncoderPreference.Auto), new("NVIDIA NVENC", EncoderPreference.Nvenc),
            new("AMD AMF", EncoderPreference.Amf), new("Intel Quick Sync", EncoderPreference.Qsv),
            new("Software (CPU)", EncoderPreference.Software),
        ];
        MonitorChoices = Displays.Enumerate().Select(d => new Choice<string?>(d.Label, d.DeviceName)).ToList();
        _customSeconds = S.ClipSeconds.ToString();

        ReloadMicrophones();
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        ApplyCustomSecondsCommand = new RelayCommand(ApplyCustomSeconds);
        TestMicCommand = new RelayCommand(() => _ = TestMicAsync());
        ApplyPhraseCommand = new RelayCommand(ApplyPhrase);
        _devicesChanged = () => Application.Current.Dispatcher.BeginInvoke(ReloadMicrophones);
        controller.AudioDevicesChanged += _devicesChanged;
    }

    public IReadOnlyList<Choice<int>> DurationChoices { get; }
    public IReadOnlyList<Choice<int>> FpsChoices { get; }
    public IReadOnlyList<Choice<ResolutionMode>> ResolutionChoices { get; }
    public IReadOnlyList<Choice<QualityPreset>> QualityChoices { get; }
    public IReadOnlyList<Choice<EncoderPreference>> EncoderChoices { get; }
    public IReadOnlyList<Choice<string?>> MonitorChoices { get; }
    public ObservableCollection<Choice<string?>> MicChoices { get; } = [];

    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand ApplyCustomSecondsCommand { get; }
    public RelayCommand TestMicCommand { get; }
    public RelayCommand ApplyPhraseCommand { get; }

    private void Commit(bool micWasIncluded = false)
    {
        _controller.ApplySettings(micWasIncluded);
        _main.Home.SettingsChanged();
    }

    public Choice<int>? Duration
    {
        get => DurationChoices.FirstOrDefault(c => c.Value == S.ClipSeconds);
        set
        {
            if (value == null || value.Value == S.ClipSeconds) return;
            S.ClipSeconds = value.Value;
            CustomSeconds = value.Value.ToString();
            Commit();
            Notify();
        }
    }

    public string CustomSeconds { get => _customSeconds; set => Set(ref _customSeconds, value); }

    private void ApplyCustomSeconds()
    {
        if (!int.TryParse(_customSeconds, out int seconds) || seconds < 5 || seconds > 1800)
        {
            _main.ShowNotice("Pick a length from 5 to 1800 seconds");
            CustomSeconds = S.ClipSeconds.ToString();
            return;
        }
        S.ClipSeconds = seconds;
        Commit();
        Notify(nameof(Duration));
        _main.ShowNotice($"Clips are now {seconds} seconds");
    }

    public Choice<int>? Fps
    {
        get => FpsChoices.FirstOrDefault(c => c.Value == S.Fps);
        set { if (value != null) { S.Fps = value.Value; Commit(); Notify(); } }
    }

    public Choice<ResolutionMode>? Resolution
    {
        get => ResolutionChoices.FirstOrDefault(c => c.Value == S.Resolution);
        set { if (value != null) { S.Resolution = value.Value; Commit(); Notify(); } }
    }

    public Choice<QualityPreset>? Quality
    {
        get => QualityChoices.FirstOrDefault(c => c.Value == S.Quality);
        set { if (value != null) { S.Quality = value.Value; Commit(); Notify(); } }
    }

    public Choice<EncoderPreference>? Encoder
    {
        get => EncoderChoices.FirstOrDefault(c => c.Value == S.Encoder);
        set { if (value != null) { S.Encoder = value.Value; Commit(); Notify(); } }
    }

    public string EncoderStatus { get => _encoderStatus; private set => Set(ref _encoderStatus, value); }

    public Choice<string?>? Monitor
    {
        get => MonitorChoices.FirstOrDefault(c => c.Value == S.MonitorDevice) ?? MonitorChoices.FirstOrDefault();
        set { if (value != null) { S.MonitorDevice = value.Value; Commit(); Notify(); } }
    }

    public bool CaptureCursor
    {
        get => S.CaptureCursor;
        set { S.CaptureCursor = value; Commit(); Notify(); }
    }

    public bool RecordOutsideGames
    {
        get => S.RecordOutsideGames;
        set { S.RecordOutsideGames = value; Commit(); Notify(); }
    }

    public bool SystemAudio
    {
        get => S.SystemAudioEnabled;
        set { S.SystemAudioEnabled = value; Commit(); Notify(); }
    }

    public double SystemVolume
    {
        get => S.SystemVolume;
        set { S.SystemVolume = (float)value; Commit(); Notify(); }
    }

    public bool IncludeMic
    {
        get => S.IncludeMicrophone;
        set
        {
            bool was = S.IncludeMicrophone;
            S.IncludeMicrophone = value;
            Commit(was);
            Notify();
            if (was && !value) _main.ShowNotice("Mic off. Buffered footage was cleared so earlier mic audio can't reach a clip.");
        }
    }

    public Choice<string?>? Mic
    {
        get => MicChoices.FirstOrDefault(c => c.Value == S.MicrophoneId) ?? MicChoices.FirstOrDefault();
        set
        {
            if (value == null || value.Value == S.MicrophoneId) return;
            S.MicrophoneId = value.Value;
            Commit();
            Notify();
        }
    }

    public double MicVolume
    {
        get => S.MicrophoneVolume;
        set { S.MicrophoneVolume = (float)value; Commit(); Notify(); }
    }

    public bool MicMuted
    {
        get => S.MicrophoneMuted;
        set { S.MicrophoneMuted = value; Commit(); Notify(); }
    }

    public double MicLevel { get => _micLevel; private set => Set(ref _micLevel, value); }
    public bool Testing { get => _testing; private set { if (Set(ref _testing, value)) Notify(nameof(TestButtonText)); } }
    public string TestButtonText => _testing ? "Listening..." : "Test microphone";
    public string TestResult { get => _testResult; private set => Set(ref _testResult, value); }

    private void ReloadMicrophones()
    {
        MicChoices.Clear();
        MicChoices.Add(new Choice<string?>("Windows default", null));
        foreach (AudioDeviceInfo device in AudioDevices.Microphones())
            MicChoices.Add(new Choice<string?>(device.Name, device.Id));
        if (S.MicrophoneId != null && MicChoices.All(c => c.Value != S.MicrophoneId))
            MicChoices.Add(new Choice<string?>("Saved microphone (not connected)", S.MicrophoneId));
        Notify(nameof(Mic));
    }

    private async Task TestMicAsync()
    {
        if (_testing) return;
        Testing = true;
        TestResult = $"Speak now. Try saying \"{S.VoicePhrase}\".";
        _testCancel = new CancellationTokenSource();
        StartMeter();
        try
        {
            MicTestResult result = await new MicTest(_controller.Mic)
                .RunAsync(4, S.VoicePhrase, S.VoiceSensitivity, true, _testCancel.Token);

            if (result.Peak <= 0.001f)
            {
                TestResult = "No sound came through. Check that the right microphone is selected and isn't muted in Windows.";
            }
            else
            {
                string level = result.Peak < 0.05f ? "Very quiet, you may want to raise the mic level in Windows." :
                    result.Peak > 0.98f ? "Clipping, your input is too loud." : "Level looks good.";
                string phrase = result.Phrase switch
                {
                    { Accepted: true } => $"Heard \"{S.VoicePhrase}\", so voice clipping would have fired.",
                    { } => $"Almost heard \"{S.VoicePhrase}\". Raise sensitivity if this keeps happening.",
                    _ => "Didn't hear the phrase in that recording.",
                };
                TestResult = $"{level} {phrase}";
            }
        }
        catch (OperationCanceledException)
        {
            TestResult = "";
        }
        catch (Exception ex)
        {
            TestResult = "Test failed: " + ex.Message;
        }
        finally
        {
            StopMeter();
            Testing = false;
            _testCancel?.Dispose();
            _testCancel = null;
        }
    }

    /// <summary>The meter reads the mic hub's peak. It only needs the device to stay open.</summary>
    private static unsafe void KeepMicOpen(float* stereo, int frames, long timeUs) { }

    private void StartMeter()
    {
        if (_meterOn) return;
        _meterOn = true;
        _controller.Mic.Subscribe(_meterSink);
    }

    private void StopMeter()
    {
        if (!_meterOn) return;
        _meterOn = false;
        _controller.Mic.Unsubscribe(_meterSink);
        MicLevel = 0;
    }

    /// <summary>Leaving the page must not keep the mic open.</summary>
    public void StopMeter(bool cancelTest = true)
    {
        if (cancelTest) _testCancel?.Cancel();
        StopMeter();
    }

    public bool VoiceEnabled
    {
        get => S.VoiceEnabled;
        set { S.VoiceEnabled = value; Commit(); Notify(); }
    }

    public string VoicePhrase
    {
        get => S.VoicePhrase;
        set { _phraseDraft = value; Notify(); }
    }

    private string? _phraseDraft;

    public string PhraseError { get => _phraseError; private set => Set(ref _phraseError, value); }

    private void ApplyPhrase()
    {
        string phrase = VoiceTrigger.Normalize(_phraseDraft ?? S.VoicePhrase);
        if (phrase.Length == 0 || phrase.Split(' ').Length > 5)
        {
            PhraseError = "Use one to five words.";
            return;
        }
        try
        {
            IReadOnlyList<string> unknown = VoiceTrigger.UnknownWords(phrase);
            if (unknown.Count > 0)
            {
                PhraseError = $"The voice model doesn't know: {string.Join(", ", unknown)}";
                return;
            }
        }
        catch (Exception ex)
        {
            PhraseError = "Voice model unavailable: " + ex.Message;
            return;
        }
        PhraseError = "";
        S.VoicePhrase = phrase;
        _phraseDraft = null;
        Commit();
        Notify(nameof(VoicePhrase));
        _main.ShowNotice($"Trigger phrase is now \"{phrase}\"");
    }

    public double VoiceSensitivity
    {
        get => S.VoiceSensitivity;
        set { S.VoiceSensitivity = value; Commit(); Notify(); }
    }

    public HotkeySpec? ClipHotkey
    {
        get => S.ClipHotkey;
        set
        {
            if (value == null || value == S.ClipHotkey) return;
            if (!TrySetHotkey(value, true)) Notify();
        }
    }

    public HotkeySpec? OpenHotkey
    {
        get => S.OpenHotkey;
        set
        {
            if (value == S.OpenHotkey) return;
            if (value != null && !TrySetHotkey(value, false)) Notify();
        }
    }

    public string HotkeyError { get => _hotkeyError; private set => Set(ref _hotkeyError, value); }

    private bool TrySetHotkey(HotkeySpec hotkey, bool isClip)
    {
        HotkeySpec? other = isClip ? S.OpenHotkey : S.ClipHotkey;
        if (hotkey == other)
        {
            HotkeyError = $"{hotkey} is already used by the other ClipClip shortcut.";
            return false;
        }
        if (hotkey.VirtualKey == 0x7B && !hotkey.Ctrl && !hotkey.Alt && !hotkey.Shift && !hotkey.Win)
        {
            HotkeyError = "F12 is reserved by Windows. Pick another key.";
            return false;
        }
        if (_controller.ProbeHotkey(hotkey) != HotkeyResult.Ok)
        {
            HotkeyError = $"{hotkey} is already taken by another app. Pick another combination.";
            return false;
        }

        HotkeyError = "";
        if (isClip) S.ClipHotkey = hotkey;
        else S.OpenHotkey = hotkey;
        _controller.ApplyHotkeys();
        SettingsStore.Save(S);
        _main.Home.SettingsChanged();
        Notify(isClip ? nameof(ClipHotkey) : nameof(OpenHotkey));
        return true;
    }

    public string ClipDirectory => S.ClipDirectory;

    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose where clips are saved", InitialDirectory = S.ClipDirectory };
        if (dialog.ShowDialog() != true) return;
        try
        {
            Directory.CreateDirectory(dialog.FolderName);
            S.ClipDirectory = dialog.FolderName;
            Commit();
            Notify(nameof(ClipDirectory));
        }
        catch (Exception ex)
        {
            _main.ShowNotice("Can't use that folder: " + ex.Message);
        }
    }

    public bool StartWithWindows
    {
        get => WindowsStartup.IsEnabled();
        set
        {
            S.StartWithWindows = value;
            WindowsStartup.Set(value, Environment.ProcessPath!);
            SettingsStore.Save(S);
            Notify();
        }
    }

    public bool MinimizeToTray
    {
        get => S.MinimizeToTray;
        set { S.MinimizeToTray = value; SettingsStore.Save(S); Notify(); }
    }

    public bool Notifications
    {
        get => S.Notifications;
        set { S.Notifications = value; SettingsStore.Save(S); Notify(); }
    }

    public bool ClipSound
    {
        get => S.ClipSound;
        set { S.ClipSound = value; SettingsStore.Save(S); Notify(); }
    }

    public void Update()
    {
        RecordingEngine? engine = _controller.Engine;
        EncoderStatus = engine?.EncoderName is { } name
            ? $"In use: {name}{(engine.EncoderIsHardware ? "" : ", no hardware encoder available")}"
            : "Chosen when recording starts";
        if (_meterOn) MicLevel = Math.Clamp(_controller.Mic.TakePeak(), 0, 1);
    }

    public void Dispose()
    {
        _controller.AudioDevicesChanged -= _devicesChanged;
        _testCancel?.Cancel();
        StopMeter();
    }
}

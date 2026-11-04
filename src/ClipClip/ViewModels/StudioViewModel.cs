using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClipClip.Engine;
using ClipClip.Engine.Audio;
using ClipClip.Engine.Editing;
using ClipClip.Engine.Library;
using Microsoft.Win32;
using NAudio.Wave;

namespace ClipClip.ViewModels;

public enum StudioTab { Media, Text, Stickers, Audio }

/// <summary>
/// The editor. The project is plain data; every change goes through <see cref="Commit"/>, which
/// records an undo step, saves after a short pause and tells the timeline and preview to redraw.
/// </summary>
public sealed class StudioViewModel : ObservableObject, IDisposable
{
    private const int HistoryLimit = 150;

    private readonly ClipController _controller;
    private readonly MainViewModel _main;
    private readonly Action _libraryChanged;
    private readonly DispatcherTimer _saveTimer;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private string? _lastCommitKey;
    private DateTime _lastCommitTime;

    private EditorProject _project = new();
    private object? _selected;
    private double _playhead;
    private double _pixelsPerSecond = 40;
    private StudioTab _tab = StudioTab.Media;
    private bool _isExporting;
    private double _progress;
    private string _status = "";
    private string? _resultPath;
    private CancellationTokenSource? _cancel;

    private WaveFileWriter? _voiceWriter;
    private string? _voicePath;
    private double _voiceStart;
    private readonly AudioSink _voiceSink;

    public StudioViewModel(ClipController controller, MainViewModel main)
    {
        _controller = controller;
        _main = main;
        unsafe { _voiceSink = OnVoiceSamples; }
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            ProjectStore.Save(_project);
            RefreshProjects();
        };

        AspectChoices = [new("Landscape (original)", ExportAspect.Original), new("Vertical 9:16", ExportAspect.Vertical), new("Square 1:1", ExportAspect.Square)];
        FpsChoices = [new("30 fps", 30), new("60 fps", 60)];
        SpeedChoices = [new("0.25x", 0.25), new("0.5x", 0.5), new("0.75x", 0.75), new("1x", 1.0), new("1.5x", 1.5), new("2x", 2.0), new("3x", 3.0), new("4x", 4.0)];
        FilterChoices = [new("None", FilterPreset.None), new("Vivid", FilterPreset.Vivid), new("Warm", FilterPreset.Warm), new("Cool", FilterPreset.Cool), new("Black and white", FilterPreset.Mono), new("Faded", FilterPreset.Faded)];
        MotionChoices = [new("None", MotionEffect.None), new("Slow zoom", MotionEffect.SlowZoom), new("Camera shake", MotionEffect.Shake)];
        TransitionChoices = [new("None", TransitionKind.None), new("Fade from black", TransitionKind.Fade), new("White flash", TransitionKind.Flash), new("Zoom punch", TransitionKind.Zoom)];
        AnimationChoices = [new("None", LayerAnimation.None), new("Pop in", LayerAnimation.Pop), new("Fade", LayerAnimation.Fade), new("Slide up", LayerAnimation.SlideUp)];
        FontChoices = new[] { "Bahnschrift", "Segoe UI", "Impact", "Arial Black", "Comic Sans MS", "Georgia", "Consolas" }
            .Where(f => System.Drawing.FontFamily.Families.Any(family => family.Name == f)).ToList();
        ColorChoices = ["#FFFFFF", "#FFE14D", "#FF4D6D", "#A855F7", "#38BDF8", "#4ADE80", "#FB923C", "#111111"];

        Stickers = new ObservableCollection<string>(StickerLibrary.EnsureBuiltIns());

        AddMediaCommand = new RelayCommand(p => { if (p is ClipItem item) AddClip(item.Info); });
        ImportVideoCommand = new RelayCommand(ImportVideo);
        AddTextCommand = new RelayCommand(p => AddText(p as string ?? "title"));
        AddStickerCommand = new RelayCommand(p => { if (p is string path) AddSticker(path); });
        ImportStickerCommand = new RelayCommand(ImportSticker);
        ImportAudioCommand = new RelayCommand(ImportAudio);
        RecordVoiceCommand = new RelayCommand(ToggleVoiceover);
        SplitCommand = new RelayCommand(Split);
        DeleteCommand = new RelayCommand(Delete);
        DuplicateCommand = new RelayCommand(Duplicate);
        UndoCommand = new RelayCommand(Undo);
        RedoCommand = new RelayCommand(Redo);
        NewProjectCommand = new RelayCommand(NewProject);
        OpenProjectCommand = new RelayCommand(p => { if (p is ProjectEntry entry) Open(entry.Id); });
        DeleteProjectCommand = new RelayCommand(DeleteProject);
        SetColorCommand = new RelayCommand(p =>
        {
            if (p is string color && SelectedLayer != null)
            {
                SelectedLayer.Color = color;
                Commit("color");
            }
        });
        TabCommand = new RelayCommand(p => { if (p is string name && Enum.TryParse(name, out StudioTab tab)) Tab = tab; });
        ZoomInCommand = new RelayCommand(() => PixelsPerSecond *= 1.4);
        ZoomOutCommand = new RelayCommand(() => PixelsPerSecond /= 1.4);
        ExportCommand = new RelayCommand(Export);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel());
        ShowResultCommand = new RelayCommand(() =>
        {
            ClipInfo? clip = _controller.Library.Clips.FirstOrDefault(c => c.Path == _resultPath);
            if (clip == null) return;
            _main.Page = Page.Clips;
            _main.Clips.Play(clip);
        });

        _libraryChanged = () => Application.Current.Dispatcher.BeginInvoke(ReloadMedia);
        controller.Library.Changed += _libraryChanged;
        ReloadMedia();

        ProjectEntry? recent = ProjectStore.List().FirstOrDefault();
        LoadProject(recent != null ? ProjectStore.Load(recent.Id) ?? new EditorProject() : new EditorProject());
    }

    // Lists for the inspector and toolbars.
    public IReadOnlyList<Choice<ExportAspect>> AspectChoices { get; }
    public IReadOnlyList<Choice<int>> FpsChoices { get; }
    public IReadOnlyList<Choice<double>> SpeedChoices { get; }
    public IReadOnlyList<Choice<FilterPreset>> FilterChoices { get; }
    public IReadOnlyList<Choice<MotionEffect>> MotionChoices { get; }
    public IReadOnlyList<Choice<TransitionKind>> TransitionChoices { get; }
    public IReadOnlyList<Choice<LayerAnimation>> AnimationChoices { get; }
    public IReadOnlyList<string> FontChoices { get; }
    public IReadOnlyList<string> ColorChoices { get; }
    public ObservableCollection<string> Stickers { get; }
    public ObservableCollection<ClipItem> Media { get; } = [];
    public ObservableCollection<ProjectEntry> Projects { get; } = [];

    public RelayCommand AddMediaCommand { get; }
    public RelayCommand ImportVideoCommand { get; }
    public RelayCommand AddTextCommand { get; }
    public RelayCommand AddStickerCommand { get; }
    public RelayCommand ImportStickerCommand { get; }
    public RelayCommand ImportAudioCommand { get; }
    public RelayCommand RecordVoiceCommand { get; }
    public RelayCommand SplitCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand NewProjectCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand DeleteProjectCommand { get; }
    public RelayCommand SetColorCommand { get; }
    public RelayCommand TabCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ShowResultCommand { get; }

    /// <summary>The project changed: redraw the timeline and the preview.</summary>
    public event Action? ProjectChanged;

    public EditorProject Project => _project;

    public string ProjectName
    {
        get => _project.Name;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _project.Name) return;
            _project.Name = value.Trim();
            Commit("name");
        }
    }

    public StudioTab Tab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;
            Notify(nameof(IsMediaTab));
            Notify(nameof(IsTextTab));
            Notify(nameof(IsStickersTab));
            Notify(nameof(IsAudioTab));
        }
    }

    public bool IsMediaTab { get => _tab == StudioTab.Media; set { if (value) Tab = StudioTab.Media; } }
    public bool IsTextTab { get => _tab == StudioTab.Text; set { if (value) Tab = StudioTab.Text; } }
    public bool IsStickersTab { get => _tab == StudioTab.Stickers; set { if (value) Tab = StudioTab.Stickers; } }
    public bool IsAudioTab { get => _tab == StudioTab.Audio; set { if (value) Tab = StudioTab.Audio; } }

    public double PixelsPerSecond
    {
        get => _pixelsPerSecond;
        set
        {
            if (Set(ref _pixelsPerSecond, Math.Clamp(value, 6, 400))) ProjectChanged?.Invoke();
        }
    }

    public double Playhead
    {
        get => _playhead;
        set
        {
            double clamped = Math.Clamp(value, 0, Math.Max(0, _project.Duration - 0.001));
            if (Set(ref _playhead, clamped)) Notify(nameof(TimeText));
        }
    }

    public string TimeText => $"{Format(_playhead)} / {Format(_project.Duration)}";

    private static string Format(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss\.f" : @"m\:ss\.f");

    public bool HasClips => _project.Video.Count > 0;

    public ExportAspect Aspect
    {
        get => _project.Aspect;
        set
        {
            if (value == _project.Aspect) return;
            _project.Aspect = value;
            Commit("aspect");
        }
    }

    public int Fps
    {
        get => _project.Fps;
        set
        {
            if (value == _project.Fps) return;
            _project.Fps = value;
            Commit("fps");
        }
    }

    // Selection
    public object? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            NotifySelection();
        }
    }

    public VideoClip? SelectedVideo => _selected as VideoClip;
    public OverlayLayer? SelectedLayer => _selected as OverlayLayer;
    public AudioClip? SelectedAudio => _selected as AudioClip;
    public bool IsVideoSelected => _selected is VideoClip;
    public bool IsTextSelected => _selected is OverlayLayer { Kind: LayerKind.Text };
    public bool IsStickerSelected => _selected is OverlayLayer { Kind: LayerKind.Sticker };
    public bool IsAudioSelected => _selected is AudioClip;
    public bool HasSelection => _selected != null;

    private void NotifySelection()
    {
        foreach (string name in new[] { nameof(SelectedVideo), nameof(SelectedLayer), nameof(SelectedAudio), nameof(IsVideoSelected),
                     nameof(IsTextSelected), nameof(IsStickerSelected), nameof(IsAudioSelected), nameof(HasSelection) })
            Notify(name);
        ProjectChanged?.Invoke();
    }

    // Undo, redo and saving

    /// <summary>Records the current state as an undo step. Rapid edits with the same key, such
    /// as dragging a slider, merge into one step.</summary>
    public void Commit(string key)
    {
        string json = _project.ToJson();
        if (_historyIndex >= 0 && _historyIndex < _history.Count && _history[_historyIndex] == json)
        {
            Changed();
            return;
        }
        bool merge = key == _lastCommitKey && DateTime.Now - _lastCommitTime < TimeSpan.FromMilliseconds(700) && _historyIndex > 0;
        if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        if (merge) _history[_historyIndex] = json;
        else
        {
            _history.Add(json);
            if (_history.Count > HistoryLimit) _history.RemoveAt(0);
            _historyIndex = _history.Count - 1;
        }
        _lastCommitKey = key;
        _lastCommitTime = DateTime.Now;
        Changed();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Redraws without recording a step, for live dragging.</summary>
    public void Changed()
    {
        Notify(nameof(TimeText));
        Notify(nameof(HasClips));
        Notify(nameof(ProjectName));
        Notify(nameof(Aspect));
        Notify(nameof(Fps));
        Playhead = _playhead;
        ProjectChanged?.Invoke();
    }

    private void Undo()
    {
        if (_historyIndex <= 0) return;
        _historyIndex--;
        Restore();
    }

    private void Redo()
    {
        if (_historyIndex >= _history.Count - 1) return;
        _historyIndex++;
        Restore();
    }

    private void Restore()
    {
        string? selectedId = SelectedId();
        _project = EditorProject.FromJson(_history[_historyIndex]);
        _lastCommitKey = null;
        Selected = FindById(selectedId);
        NotifySelection();
        Changed();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private string? SelectedId() => _selected switch
    {
        VideoClip v => v.Id,
        OverlayLayer l => l.Id,
        AudioClip a => a.Id,
        _ => null,
    };

    private object? FindById(string? id) =>
        id == null ? null :
        (object?)_project.Video.FirstOrDefault(v => v.Id == id) ??
        (object?)_project.Layers.FirstOrDefault(l => l.Id == id) ??
        _project.Audio.FirstOrDefault(a => a.Id == id);

    private void LoadProject(EditorProject project)
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            ProjectStore.Save(_project);
        }
        _project = project;
        _history.Clear();
        _history.Add(project.ToJson());
        _historyIndex = 0;
        _lastCommitKey = null;
        Selected = null;
        _playhead = 0;
        Changed();
        RefreshProjects();
    }

    private void NewProject() => LoadProject(new EditorProject());

    private void Open(string id)
    {
        if (id == _project.Id) return;
        EditorProject? project = ProjectStore.Load(id);
        if (project != null) LoadProject(project);
    }

    private void DeleteProject()
    {
        if (!Views.ConfirmDialog.Ask("Delete this project?", $"\"{_project.Name}\" and its voiceovers are removed. Exported videos stay.", "Delete")) return;
        _saveTimer.Stop();
        ProjectStore.Delete(_project.Id);
        ProjectEntry? next = ProjectStore.List().FirstOrDefault();
        LoadProject(next != null ? ProjectStore.Load(next.Id) ?? new EditorProject() : new EditorProject());
    }

    private void RefreshProjects()
    {
        Projects.Clear();
        foreach (ProjectEntry entry in ProjectStore.List()) Projects.Add(entry);
    }

    private void ReloadMedia()
    {
        Media.Clear();
        foreach (ClipInfo clip in _controller.Library.Clips) Media.Add(new ClipItem(clip));
    }

    // Adding things

    public void AddClip(ClipInfo clip) => AddVideo(clip.Path, clip.Title, clip.DurationSeconds);

    private void AddVideo(string path, string title, double duration)
    {
        if (duration <= 0)
        {
            try { duration = MediaInfo.Probe(path).Duration; }
            catch (Exception ex)
            {
                _main.ShowNotice("Can't open that video: " + ex.Message);
                return;
            }
        }
        if (_project.Video.Count == 0 && _project.Name == "Untitled project") _project.Name = title;
        var clip = new VideoClip { SourcePath = path, Title = title, SourceDuration = duration, In = 0, Out = duration };
        _project.Video.Add(clip);
        Selected = clip;
        Commit("add");
    }

    private void ImportVideo()
    {
        var dialog = new OpenFileDialog { Title = "Add a video", Filter = "Videos|*.mp4;*.mov;*.mkv;*.webm;*.avi|All files|*.*" };
        if (dialog.ShowDialog() == true) AddVideo(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName), 0);
    }

    private double LayerStart() => Math.Min(_playhead, Math.Max(0, _project.Duration - 0.5));

    private void AddText(string style)
    {
        double start = LayerStart();
        var layer = new OverlayLayer { Kind = LayerKind.Text, Start = start, End = Math.Min(start + 3, Math.Max(start + 0.5, _project.Duration)) };
        switch (style)
        {
            case "caption":
                layer.Text = "Caption";
                layer.Y = 0.82;
                layer.Size = 0.06;
                layer.Animation = LayerAnimation.Fade;
                break;
            case "callout":
                layer.Text = "NO WAY";
                layer.Y = 0.2;
                layer.Size = 0.1;
                layer.Color = "#FFE14D";
                layer.Font = FontChoices.Contains("Impact") ? "Impact" : "Bahnschrift";
                layer.Animation = LayerAnimation.SlideUp;
                break;
            case "subtitle":
                layer.Text = "Subtitle";
                layer.Y = 0.9;
                layer.Size = 0.045;
                layer.Bold = false;
                layer.Animation = LayerAnimation.None;
                break;
            default:
                layer.Text = "Title";
                layer.Size = 0.12;
                break;
        }
        _project.Layers.Add(layer);
        Selected = layer;
        Commit("add");
    }

    private void AddSticker(string path)
    {
        double start = LayerStart();
        var layer = new OverlayLayer
        {
            Kind = LayerKind.Sticker, ImagePath = path, Text = Path.GetFileNameWithoutExtension(path),
            Start = start, End = Math.Min(start + 2.5, Math.Max(start + 0.5, _project.Duration)),
            X = 0.5, Y = 0.4, Size = 0.32, Animation = LayerAnimation.Pop,
        };
        _project.Layers.Add(layer);
        Selected = layer;
        Commit("add");
    }

    private void ImportSticker()
    {
        var dialog = new OpenFileDialog { Title = "Choose an image", Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp|All files|*.*" };
        if (dialog.ShowDialog() != true) return;
        Stickers.Add(dialog.FileName);
        AddSticker(dialog.FileName);
    }

    private void ImportAudio()
    {
        var dialog = new OpenFileDialog { Title = "Add music or a sound", Filter = "Audio|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac;*.mp4|All files|*.*" };
        if (dialog.ShowDialog() != true) return;
        double duration;
        try { duration = MediaInfo.Probe(dialog.FileName).Duration; }
        catch (Exception ex)
        {
            _main.ShowNotice("Can't open that file: " + ex.Message);
            return;
        }
        var clip = new AudioClip
        {
            SourcePath = dialog.FileName, Title = Path.GetFileNameWithoutExtension(dialog.FileName), SourceDuration = duration,
            Start = _playhead, In = 0, Out = duration, Volume = 0.6, FadeOut = 1,
        };
        _project.Audio.Add(clip);
        Selected = clip;
        Commit("add");
    }

    // Voiceover

    public bool IsRecording => _voiceWriter != null;
    public string MicName => AudioDevices.MicrophoneName(_controller.Settings.MicrophoneId);

    /// <summary>Raised to start or stop preview playback around a voiceover.</summary>
    public event Action<bool>? RecordingChanged;

    private void ToggleVoiceover()
    {
        if (_voiceWriter == null) StartVoiceover();
        else StopVoiceover();
    }

    private void StartVoiceover()
    {
        _voicePath = Path.Combine(ProjectStore.MediaDirectory(_project.Id), $"voice_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        _voiceWriter = new WaveFileWriter(_voicePath, WaveFormat.CreateIeeeFloatWaveFormat(AudioFile.Rate, 2));
        _voiceStart = _playhead;
        _controller.Mic.Subscribe(_voiceSink);
        Notify(nameof(IsRecording));
        RecordingChanged?.Invoke(true);
    }

    private void StopVoiceover()
    {
        _controller.Mic.Unsubscribe(_voiceSink);
        WaveFileWriter? writer = _voiceWriter;
        _voiceWriter = null;
        double seconds = writer?.TotalTime.TotalSeconds ?? 0;
        writer?.Dispose();
        Notify(nameof(IsRecording));
        RecordingChanged?.Invoke(false);
        if (_voicePath == null || seconds < 0.3)
        {
            _main.ShowNotice("No voiceover was recorded. Check your microphone in Settings.");
            return;
        }
        var clip = new AudioClip
        {
            SourcePath = _voicePath, Title = "Voiceover", SourceDuration = seconds, Start = _voiceStart,
            In = 0, Out = seconds, IsVoiceover = true,
        };
        _project.Audio.Add(clip);
        Selected = clip;
        Commit("add");
    }

    private unsafe void OnVoiceSamples(float* stereo, int frames, long timeUs)
    {
        WaveFileWriter? writer = _voiceWriter;
        if (writer == null) return;
        var buffer = new float[frames * 2];
        new ReadOnlySpan<float>(stereo, frames * 2).CopyTo(buffer);
        lock (buffer)
        {
            try { writer.WriteSamples(buffer, 0, buffer.Length); }
            catch (ObjectDisposedException) { /* Stopped mid-packet. */ }
        }
    }

    // Editing

    private void Split()
    {
        double at = _playhead;
        switch (_selected)
        {
            case OverlayLayer layer when at > layer.Start + 0.1 && at < layer.End - 0.1:
            {
                OverlayLayer copy = CloneLayer(layer);
                copy.Start = at;
                layer.End = at;
                _project.Layers.Add(copy);
                Selected = copy;
                break;
            }
            case AudioClip audio when at > audio.Start + 0.1 && at < audio.End - 0.1:
            {
                AudioClip copy = CloneAudio(audio);
                double cut = audio.In + (at - audio.Start);
                copy.Start = at;
                copy.In = cut;
                copy.FadeIn = 0;
                audio.Out = cut;
                audio.FadeOut = 0;
                _project.Audio.Add(copy);
                Selected = copy;
                break;
            }
            default:
            {
                (VideoClip? clip, double start) = _project.ClipAt(at);
                if (clip == null) return;
                double cut = clip.In + (at - start) * clip.Speed;
                if (cut <= clip.In + 0.1 || cut >= clip.Out - 0.1) return;
                VideoClip copy = CloneVideo(clip);
                copy.In = cut;
                copy.Transition = TransitionKind.None;
                clip.Out = cut;
                _project.Video.Insert(_project.Video.IndexOf(clip) + 1, copy);
                Selected = copy;
                break;
            }
        }
        Commit("split");
    }

    private void Delete()
    {
        switch (_selected)
        {
            case VideoClip clip: _project.Video.Remove(clip); break;
            case OverlayLayer layer: _project.Layers.Remove(layer); break;
            case AudioClip audio: _project.Audio.Remove(audio); break;
            default: return;
        }
        Selected = null;
        Commit("delete");
    }

    private void Duplicate()
    {
        switch (_selected)
        {
            case VideoClip clip:
            {
                VideoClip copy = CloneVideo(clip);
                _project.Video.Insert(_project.Video.IndexOf(clip) + 1, copy);
                Selected = copy;
                break;
            }
            case OverlayLayer layer:
            {
                OverlayLayer copy = CloneLayer(layer);
                copy.Start = layer.End;
                copy.End = layer.End + layer.Duration;
                _project.Layers.Add(copy);
                Selected = copy;
                break;
            }
            case AudioClip audio:
            {
                AudioClip copy = CloneAudio(audio);
                copy.Start = audio.End;
                _project.Audio.Add(copy);
                Selected = copy;
                break;
            }
            default: return;
        }
        Commit("duplicate");
    }

    private static VideoClip CloneVideo(VideoClip clip) =>
        System.Text.Json.JsonSerializer.Deserialize<VideoClip>(System.Text.Json.JsonSerializer.Serialize(clip))! is { } copy ? WithNewId(copy) : clip;

    private static OverlayLayer CloneLayer(OverlayLayer layer) =>
        System.Text.Json.JsonSerializer.Deserialize<OverlayLayer>(System.Text.Json.JsonSerializer.Serialize(layer))! is { } copy ? WithNewId(copy) : layer;

    private static AudioClip CloneAudio(AudioClip audio) =>
        System.Text.Json.JsonSerializer.Deserialize<AudioClip>(System.Text.Json.JsonSerializer.Serialize(audio))! is { } copy ? WithNewId(copy) : audio;

    private static VideoClip WithNewId(VideoClip clip) { clip.Id = Guid.NewGuid().ToString("N"); return clip; }
    private static OverlayLayer WithNewId(OverlayLayer layer) { layer.Id = Guid.NewGuid().ToString("N"); return layer; }
    private static AudioClip WithNewId(AudioClip clip) { clip.Id = Guid.NewGuid().ToString("N"); return clip; }

    /// <summary>Moves a video piece to a new position on the main track.</summary>
    public void MoveVideo(VideoClip clip, int index)
    {
        int from = _project.Video.IndexOf(clip);
        if (from < 0) return;
        _project.Video.RemoveAt(from);
        if (index > from) index--;
        _project.Video.Insert(Math.Clamp(index, 0, _project.Video.Count), clip);
        Commit("move");
    }

    // Export

    public bool IsExporting { get => _isExporting; private set => Set(ref _isExporting, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool HasResult => _resultPath != null && !_isExporting;

    private async void Export()
    {
        if (_isExporting || _project.Video.Count == 0) return;
        EditorProject snapshot = _project.Clone();
        string directory = _controller.Settings.ClipDirectory;
        string baseName = RecordingEngine.Sanitize(_project.Name);
        _cancel = new CancellationTokenSource();
        CancellationToken token = _cancel.Token;
        var progress = new Progress<double>(value =>
        {
            Progress = value;
            Status = $"Exporting  {Math.Round(value * 100)}%";
        });

        IsExporting = true;
        _resultPath = null;
        Notify(nameof(HasResult));
        Progress = 0;
        Status = "Preparing audio";
        try
        {
            _resultPath = await Task.Run(() => Exporter.Export(snapshot, directory, baseName, progress, token), token);
            await Task.Run(_controller.Library.Refresh);
            Status = $"Saved {Path.GetFileNameWithoutExtension(_resultPath)}";
        }
        catch (OperationCanceledException)
        {
            Status = "Export cancelled";
        }
        catch (Exception ex)
        {
            Status = "Export failed: " + ex.Message;
        }
        finally
        {
            IsExporting = false;
            Notify(nameof(HasResult));
            _cancel.Dispose();
            _cancel = null;
        }
    }

    public void Dispose()
    {
        _cancel?.Cancel();
        if (_voiceWriter != null) StopVoiceover();
        _saveTimer.Stop();
        ProjectStore.Save(_project);
        _controller.Library.Changed -= _libraryChanged;
    }
}

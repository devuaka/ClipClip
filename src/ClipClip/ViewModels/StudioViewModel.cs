using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ClipClip.Engine;
using ClipClip.Engine.Editing;
using ClipClip.Engine.Library;
using Microsoft.Win32;

namespace ClipClip.ViewModels;

public sealed class SegmentItem : ObservableObject
{
    private bool _isSelected;

    public SegmentItem(EditSegment segment, string title, double sourceLength)
    {
        Segment = segment;
        Title = title;
        SourceLength = sourceLength;
    }

    public EditSegment Segment { get; }
    public string Title { get; }
    public double SourceLength { get; }

    public double In => Segment.In;
    public double Out => Segment.Out;

    public void SetRange(double start, double end)
    {
        Segment.In = start;
        Segment.Out = end;
        Changed();
    }

    public double Speed
    {
        get => Segment.Speed;
        set
        {
            Segment.Speed = value;
            Notify();
            Changed();
        }
    }

    public double Volume
    {
        get => Segment.Volume;
        set
        {
            Segment.Volume = (float)value;
            Notify();
        }
    }

    public bool Muted
    {
        get => Segment.Muted;
        set
        {
            Segment.Muted = value;
            Notify();
        }
    }

    public string Caption
    {
        get => Segment.Caption ?? "";
        set
        {
            Segment.Caption = value;
            Notify();
            Notify(nameof(HasCaption));
        }
    }

    public bool HasCaption => !string.IsNullOrWhiteSpace(Segment.Caption);

    public CaptionPlacement Placement
    {
        get => Segment.Placement;
        set
        {
            Segment.Placement = value;
            Notify();
            Notify(nameof(CaptionAlignment));
        }
    }

    public VerticalAlignment CaptionAlignment => Segment.Placement switch
    {
        CaptionPlacement.Top => VerticalAlignment.Top,
        CaptionPlacement.Middle => VerticalAlignment.Center,
        _ => VerticalAlignment.Bottom,
    };

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public double TimelineWidth => Math.Clamp(Segment.OutputSeconds * 18, 76, 900);
    public string DurationText => ClipItem.FormatTime(Segment.OutputSeconds) + (Segment.Speed != 1 ? $"  ·  {Segment.Speed}x" : "");

    private void Changed()
    {
        Notify(nameof(TimelineWidth));
        Notify(nameof(DurationText));
    }

    public SegmentItem Clone() => new(Segment.Clone(), Title, SourceLength);
}

/// <summary>The Studio: a timeline of pieces from any clips, rendered to one new video.</summary>
public sealed class StudioViewModel : ObservableObject, IDisposable
{
    private readonly ClipController _controller;
    private readonly MainViewModel _main;
    private readonly Action _libraryChanged;
    private SegmentItem? _selected;
    private Choice<ClipInfo>? _clipToAdd;
    private Choice<ExportAspect> _aspect;
    private string? _musicPath;
    private double _musicVolume = 0.5;
    private bool _isExporting;
    private double _progress;
    private string _status = "";
    private string? _resultPath;
    private CancellationTokenSource? _cancel;

    public StudioViewModel(ClipController controller, MainViewModel main)
    {
        _controller = controller;
        _main = main;
        AspectChoices =
        [
            new("Landscape, like the original", ExportAspect.Original),
            new("Vertical 9:16, for TikTok and Shorts", ExportAspect.Vertical),
            new("Square 1:1", ExportAspect.Square),
        ];
        _aspect = AspectChoices[0];
        SpeedChoices = [new("0.25x", 0.25), new("0.5x", 0.5), new("1x", 1), new("1.5x", 1.5), new("2x", 2)];
        PlacementChoices = [new("Top", CaptionPlacement.Top), new("Middle", CaptionPlacement.Middle), new("Bottom", CaptionPlacement.Bottom)];

        AddCommand = new RelayCommand(() =>
        {
            if (_clipToAdd != null) AddClip(_clipToAdd.Value);
        });
        SelectCommand = new RelayCommand(parameter => Selected = parameter as SegmentItem);
        SplitCommand = new RelayCommand(Split);
        DeleteCommand = new RelayCommand(Delete);
        DuplicateCommand = new RelayCommand(Duplicate);
        MoveLeftCommand = new RelayCommand(() => Move(-1));
        MoveRightCommand = new RelayCommand(() => Move(1));
        ChooseMusicCommand = new RelayCommand(ChooseMusic);
        ClearMusicCommand = new RelayCommand(() => MusicPath = null);
        ExportCommand = new RelayCommand(Export);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel());
        ShowResultCommand = new RelayCommand(() =>
        {
            ClipInfo? clip = _controller.Library.Clips.FirstOrDefault(c => c.Path == _resultPath);
            if (clip == null) return;
            _main.Page = Page.Clips;
            _main.Clips.Play(clip);
        });

        _libraryChanged = () => Application.Current.Dispatcher.BeginInvoke(ReloadLibrary);
        controller.Library.Changed += _libraryChanged;
        ReloadLibrary();
    }

    public ObservableCollection<SegmentItem> Segments { get; } = [];
    public ObservableCollection<Choice<ClipInfo>> LibraryClips { get; } = [];
    public IReadOnlyList<Choice<ExportAspect>> AspectChoices { get; }
    public IReadOnlyList<Choice<double>> SpeedChoices { get; }
    public IReadOnlyList<Choice<CaptionPlacement>> PlacementChoices { get; }

    public RelayCommand AddCommand { get; }
    public RelayCommand SelectCommand { get; }
    public RelayCommand SplitCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand MoveLeftCommand { get; }
    public RelayCommand MoveRightCommand { get; }
    public RelayCommand ChooseMusicCommand { get; }
    public RelayCommand ClearMusicCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ShowResultCommand { get; }

    /// <summary>Where the preview is, in seconds of the selected piece's source clip.</summary>
    public double Playhead { get; set; }

    public event Action? SelectionChanged;

    public SegmentItem? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            if (_selected != null) _selected.IsSelected = false;
            _selected = value;
            if (_selected != null) _selected.IsSelected = true;
            Notify();
            Notify(nameof(HasSelection));
            Notify(nameof(SelectedSpeed));
            Notify(nameof(SelectedPlacement));
            SelectionChanged?.Invoke();
        }
    }

    public bool HasSelection => _selected != null;
    public bool HasSegments => Segments.Count > 0;
    public string TotalText => $"{Segments.Count} {(Segments.Count == 1 ? "piece" : "pieces")}  ·  {ClipItem.FormatTime(Segments.Sum(s => s.Segment.OutputSeconds))} total";

    public Choice<ClipInfo>? ClipToAdd { get => _clipToAdd; set => Set(ref _clipToAdd, value); }

    public Choice<double>? SelectedSpeed
    {
        get => _selected == null ? null : SpeedChoices.FirstOrDefault(c => c.Value == _selected.Speed);
        set
        {
            if (_selected == null || value == null) return;
            _selected.Speed = value.Value;
            Notify();
            Notify(nameof(TotalText));
            SelectionChanged?.Invoke();
        }
    }

    public Choice<CaptionPlacement>? SelectedPlacement
    {
        get => _selected == null ? null : PlacementChoices.FirstOrDefault(c => c.Value == _selected.Placement);
        set
        {
            if (_selected == null || value == null) return;
            _selected.Placement = value.Value;
            Notify();
        }
    }

    public Choice<ExportAspect> Aspect
    {
        get => _aspect;
        set
        {
            if (Set(ref _aspect, value)) AspectChanged?.Invoke();
        }
    }

    public event Action? AspectChanged;

    public string? MusicPath
    {
        get => _musicPath;
        private set
        {
            if (!Set(ref _musicPath, value)) return;
            Notify(nameof(MusicName));
            Notify(nameof(HasMusic));
        }
    }

    public string MusicName => _musicPath == null ? "No music" : Path.GetFileName(_musicPath);
    public bool HasMusic => _musicPath != null;
    public double MusicVolume { get => _musicVolume; set => Set(ref _musicVolume, value); }

    public bool IsExporting { get => _isExporting; private set => Set(ref _isExporting, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool HasResult => _resultPath != null && !_isExporting;

    public void AddClip(ClipInfo clip)
    {
        var segment = new EditSegment { SourcePath = clip.Path, In = 0, Out = Math.Max(0.5, clip.DurationSeconds) };
        var item = new SegmentItem(segment, clip.Title, clip.DurationSeconds);
        Segments.Add(item);
        Selected = item;
        SegmentsChanged();
    }

    private void SegmentsChanged()
    {
        Notify(nameof(HasSegments));
        Notify(nameof(TotalText));
    }

    private void ReloadLibrary()
    {
        LibraryClips.Clear();
        foreach (ClipInfo clip in _controller.Library.Clips)
            LibraryClips.Add(new Choice<ClipInfo>($"{clip.Title}  ({ClipItem.FormatTime(clip.DurationSeconds)})", clip));
        ClipToAdd ??= LibraryClips.FirstOrDefault();
    }

    private void Split()
    {
        SegmentItem? item = _selected;
        if (item == null) return;
        double at = Playhead;
        if (at <= item.In + 0.25 || at >= item.Out - 0.25)
        {
            _main.ShowNotice("Move the preview into the middle of the piece, then split.");
            return;
        }
        SegmentItem right = item.Clone();
        right.SetRange(at, item.Out);
        item.SetRange(item.In, at);
        Segments.Insert(Segments.IndexOf(item) + 1, right);
        Selected = right;
        SegmentsChanged();
    }

    private void Delete()
    {
        SegmentItem? item = _selected;
        if (item == null) return;
        int index = Segments.IndexOf(item);
        Segments.Remove(item);
        Selected = Segments.Count == 0 ? null : Segments[Math.Min(index, Segments.Count - 1)];
        SegmentsChanged();
    }

    private void Duplicate()
    {
        SegmentItem? item = _selected;
        if (item == null) return;
        SegmentItem copy = item.Clone();
        Segments.Insert(Segments.IndexOf(item) + 1, copy);
        Selected = copy;
        SegmentsChanged();
    }

    private void Move(int direction)
    {
        SegmentItem? item = _selected;
        if (item == null) return;
        int index = Segments.IndexOf(item);
        int target = index + direction;
        if (target < 0 || target >= Segments.Count) return;
        Segments.Move(index, target);
    }

    private void ChooseMusic()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose background music",
            Filter = "Audio|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac;*.mp4|All files|*.*",
        };
        if (dialog.ShowDialog() == true) MusicPath = dialog.FileName;
    }

    private async void Export()
    {
        if (_isExporting || Segments.Count == 0) return;
        var project = new EditProject
        {
            Aspect = _aspect.Value,
            MusicPath = _musicPath,
            MusicVolume = (float)_musicVolume,
        };
        foreach (SegmentItem item in Segments) project.Segments.Add(item.Segment.Clone());

        string directory = _controller.Settings.ClipDirectory;
        string baseName = RecordingEngine.Sanitize(Segments[0].Title) + "_edit";
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
        Status = "Starting export";
        try
        {
            _resultPath = await Task.Run(() => Exporter.Export(project, directory, baseName, progress, token), token);
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
        _controller.Library.Changed -= _libraryChanged;
    }
}

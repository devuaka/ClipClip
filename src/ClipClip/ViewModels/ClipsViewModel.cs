using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClipClip.Engine;
using ClipClip.Engine.Library;
using Microsoft.Win32;

namespace ClipClip.ViewModels;

public enum ClipSort { Newest, Oldest, Name }

public sealed class ClipsViewModel : ObservableObject, IDisposable
{
    private readonly ClipController _controller;
    private readonly MainViewModel _main;
    private readonly Action _libraryChanged;
    private string _search = "";
    private Choice<ClipSort> _sort;
    private ClipItem? _playing;
    private string _renameText = "";
    private bool _isRenaming;
    private ClipInfo? _pendingPlay;
    private bool _favoritesOnly;
    private bool _isEditing;
    private double _trimStart;
    private double _trimEnd;
    private bool _removeAudio;
    private bool _savingTrim;

    public ClipsViewModel(ClipController controller, MainViewModel main)
    {
        _controller = controller;
        _main = main;
        SortOptions =
        [
            new("Newest first", ClipSort.Newest),
            new("Oldest first", ClipSort.Oldest),
            new("Name", ClipSort.Name),
        ];
        _sort = SortOptions[0];

        PlayCommand = new RelayCommand(parameter =>
        {
            if (parameter is ClipItem item) Play(item.Info);
        });
        ClosePlayerCommand = new RelayCommand(ClosePlayer);
        ExportCommand = new RelayCommand(parameter => Export(ItemFrom(parameter)));
        CopyCommand = new RelayCommand(parameter => Copy(ItemFrom(parameter)));
        OpenFolderCommand = new RelayCommand(parameter => OpenFolder(ItemFrom(parameter)));
        OpenExternalCommand = new RelayCommand(parameter => OpenExternal(ItemFrom(parameter)));
        DeleteCommand = new RelayCommand(parameter => Delete(ItemFrom(parameter)));
        StartRenameCommand = new RelayCommand(StartRename);
        ConfirmRenameCommand = new RelayCommand(ConfirmRename);
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        OpenClipFolderCommand = new RelayCommand(() => Shell(_controller.Settings.ClipDirectory));
        ToggleFavoriteCommand = new RelayCommand(parameter => ToggleFavorite(ItemFrom(parameter)));
        ToggleEditCommand = new RelayCommand(ToggleEdit);
        OpenInStudioCommand = new RelayCommand(parameter =>
        {
            if (ItemFrom(parameter) is { } item) _main.OpenInStudio(item.Info);
        });
        SaveTrimCommand = new RelayCommand(SaveTrim);

        _libraryChanged = () => Application.Current.Dispatcher.BeginInvoke(Reload);
        controller.Library.Changed += _libraryChanged;
        Reload();
    }

    public ObservableCollection<ClipItem> Items { get; } = [];
    public IReadOnlyList<Choice<ClipSort>> SortOptions { get; }

    public RelayCommand PlayCommand { get; }
    public RelayCommand ClosePlayerCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenExternalCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand StartRenameCommand { get; }
    public RelayCommand ConfirmRenameCommand { get; }
    public RelayCommand CancelRenameCommand { get; }
    public RelayCommand OpenClipFolderCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand ToggleEditCommand { get; }
    public RelayCommand OpenInStudioCommand { get; }
    public RelayCommand SaveTrimCommand { get; }

    /// <summary>The player must let go of a file before it can be renamed or deleted.</summary>
    public event Action? ReleaseMedia;

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value)) Reload();
        }
    }

    public Choice<ClipSort> Sort
    {
        get => _sort;
        set
        {
            if (Set(ref _sort, value)) Reload();
        }
    }

    public ClipItem? Playing
    {
        get => _playing;
        private set
        {
            if (Set(ref _playing, value)) Notify(nameof(IsPlayerOpen));
        }
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (Set(ref _favoritesOnly, value)) Reload();
        }
    }

    public bool IsEditing { get => _isEditing; private set => Set(ref _isEditing, value); }
    public bool RemoveAudio { get => _removeAudio; set => Set(ref _removeAudio, value); }
    public double TrimStart => _trimStart;
    public double TrimEnd => _trimEnd;
    public double ClipLength => _playing?.Info.DurationSeconds ?? 0;
    public string TrimSummary =>
        $"Keeps {ClipItem.FormatTime(_trimStart)} to {ClipItem.FormatTime(_trimEnd)}, {ClipItem.FormatTime(_trimEnd - _trimStart)} long";

    /// <summary>Raised when the kept range changes, so the view can redraw it.</summary>
    public event Action? TrimChanged;

    public void SetTrimStart(double seconds)
    {
        _trimStart = Math.Clamp(seconds, 0, Math.Max(0, _trimEnd - 1));
        TrimUpdated();
    }

    public void SetTrimEnd(double seconds)
    {
        _trimEnd = Math.Clamp(seconds, Math.Min(_trimStart + 1, ClipLength), ClipLength);
        TrimUpdated();
    }

    private void TrimUpdated()
    {
        Notify(nameof(TrimSummary));
        TrimChanged?.Invoke();
    }

    private void ToggleEdit()
    {
        IsEditing = !_isEditing;
        RemoveAudio = false;
        _trimStart = 0;
        _trimEnd = ClipLength;
        TrimUpdated();
    }

    private async void SaveTrim()
    {
        ClipItem? item = _playing;
        if (item == null || _savingTrim) return;
        _savingTrim = true;
        string source = item.Info.Path;
        double start = _trimStart, end = _trimEnd;
        bool removeAudio = _removeAudio;
        try
        {
            string saved = await Task.Run(() => ClipEditor.Trim(source, start, end, removeAudio));
            await Task.Run(_controller.Library.Refresh);
            IsEditing = false;
            _main.ShowNotice($"Saved as {Path.GetFileNameWithoutExtension(saved)}. The original is unchanged.");
        }
        catch (Exception ex)
        {
            _main.ShowNotice("Could not save the edit: " + ex.Message);
        }
        finally
        {
            _savingTrim = false;
        }
    }

    private void ToggleFavorite(ClipItem? item)
    {
        if (item == null) return;
        item.IsFavorite = !item.IsFavorite;
        _controller.Library.SetFavorite(item.Info, item.IsFavorite);
        if (_favoritesOnly) Reload();
    }

    public bool IsPlayerOpen => _playing != null;
    public bool IsEmpty => Items.Count == 0;
    public bool LibraryHasClips => _controller.Library.Clips.Count > 0;
    public string CountText => $"{Items.Count} {(Items.Count == 1 ? "clip" : "clips")}";

    public string RenameText { get => _renameText; set => Set(ref _renameText, value); }
    public bool IsRenaming { get => _isRenaming; private set => Set(ref _isRenaming, value); }

    public void Play(ClipInfo info)
    {
        ClipItem? item = Items.FirstOrDefault(i => i.Info.Path == info.Path);
        if (item == null)
        {
            // Filtered out of view. Clear the search so the clip can be found again after closing.
            _pendingPlay = info;
            Search = "";
            return;
        }
        IsRenaming = false;
        IsEditing = false;
        Playing = item;
    }

    public void ClosePlayer()
    {
        IsRenaming = false;
        IsEditing = false;
        ReleaseMedia?.Invoke();
        Playing = null;
    }

    private void Reload()
    {
        IEnumerable<ClipInfo> clips = _controller.Library.Clips;
        if (_favoritesOnly) clips = clips.Where(c => c.IsFavorite);
        string query = _search.Trim();
        if (query.Length > 0)
        {
            clips = clips.Where(c =>
                c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (c.Game?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        clips = _sort.Value switch
        {
            ClipSort.Oldest => clips.OrderBy(c => c.Created),
            ClipSort.Name => clips.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => clips.OrderByDescending(c => c.Created),
        };

        var previous = Items.ToDictionary(i => i.Info.Path, StringComparer.OrdinalIgnoreCase);
        Items.Clear();
        foreach (ClipInfo clip in clips)
            Items.Add(previous.TryGetValue(clip.Path, out ClipItem? known) && known.Info.SizeBytes == clip.SizeBytes ? known : new ClipItem(clip));

        Notify(nameof(IsEmpty));
        Notify(nameof(LibraryHasClips));
        Notify(nameof(CountText));

        if (_playing != null && !Items.Contains(_playing) && !File.Exists(_playing.Info.Path)) ClosePlayer();
        if (_pendingPlay != null)
        {
            ClipInfo pending = _pendingPlay;
            _pendingPlay = null;
            Play(pending);
        }
    }

    private ClipItem? ItemFrom(object? parameter) => parameter as ClipItem ?? _playing;

    private async void Export(ClipItem? item)
    {
        if (item == null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export clip",
            FileName = Path.GetFileName(item.Info.Path),
            Filter = "MP4 video|*.mp4",
            DefaultExt = ".mp4",
        };
        if (dialog.ShowDialog() != true) return;

        string source = item.Info.Path;
        string target = dialog.FileName;
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            await Task.Run(() => File.Copy(source, target, true));
            _main.ShowNotice($"Exported to {target}");
        }
        catch (Exception ex)
        {
            _main.ShowNotice("Export failed: " + ex.Message);
        }
    }

    private void Copy(ClipItem? item)
    {
        if (item == null) return;
        var files = new System.Collections.Specialized.StringCollection { item.Info.Path };
        Clipboard.SetFileDropList(files);
        _main.ShowNotice("Clip copied. Paste it into Discord, a folder or anywhere else.");
    }

    private void OpenFolder(ClipItem? item)
    {
        if (item != null) Process.Start("explorer.exe", $"/select,\"{item.Info.Path}\"");
    }

    private void OpenExternal(ClipItem? item)
    {
        if (item != null) Shell(item.Info.Path);
    }

    private void Shell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _main.ShowNotice("Could not open it: " + ex.Message);
        }
    }

    private void Delete(ClipItem? item)
    {
        if (item == null) return;
        if (!Views.ConfirmDialog.Ask("Delete this clip?", $"\"{item.Title}\" goes to the Recycle Bin.", "Delete")) return;

        bool wasPlaying = _playing == item;
        if (wasPlaying) ClosePlayer();
        // The player can hold the file briefly after being told to stop.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => Remove(item));
    }

    private void Remove(ClipItem item)
    {
        try
        {
            _controller.Library.Delete(item.Info);
            _main.ShowNotice("Clip moved to the Recycle Bin");
        }
        catch (Exception ex)
        {
            _main.ShowNotice("Delete failed: " + ex.Message);
        }
    }

    private void StartRename()
    {
        if (_playing == null) return;
        RenameText = _playing.Title;
        IsRenaming = true;
    }

    private void ConfirmRename()
    {
        ClipItem? item = _playing;
        if (item == null) return;
        string title = _renameText.Trim();
        IsRenaming = false;
        if (title.Length == 0 || title == item.Title) return;

        // The player reopens the file under its new name after the move.
        ReleaseMedia?.Invoke();
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            try
            {
                _controller.Library.Rename(item.Info, title);
                item.TitleChanged();
                Notify(nameof(Playing));
                _main.ShowNotice("Clip renamed");
            }
            catch (Exception ex)
            {
                _main.ShowNotice("Rename failed: " + ex.Message);
            }
            MediaReopen?.Invoke();
        });
    }

    public event Action? MediaReopen;

    public void Dispose() => _controller.Library.Changed -= _libraryChanged;
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using ClipClip.Engine;
using ClipClip.Engine.Core;
using ClipClip.Engine.Detection;
using Microsoft.Win32;

namespace ClipClip.ViewModels;

public sealed class CustomGameRow : ObservableObject
{
    private readonly CustomGame _game;
    private readonly Action _changed;

    public CustomGameRow(CustomGame game, Action changed)
    {
        _game = game;
        _changed = changed;
    }

    public CustomGame Game => _game;
    public string ExePath => _game.ExePath;

    public string Name
    {
        get => _game.Name;
        set
        {
            string name = value.Trim();
            if (name.Length == 0 || name == _game.Name) return;
            _game.Name = name;
            Notify();
            _changed();
        }
    }

    public bool Enabled
    {
        get => _game.Enabled;
        set
        {
            if (value == _game.Enabled) return;
            _game.Enabled = value;
            Notify();
            _changed();
        }
    }
}

/// <summary>A game ClipClip found by itself. Off puts its folder or exe on the ignore list.</summary>
public sealed class DetectedGameRow : ObservableObject
{
    private readonly string _key;
    private readonly AppSettings _settings;
    private readonly Action _changed;

    public DetectedGameRow(string name, string source, string key, AppSettings settings, Action changed)
    {
        Name = name;
        Source = source;
        _key = key;
        _settings = settings;
        _changed = changed;
    }

    public string Name { get; }
    public string Source { get; }

    public bool Enabled
    {
        get => !_settings.IgnoredExes.Contains(_key, StringComparer.OrdinalIgnoreCase);
        set
        {
            if (value == Enabled) return;
            if (value) _settings.IgnoredExes.RemoveAll(e => string.Equals(e, _key, StringComparison.OrdinalIgnoreCase));
            else _settings.IgnoredExes.Add(_key);
            Notify();
            _changed();
        }
    }
}

public sealed class GamesViewModel : ObservableObject, IDisposable
{
    private readonly ClipController _controller;
    private readonly MainViewModel _main;
    private readonly Action<IReadOnlyList<InstalledGame>> _scanned;
    private List<DetectedGameRow> _allDetected = [];
    private string _filter = "";
    private string _currentName = "None";
    private string _currentDetail = "";
    private bool _hasGame;

    public GamesViewModel(ClipController controller, MainViewModel main)
    {
        _controller = controller;
        _main = main;
        foreach (CustomGame game in controller.Settings.CustomGames)
            Custom.Add(new CustomGameRow(game, Apply));

        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(parameter =>
        {
            if (parameter is not CustomGameRow row) return;
            Custom.Remove(row);
            controller.Settings.CustomGames.Remove(row.Game);
            Apply();
            Notify(nameof(HasCustom));
        });

        _scanned = _ => Application.Current.Dispatcher.BeginInvoke(ReloadDetected);
        controller.InstalledGamesChanged += _scanned;
        ReloadDetected();
    }

    public ObservableCollection<CustomGameRow> Custom { get; } = [];
    public ObservableCollection<DetectedGameRow> Detected { get; } = [];
    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public bool HasCustom => Custom.Count > 0;
    public string DetectedCount => $"{_allDetected.Count} found on this PC";

    public bool AutoDetect
    {
        get => _controller.Settings.AutoDetectGames;
        set
        {
            if (value == _controller.Settings.AutoDetectGames) return;
            _controller.Settings.AutoDetectGames = value;
            Apply();
            Notify();
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) ApplyFilter();
        }
    }

    public string CurrentName { get => _currentName; private set => Set(ref _currentName, value); }
    public string CurrentDetail { get => _currentDetail; private set => Set(ref _currentDetail, value); }
    public bool HasGame { get => _hasGame; private set => Set(ref _hasGame, value); }

    public void Update()
    {
        DetectedGame? game = _controller.CurrentGame;
        HasGame = game != null;
        CurrentName = game?.Name ?? "No game running";
        CurrentDetail = game == null
            ? (_controller.Settings.RecordOutsideGames ? "Desktop recording is on, so ClipClip records anyway." : "ClipClip starts recording when a game launches.")
            : $"{(game.IsFullscreen ? "Fullscreen" : "Windowed")}  ·  found by {SourceText(game.Source)}";
    }

    private static string SourceText(GameSource source) => source switch
    {
        GameSource.Custom => "your custom list",
        GameSource.Library => "your game library",
        GameSource.KnownGame => "its name",
        GameSource.Engine => "its game engine",
        _ => "fullscreen detection",
    };

    private void Add()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a game executable",
            Filter = "Programs (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        string path = dialog.FileName;
        if (_controller.Settings.CustomGames.Any(g => string.Equals(g.ExePath, path, StringComparison.OrdinalIgnoreCase)))
        {
            _main.ShowNotice("That game is already on your list");
            return;
        }

        var game = new CustomGame { Name = DisplayName(path), ExePath = path };
        _controller.Settings.CustomGames.Add(game);
        Custom.Add(new CustomGameRow(game, Apply));
        Notify(nameof(HasCustom));
        Apply();
    }

    private static string DisplayName(string path)
    {
        try
        {
            string? product = FileVersionInfo.GetVersionInfo(path).ProductName?.Trim();
            if (!string.IsNullOrEmpty(product)) return product;
        }
        catch (IOException)
        {
            // Fall back to the file name.
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    private void ReloadDetected()
    {
        AppSettings settings = _controller.Settings;
        List<CustomGame> seen;
        lock (settings.SeenGames) seen = settings.SeenGames.ToList();

        _allDetected = _controller.InstalledGames
            .Select(g => new DetectedGameRow(g.Name, g.Store, g.InstallDir, settings, Apply))
            .Concat(seen.Select(g => new DetectedGameRow(g.Name, "Seen running", g.ExePath, settings, Apply)))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ApplyFilter();
        Notify(nameof(DetectedCount));
    }

    private void ApplyFilter()
    {
        Detected.Clear();
        string query = _filter.Trim();
        foreach (DetectedGameRow row in _allDetected)
        {
            if (query.Length == 0 || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                Detected.Add(row);
        }
    }

    private void Apply() => _controller.ApplySettings();

    public void Dispose() => _controller.InstalledGamesChanged -= _scanned;
}

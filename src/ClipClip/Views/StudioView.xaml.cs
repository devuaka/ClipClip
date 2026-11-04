using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClipClip.Engine.Editing;
using ClipClip.ViewModels;

namespace ClipClip.Views;

/// <summary>
/// The preview plays the selected piece at its speed and volume, with the caption and the
/// export frame drawn over it, so what you see is close to what export makes.
/// </summary>
public partial class StudioView : UserControl
{
    private readonly DispatcherTimer _timer;
    private StudioViewModel? _viewModel;
    private SegmentItem? _watched;
    private MediaElement? _player;
    private string? _loadedPath;
    private bool _playing;
    private bool _scrubbing;

    public StudioView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Tick();
        DataContextChanged += OnContextChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) Pause();
        };
        Unloaded += (_, _) => Release();
    }

    private MediaElement Player
    {
        get
        {
            if (_player != null) return _player;
            _player = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true };
            _player.MediaOpened += (_, _) =>
            {
                SeekTo(_viewModel?.Selected?.In ?? 0);
                UpdateFrames();
            };
            PreviewHost.Child = _player;
            return _player;
        }
    }

    private void OnContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.SelectionChanged -= OnSelectionChanged;
            _viewModel.AspectChanged -= UpdateFrames;
        }
        _viewModel = e.NewValue as StudioViewModel;
        if (_viewModel != null)
        {
            _viewModel.SelectionChanged += OnSelectionChanged;
            _viewModel.AspectChanged += UpdateFrames;
        }
    }

    private void OnSelectionChanged()
    {
        if (_watched != null) _watched.PropertyChanged -= OnPieceChanged;
        _watched = _viewModel?.Selected;
        if (_watched != null) _watched.PropertyChanged += OnPieceChanged;

        SegmentItem? piece = _watched;
        if (piece == null)
        {
            Pause();
            return;
        }
        Scrub.Minimum = piece.In;
        Scrub.Maximum = piece.Out;
        ApplyAudio();
        if (_loadedPath != piece.Segment.SourcePath)
        {
            _loadedPath = piece.Segment.SourcePath;
            Player.Source = new Uri(_loadedPath);
            Player.Pause();
        }
        else
        {
            SeekTo(piece.In);
        }
        Player.SpeedRatio = piece.Speed;
    }

    private void OnPieceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_watched == null || _player == null) return;
        if (e.PropertyName is nameof(SegmentItem.Volume) or nameof(SegmentItem.Muted)) ApplyAudio();
        if (e.PropertyName == nameof(SegmentItem.Speed)) _player.SpeedRatio = _watched.Speed;
    }

    private void ApplyAudio()
    {
        if (_watched == null) return;
        Player.Volume = _watched.Muted ? 0 : Math.Min(1, _watched.Volume * 0.8);
    }

    private void Tick()
    {
        if (_player == null || _watched == null) return;
        double position = _player.Position.TotalSeconds;
        if (_playing && position >= _watched.Out)
        {
            Pause();
            SeekTo(_watched.In);
            position = _watched.In;
        }
        if (!_scrubbing) Scrub.Value = position;
        if (_viewModel != null) _viewModel.Playhead = position;
        TimeText.Text = ClipItem.FormatTime(position - _watched.In);
    }

    private void SeekTo(double seconds)
    {
        if (_player == null) return;
        _player.Position = TimeSpan.FromSeconds(seconds);
        Scrub.Value = seconds;
        if (_viewModel != null) _viewModel.Playhead = seconds;
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_watched == null) return;
        if (_playing)
        {
            Pause();
            return;
        }
        if (Player.Position.TotalSeconds >= _watched.Out - 0.05) SeekTo(_watched.In);
        Player.SpeedRatio = _watched.Speed;
        Player.Play();
        _playing = true;
        PlayGlyph.Data = (Geometry)FindResource("IconPause");
        _timer.Start();
    }

    private void OnStageClick(object sender, MouseButtonEventArgs e) => OnPlayPause(sender, e);

    private void Pause()
    {
        _player?.Pause();
        _playing = false;
        PlayGlyph.Data = (Geometry)FindResource("IconPlay");
        Tick();
    }

    private void OnScrubDown(object sender, RoutedEventArgs e)
    {
        _scrubbing = true;
        _timer.Start();
    }

    private void OnScrubUp(object sender, RoutedEventArgs e)
    {
        _scrubbing = false;
        SeekTo(Scrub.Value);
        Tick();
    }

    private void OnStageSized(object sender, SizeChangedEventArgs e) => UpdateFrames();

    /// <summary>Places the export frame and caption over the part of the video that will be kept.</summary>
    private void UpdateFrames()
    {
        if (_player == null || _viewModel == null || _player.NaturalVideoWidth == 0)
        {
            CropFrame.Visibility = Visibility.Collapsed;
            return;
        }
        double videoWidth = _player.NaturalVideoWidth, videoHeight = _player.NaturalVideoHeight;
        double scale = Math.Min(Stage.ActualWidth / videoWidth, Stage.ActualHeight / videoHeight);
        double shownWidth = videoWidth * scale, shownHeight = videoHeight * scale;
        double left = (Stage.ActualWidth - shownWidth) / 2, top = (Stage.ActualHeight - shownHeight) / 2;

        double target = _viewModel.Aspect.Value switch
        {
            ExportAspect.Vertical => 9.0 / 16,
            ExportAspect.Square => 1,
            _ => videoWidth / videoHeight,
        };
        double frameWidth = shownWidth, frameHeight = shownHeight;
        if (shownWidth / shownHeight > target) frameWidth = shownHeight * target;
        else frameHeight = shownWidth / target;

        var margin = new Thickness(left + (shownWidth - frameWidth) / 2, top + (shownHeight - frameHeight) / 2, 0, 0);
        CropFrame.Margin = margin;
        CropFrame.Width = frameWidth;
        CropFrame.Height = frameHeight;
        CropFrame.Visibility = _viewModel.Aspect.Value == ExportAspect.Original ? Visibility.Collapsed : Visibility.Visible;
        CaptionBox.Margin = margin;
        CaptionBox.Width = frameWidth;
        CaptionBox.Height = frameHeight;
    }

    private void Release()
    {
        _timer.Stop();
        if (_player == null) return;
        _player.Stop();
        _player.Close();
        _player.Source = null;
        _loadedPath = null;
    }
}

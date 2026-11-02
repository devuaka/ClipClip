using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClipClip.ViewModels;

namespace ClipClip.Views;

public partial class ClipsView : UserControl
{
    private readonly DispatcherTimer _position;
    private ClipsViewModel? _viewModel;
    private bool _seeking;
    private bool _playing;
    private MediaElement? _player;

    /// <summary>Created on first use. Loading the media stack is slow and most visits never need it.</summary>
    private MediaElement Player
    {
        get
        {
            if (_player != null) return _player;
            _player = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                ScrubbingEnabled = true,
                Volume = Volume.Value,
            };
            _player.MediaOpened += OnMediaOpened;
            _player.MediaEnded += OnMediaEnded;
            _player.MediaFailed += OnMediaFailed;
            PlayerHost.Child = _player;
            return _player;
        }
    }

    public ClipsView()
    {
        InitializeComponent();
        _position = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _position.Tick += (_, _) => UpdatePosition();
        DataContextChanged += OnContextChanged;
        Unloaded += (_, _) => Release();
    }

    private void OnContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.ReleaseMedia -= Release;
            _viewModel.MediaReopen -= Reopen;
            _viewModel.TrimChanged -= DrawTrim;
        }
        _viewModel = e.NewValue as ClipsViewModel;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.ReleaseMedia += Release;
            _viewModel.MediaReopen += Reopen;
            _viewModel.TrimChanged += DrawTrim;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClipsViewModel.Playing) && _viewModel?.Playing != null) Reopen();
    }

    private void Reopen()
    {
        ClipItem? item = _viewModel?.Playing;
        if (item == null) return;
        PlayerError.Visibility = Visibility.Collapsed;
        Player.Source = new Uri(item.Info.Path);
        Player.Play();
        SetPlaying(true);
    }

    private void Release()
    {
        _position.Stop();
        if (_player == null) return;
        Player.Stop();
        Player.Close();
        Player.Source = null;
        _playing = false;
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        PlayGlyph.Data = (Geometry)FindResource(playing ? "IconPause" : "IconPlay");
        if (playing) _position.Start();
        else _position.Stop();
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (!Player.NaturalDuration.HasTimeSpan) return;
        TimeSpan duration = Player.NaturalDuration.TimeSpan;
        Seek.Maximum = duration.TotalSeconds;
        Seek.Value = 0;
        DurationText.Text = ClipItem.FormatTime(duration.TotalSeconds);
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Pause();
        Player.Position = TimeSpan.Zero;
        SetPlaying(false);
        UpdatePosition();
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        SetPlaying(false);
        PlayerError.Text = "This clip can't play here. Use the open button to try your default player.";
        PlayerError.Visibility = Visibility.Visible;
    }

    private void UpdatePosition()
    {
        if (_player == null) return;
        double seconds = Player.Position.TotalSeconds;
        if (!_seeking) Seek.Value = seconds;
        PositionText.Text = ClipItem.FormatTime(_seeking ? Seek.Value : seconds);
    }

    private void TogglePlay()
    {
        if (_playing) Player.Pause();
        else Player.Play();
        SetPlaying(!_playing);
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => TogglePlay();

    private void OnVideoClick(object sender, MouseButtonEventArgs e) => TogglePlay();

    private void OnSeekStart(object sender, RoutedEventArgs e) => _seeking = true;

    private void OnSeekEnd(object sender, RoutedEventArgs e)
    {
        _seeking = false;
        Player.Position = TimeSpan.FromSeconds(Seek.Value);
    }

    private void OnSeekClick(object sender, MouseButtonEventArgs e)
    {
        if (_seeking) return;
        Player.Position = TimeSpan.FromSeconds(Seek.Value);
        UpdatePosition();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player != null) _player.Volume = e.NewValue;
    }

    private void OnTrimStart(object sender, RoutedEventArgs e) => _viewModel?.SetTrimStart(Player.Position.TotalSeconds);

    private void OnTrimEnd(object sender, RoutedEventArgs e) => _viewModel?.SetTrimEnd(Player.Position.TotalSeconds);

    private void OnTrimTrackSized(object sender, SizeChangedEventArgs e) => DrawTrim();

    private void DrawTrim()
    {
        if (_viewModel == null || _viewModel.ClipLength <= 0) return;
        double scale = TrimTrack.ActualWidth / _viewModel.ClipLength;
        TrimFill.Margin = new Thickness(_viewModel.TrimStart * scale, 0, 0, 0);
        TrimFill.Width = Math.Max(2, (_viewModel.TrimEnd - _viewModel.TrimStart) * scale);
    }

    private void OnStartRename(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            RenameBox.Focus();
            RenameBox.SelectAll();
        });

    private void OnRenameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _viewModel?.ConfirmRenameCommand.Execute(null);
        else if (e.Key == Key.Escape) _viewModel?.CancelRenameCommand.Execute(null);
    }
}

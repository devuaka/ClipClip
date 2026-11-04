using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipClip.Engine.Editing;
using ClipClip.ViewModels;
using NAudio.Wave;

namespace ClipClip.Views;

/// <summary>
/// The editor screen. Paused, the preview shows the exact export frame at the playhead. Playing,
/// frames are rendered against the audio clock while the mixed sound plays, so effects, text and
/// transitions all show live.
/// </summary>
public partial class StudioView : UserControl
{
    private readonly PreviewRenderer _renderer = new();
    private readonly Dictionary<string, Size> _imageSizes = new(StringComparer.OrdinalIgnoreCase);
    private StudioViewModel? _viewModel;
    private WriteableBitmap? _bitmap;
    private bool _playing;
    private bool _recording;
    private EditorProject? _playSnapshot;
    private double _playStart;
    private readonly Stopwatch _clock = new();
    private WasapiOut? _audio;
    private string? _mixKey;
    private float[]? _mix;
    private Point _boxGrab;
    private (double X, double Y) _boxOrigin;
    private bool _boxDragging;

    public StudioView()
    {
        InitializeComponent();
        _renderer.FrameReady += OnFrameReady;
        DataContextChanged += OnContextChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) StopPlayback();
            else RequestFrame();
        };
        Unloaded += (_, _) =>
        {
            StopPlayback();
            _renderer.Dispose();
        };
    }

    private void OnContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.ProjectChanged -= OnProjectChanged;
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.RecordingChanged -= OnRecordingChanged;
        }
        _viewModel = e.NewValue as StudioViewModel;
        Timeline.Model = _viewModel;
        if (_viewModel != null)
        {
            _viewModel.ProjectChanged += OnProjectChanged;
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.RecordingChanged += OnRecordingChanged;
            RequestFrame();
        }
    }

    private void OnProjectChanged()
    {
        if (!_playing) RequestFrame();
        UpdateLayerBox();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StudioViewModel.Playhead)) return;
        if (!_playing) RequestFrame();
        Timeline.InvalidateVisual();
        KeepPlayheadVisible();
    }

    private void RequestFrame()
    {
        if (_viewModel == null || !IsVisible) return;
        _renderer.Request(_viewModel.Project.Clone(), _viewModel.Playhead);
    }

    private void OnFrameReady(byte[] pixels, int width, int height, double time)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            {
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                PreviewImage.Source = _bitmap;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
            UpdateLayerBox();
        });
    }

    // Playback

    private void OnPlay(object sender, RoutedEventArgs e) => TogglePlay();

    private void OnStageDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == LayerBox) return;
        Focus();
        TogglePlay();
    }

    private void TogglePlay()
    {
        if (_playing) StopPlayback();
        else _ = StartPlayback(withSound: true);
    }

    private async Task StartPlayback(bool withSound)
    {
        if (_viewModel == null || _playing || _viewModel.Project.Duration <= 0) return;
        EditorProject snapshot = _viewModel.Project.Clone();
        if (_viewModel.Playhead >= snapshot.Duration - 0.05) _viewModel.Playhead = 0;
        _playing = true;
        PlayGlyph.Data = (Geometry)FindResource("IconPause");
        _playSnapshot = snapshot;
        _playStart = _viewModel.Playhead;

        if (withSound)
        {
            string key = snapshot.ToJson();
            if (key != _mixKey || _mix == null)
            {
                _mix = await Task.Run(() => Exporter.MixAudio(snapshot));
                _mixKey = key;
            }
            if (!_playing) return;
            StartAudio(_mix, _playStart);
        }
        _clock.Restart();
        CompositionTarget.Rendering += OnRendering;
    }

    private void StartAudio(float[] mix, double from)
    {
        int offset = (int)(from * AudioFile.Rate) * 2;
        if (offset >= mix.Length) return;
        byte[] bytes = new byte[(mix.Length - offset) * 4];
        Buffer.BlockCopy(mix, offset * 4, bytes, 0, bytes.Length);
        var stream = new RawSourceWaveStream(new MemoryStream(bytes), WaveFormat.CreateIeeeFloatWaveFormat(AudioFile.Rate, 2));
        _audio = new WasapiOut();
        _audio.Init(stream);
        _audio.Play();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_viewModel == null || _playSnapshot == null) return;
        double time = _playStart + _clock.Elapsed.TotalSeconds;
        if (time >= _playSnapshot.Duration)
        {
            if (_recording)
            {
                _viewModel.RecordVoiceCommand.Execute(null);
                return;
            }
            StopPlayback();
            return;
        }
        _viewModel.Playhead = time;
        _renderer.Request(_playSnapshot, time);
    }

    private void StopPlayback()
    {
        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
        _audio?.Stop();
        _audio?.Dispose();
        _audio = null;
        if (!_playing) return;
        _playing = false;
        PlayGlyph.Data = (Geometry)FindResource("IconPlay");
        RequestFrame();
    }

    private void OnRecordingChanged(bool recording)
    {
        _recording = recording;
        StopPlayback();
        if (recording) _ = StartPlayback(withSound: false);
        else if (_viewModel?.SelectedAudio is { } clip) Timeline.ForgetMedia(clip.SourcePath);
    }

    private void KeepPlayheadVisible()
    {
        if (_viewModel == null) return;
        double x = 14 + _viewModel.Playhead * _viewModel.PixelsPerSecond;
        if (x < TimelineScroll.HorizontalOffset + 20 || x > TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth - 40)
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - TimelineScroll.ViewportWidth * 0.3));
    }

    // Moving text and stickers on the preview

    private Rect FrameRect()
    {
        if (_bitmap == null || Stage.ActualWidth <= 0) return Rect.Empty;
        double scale = Math.Min(Stage.ActualWidth / _bitmap.PixelWidth, Stage.ActualHeight / _bitmap.PixelHeight);
        double width = _bitmap.PixelWidth * scale, height = _bitmap.PixelHeight * scale;
        return new Rect((Stage.ActualWidth - width) / 2, (Stage.ActualHeight - height) / 2, width, height);
    }

    private void OnStageSized(object sender, SizeChangedEventArgs e) => UpdateLayerBox();

    private void UpdateLayerBox()
    {
        OverlayLayer? layer = _viewModel?.SelectedLayer;
        Rect frame = FrameRect();
        double now = _viewModel?.Playhead ?? 0;
        if (layer == null || frame.IsEmpty || now < layer.Start || now >= layer.End)
        {
            LayerBox.Visibility = Visibility.Collapsed;
            return;
        }

        double width, height;
        if (layer.Kind == LayerKind.Text)
        {
            string[] lines = (layer.Text ?? "").Split('\n');
            int longest = 1;
            foreach (string line in lines) longest = Math.Max(longest, line.Length);
            height = layer.Size * frame.Height * 1.25 * lines.Length;
            width = Math.Min(frame.Width, longest * layer.Size * frame.Height * 0.58 + 16);
        }
        else
        {
            Size size = ImageSize(layer.ImagePath);
            width = layer.Size * frame.Width;
            height = size.Width > 0 ? width * size.Height / size.Width : width;
        }
        LayerBox.Width = Math.Max(16, width);
        LayerBox.Height = Math.Max(16, height);
        Canvas.SetLeft(LayerBox, frame.X + layer.X * frame.Width - LayerBox.Width / 2);
        Canvas.SetTop(LayerBox, frame.Y + layer.Y * frame.Height - LayerBox.Height / 2);
        LayerBox.Visibility = Visibility.Visible;
    }

    private Size ImageSize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Size.Empty;
        if (_imageSizes.TryGetValue(path, out Size size)) return size;
        try
        {
            BitmapFrame frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            size = new Size(frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception)
        {
            size = Size.Empty;
        }
        _imageSizes[path] = size;
        return size;
    }

    private void OnBoxDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel?.SelectedLayer is not { } layer) return;
        StopPlayback();
        _boxGrab = e.GetPosition(Stage);
        _boxOrigin = (layer.X, layer.Y);
        _boxDragging = true;
        LayerBox.CaptureMouse();
        e.Handled = true;
    }

    private void OnBoxMove(object sender, MouseEventArgs e)
    {
        if (!_boxDragging || _viewModel?.SelectedLayer is not { } layer) return;
        Rect frame = FrameRect();
        Point point = e.GetPosition(Stage);
        layer.X = Math.Clamp(_boxOrigin.X + (point.X - _boxGrab.X) / frame.Width, 0, 1);
        layer.Y = Math.Clamp(_boxOrigin.Y + (point.Y - _boxGrab.Y) / frame.Height, 0, 1);
        UpdateLayerBox();
        RequestFrame();
    }

    private void OnBoxUp(object sender, MouseButtonEventArgs e)
    {
        if (!_boxDragging) return;
        _boxDragging = false;
        LayerBox.ReleaseMouseCapture();
        _viewModel?.Commit("position");
        e.Handled = true;
    }

    // Inspector, projects and keys

    private void OnInspectorEdited(object sender, RoutedEventArgs e)
    {
        // Bindings write to the project first; commit once they have.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            string key = e.OriginalSource is FrameworkElement element ? "inspect:" + element.GetHashCode() : "inspect";
            _viewModel?.Commit(key);
        });
    }

    private void OnProjects(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "New project", Command = _viewModel.NewProjectCommand });
        if (_viewModel.Projects.Count > 0) menu.Items.Add(new Separator());
        foreach (ProjectEntry entry in _viewModel.Projects)
        {
            menu.Items.Add(new MenuItem
            {
                Header = $"{entry.Name}   ·   {entry.Modified:MMM d, h:mm tt}",
                Command = _viewModel.OpenProjectCommand,
                CommandParameter = entry,
                FontWeight = entry.Id == _viewModel.Project.Id ? FontWeights.Bold : FontWeights.Normal,
            });
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Delete this project", Command = _viewModel.DeleteProjectCommand });
        menu.IsOpen = true;
    }

    private void OnNameKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        NameBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Focus();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_viewModel == null || e.OriginalSource is TextBox) return;
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.S when !control: _viewModel.SplitCommand.Execute(null); break;
            case Key.Delete: _viewModel.DeleteCommand.Execute(null); break;
            case Key.D when control: _viewModel.DuplicateCommand.Execute(null); break;
            case Key.Z when control && shift: _viewModel.RedoCommand.Execute(null); break;
            case Key.Z when control: _viewModel.UndoCommand.Execute(null); break;
            case Key.Y when control: _viewModel.RedoCommand.Execute(null); break;
            case Key.Left: StopPlayback(); _viewModel.Playhead -= shift ? 1 : 1.0 / _viewModel.Fps; break;
            case Key.Right: StopPlayback(); _viewModel.Playhead += shift ? 1 : 1.0 / _viewModel.Fps; break;
            case Key.Home: StopPlayback(); _viewModel.Playhead = 0; break;
            default: return;
        }
        e.Handled = true;
    }
}

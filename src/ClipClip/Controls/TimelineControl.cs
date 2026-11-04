using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipClip.Engine.Editing;
using ClipClip.ViewModels;

namespace ClipClip.Controls;

/// <summary>
/// The multi-track timeline, drawn in one pass: text and sticker lanes on top, the video track with
/// filmstrip thumbnails in the middle, and audio lanes with waveforms below. Pieces can be selected,
/// moved and trimmed by their edges; the ruler scrubs the playhead.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double LeftPad = 14, RulerHeight = 26, LaneHeight = 28, VideoHeight = 64, AudioHeight = 42, Gap = 8, Edge = 7;

    private static readonly Brush Background = Frozen(Color.FromRgb(0x10, 0x12, 0x18));
    private static readonly Brush RulerText = Frozen(Color.FromRgb(0x7D, 0x83, 0x94));
    private static readonly Brush VideoFill = Frozen(Color.FromRgb(0x23, 0x27, 0x33));
    private static readonly Brush TextFill = Frozen(Color.FromRgb(0x7C, 0x3A, 0xED));
    private static readonly Brush StickerFill = Frozen(Color.FromRgb(0xC0, 0x26, 0xD3));
    private static readonly Brush MusicFill = Frozen(Color.FromRgb(0x16, 0x65, 0x4A));
    private static readonly Brush VoiceFill = Frozen(Color.FromRgb(0x1E, 0x4F, 0x8F));
    private static readonly Brush Wave = Frozen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
    private static readonly Brush PlayheadBrush = Frozen(Color.FromRgb(0xF4, 0xF4, 0xF5));
    private static readonly Pen SelectedPen = FrozenPen(Color.FromRgb(0xFF, 0xFF, 0xFF), 2);
    private static readonly Pen TickPen = FrozenPen(Color.FromRgb(0x2A, 0x2F, 0x3C), 1);
    private static readonly Pen DropPen = FrozenPen(Color.FromRgb(0xFF, 0xE1, 0x4D), 3);
    private static readonly Typeface Label = new("Segoe UI Semibold");

    private readonly Dictionary<string, (double Interval, List<BitmapSource> Frames)> _filmstrips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float[]> _waveforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);

    private StudioViewModel? _model;
    private Drag? _drag;

    private enum Part { Body, Left, Right }
    private enum Mode { None, Scrub, Move, TrimLeft, TrimRight }

    private sealed class Drag
    {
        public Mode Mode;
        public object? Item;
        public double StartX;
        public double A, B, C;
        public bool Moved;
        public int DropIndex = -1;
    }

    public StudioViewModel? Model
    {
        get => _model;
        set
        {
            if (_model != null) _model.ProjectChanged -= Refresh;
            _model = value;
            if (_model != null) _model.ProjectChanged += Refresh;
            Refresh();
        }
    }

    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private EditorProject? Project => _model?.Project;
    private double Pps => _model?.PixelsPerSecond ?? 40;
    private double X(double seconds) => LeftPad + seconds * Pps;
    private double Seconds(double x) => (x - LeftPad) / Pps;

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double width)
    {
        var pen = new Pen(Frozen(color), width);
        pen.Freeze();
        return pen;
    }

    // Layout

    private static List<(T Item, int Lane)> Lanes<T>(IEnumerable<T> items, Func<T, double> start, Func<T, double> end)
    {
        var ends = new List<double>();
        var result = new List<(T, int)>();
        foreach (T item in items.OrderBy(start))
        {
            int lane = ends.FindIndex(e => e <= start(item) + 0.0001);
            if (lane < 0)
            {
                lane = ends.Count;
                ends.Add(0);
            }
            ends[lane] = end(item);
            result.Add((item, lane));
        }
        return result;
    }

    private (List<(OverlayLayer Item, int Lane)> Layers, List<(AudioClip Item, int Lane)> Audio, double VideoTop, double AudioTop, double Height) Layout()
    {
        EditorProject? project = Project;
        var layers = project == null ? [] : Lanes(project.Layers, l => l.Start, l => l.End);
        var audio = project == null ? [] : Lanes(project.Audio, a => a.Start, a => a.End);
        int layerLanes = Math.Max(1, layers.Count == 0 ? 1 : layers.Max(l => l.Lane) + 1);
        int audioLanes = Math.Max(1, audio.Count == 0 ? 1 : audio.Max(a => a.Lane) + 1);
        double videoTop = RulerHeight + Gap + layerLanes * LaneHeight + Gap;
        double audioTop = videoTop + VideoHeight + Gap;
        double height = audioTop + audioLanes * AudioHeight + Gap;
        return (layers, audio, videoTop, audioTop, height);
    }

    protected override Size MeasureOverride(Size available)
    {
        double duration = Project?.Duration ?? 0;
        double extent = Math.Max(Project?.Audio.Select(a => a.End).DefaultIfEmpty(0).Max() ?? 0, Project?.Layers.Select(l => l.End).DefaultIfEmpty(0).Max() ?? 0);
        double width = LeftPad + Math.Max(duration, extent) * Pps + 320;
        return new Size(width, Layout().Height);
    }

    // Drawing

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        EditorProject? project = Project;
        if (project == null) return;
        var layout = Layout();
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        DrawRuler(dc, dpi);

        // Lane backgrounds give the tracks shape even when empty.
        var laneBrush = Frozen(Color.FromRgb(0x15, 0x18, 0x20));
        dc.DrawRectangle(laneBrush, null, new Rect(0, RulerHeight + Gap, ActualWidth, layout.VideoTop - RulerHeight - Gap * 2));
        dc.DrawRectangle(laneBrush, null, new Rect(0, layout.VideoTop, ActualWidth, VideoHeight));
        dc.DrawRectangle(laneBrush, null, new Rect(0, layout.AudioTop, ActualWidth, layout.Height - layout.AudioTop - Gap));

        foreach ((VideoClip clip, double start) in project.Sequence())
        {
            Rect rect = new(X(start), layout.VideoTop, clip.Duration * Pps, VideoHeight);
            DrawVideo(dc, clip, rect, dpi);
        }
        foreach ((OverlayLayer layer, int lane) in layout.Layers)
        {
            Rect rect = new(X(layer.Start), RulerHeight + Gap + lane * LaneHeight + 2, Math.Max(4, layer.Duration * Pps), LaneHeight - 4);
            DrawBlock(dc, rect, layer.Kind == LayerKind.Text ? TextFill : StickerFill, (layer.Kind == LayerKind.Text ? "T  " : "★  ") + layer.Text, dpi, layer == _model?.Selected);
        }
        foreach ((AudioClip clip, int lane) in layout.Audio)
        {
            Rect rect = new(X(clip.Start), layout.AudioTop + lane * AudioHeight + 2, Math.Max(4, clip.Duration * Pps), AudioHeight - 4);
            DrawAudio(dc, clip, rect, dpi);
        }

        if (_drag is { Mode: Mode.Move, Item: VideoClip, DropIndex: >= 0 } drop)
        {
            double x = LeftPad;
            for (int i = 0; i < drop.DropIndex && i < project.Video.Count; i++) x += project.Video[i].Duration * Pps;
            dc.DrawLine(DropPen, new Point(x, layout.VideoTop - 4), new Point(x, layout.VideoTop + VideoHeight + 4));
        }

        double head = X(_model!.Playhead);
        dc.DrawRectangle(PlayheadBrush, null, new Rect(head - 1, 0, 2, ActualHeight));
        var cap = new StreamGeometry();
        using (StreamGeometryContext g = cap.Open())
        {
            g.BeginFigure(new Point(head - 7, 0), true, true);
            g.LineTo(new Point(head + 7, 0), true, false);
            g.LineTo(new Point(head + 7, 10), true, false);
            g.LineTo(new Point(head, 17), true, false);
            g.LineTo(new Point(head - 7, 10), true, false);
        }
        cap.Freeze();
        dc.DrawGeometry(PlayheadBrush, null, cap);
    }

    private void DrawRuler(DrawingContext dc, double dpi)
    {
        double pps = Pps;
        double[] steps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300];
        double step = steps.FirstOrDefault(s => s * pps >= 70, 600);
        double end = Seconds(ActualWidth);
        for (double t = 0; t <= end; t += step)
        {
            double x = X(t);
            dc.DrawLine(TickPen, new Point(x, RulerHeight - 8), new Point(x, RulerHeight));
            var text = new FormattedText(TimeSpan.FromSeconds(t).ToString(t >= 3600 ? @"h\:mm\:ss" : @"m\:ss"), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Label, 10, RulerText, dpi);
            dc.DrawText(text, new Point(x + 3, 4));
            for (int i = 1; i < 4; i++)
            {
                double minor = X(t + step * i / 4);
                dc.DrawLine(TickPen, new Point(minor, RulerHeight - 4), new Point(minor, RulerHeight));
            }
        }
    }

    private void DrawBlock(DrawingContext dc, Rect rect, Brush fill, string label, double dpi, bool selected)
    {
        dc.DrawRoundedRectangle(fill, selected ? SelectedPen : null, rect, 6, 6);
        if (rect.Width > 26)
        {
            dc.PushClip(new RectangleGeometry(new Rect(rect.X + 6, rect.Y, Math.Max(0, rect.Width - 12), rect.Height)));
            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Label, 11, Brushes.White, dpi);
            dc.DrawText(text, new Point(rect.X + 8, rect.Y + (rect.Height - text.Height) / 2));
            dc.Pop();
        }
        if (selected) DrawHandles(dc, rect);
    }

    private static void DrawHandles(DrawingContext dc, Rect rect)
    {
        dc.DrawRoundedRectangle(Brushes.White, null, new Rect(rect.X + 1, rect.Y + 4, 4, rect.Height - 8), 2, 2);
        dc.DrawRoundedRectangle(Brushes.White, null, new Rect(rect.Right - 5, rect.Y + 4, 4, rect.Height - 8), 2, 2);
    }

    private void DrawVideo(DrawingContext dc, VideoClip clip, Rect rect, double dpi)
    {
        bool selected = clip == _model?.Selected;
        var shape = new RectangleGeometry(rect, 6, 6);
        dc.PushClip(shape);
        dc.DrawRectangle(VideoFill, null, rect);
        if (_filmstrips.TryGetValue(clip.SourcePath, out var strip) && strip.Frames.Count > 0)
        {
            BitmapSource first = strip.Frames[0];
            double tileWidth = Math.Max(20, VideoHeight * first.PixelWidth / (double)first.PixelHeight);
            for (double x = rect.Left; x < rect.Right; x += tileWidth)
            {
                double sourceTime = clip.In + (x - rect.Left) / Pps * clip.Speed;
                int index = Math.Clamp((int)(sourceTime / strip.Interval), 0, strip.Frames.Count - 1);
                dc.DrawImage(strip.Frames[index], new Rect(x, rect.Top, tileWidth, VideoHeight));
            }
        }
        else
        {
            LoadFilmstrip(clip);
        }

        string label = clip.Title + (Math.Abs(clip.Speed - 1) > 0.001 ? $"   {clip.Speed}x" : "") + (clip.Transition != TransitionKind.None ? "   ◣" : "");
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Label, 11, Brushes.White, dpi);
        var labelBack = Frozen(Color.FromArgb(0xB0, 0, 0, 0));
        dc.DrawRectangle(labelBack, null, new Rect(rect.X, rect.Bottom - 18, Math.Min(rect.Width, text.Width + 14), 18));
        dc.DrawText(text, new Point(rect.X + 6, rect.Bottom - 17));
        dc.Pop();
        if (selected)
        {
            dc.DrawRoundedRectangle(null, SelectedPen, rect, 6, 6);
            DrawHandles(dc, rect);
        }
        else
        {
            dc.DrawRoundedRectangle(null, TickPen, rect, 6, 6);
        }
    }

    private void DrawAudio(DrawingContext dc, AudioClip clip, Rect rect, double dpi)
    {
        bool selected = clip == _model?.Selected;
        dc.DrawRoundedRectangle(clip.IsVoiceover ? VoiceFill : MusicFill, selected ? SelectedPen : null, rect, 6, 6);
        if (_waveforms.TryGetValue(clip.SourcePath, out float[]? peaks) && peaks.Length > 0)
        {
            var geometry = new StreamGeometry();
            double middle = rect.Top + rect.Height / 2 + 4;
            double reach = rect.Height / 2 - 8;
            using (StreamGeometryContext g = geometry.Open())
            {
                for (double x = rect.Left + 2; x < rect.Right - 2; x += 2)
                {
                    double time = clip.In + (x - rect.Left) / Pps;
                    int index = Math.Clamp((int)(time * 50), 0, peaks.Length - 1);
                    double h = Math.Max(1, peaks[index] * reach * (float)Math.Min(1.5, clip.Volume));
                    g.BeginFigure(new Point(x, middle - h), false, false);
                    g.LineTo(new Point(x, middle + h), true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(Wave, 1.2), geometry);
        }
        else
        {
            LoadWaveform(clip.SourcePath);
        }
        if (rect.Width > 30)
        {
            dc.PushClip(new RectangleGeometry(rect));
            var text = new FormattedText((clip.IsVoiceover ? "●  " : "♪  ") + clip.Title, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Label, 10, Brushes.White, dpi);
            dc.DrawText(text, new Point(rect.X + 7, rect.Y + 2));
            dc.Pop();
        }
        if (selected) DrawHandles(dc, rect);
    }

    private void LoadFilmstrip(VideoClip clip)
    {
        string path = clip.SourcePath;
        if (!_loading.Add(path)) return;
        double duration = clip.SourceDuration > 0 ? clip.SourceDuration : clip.Out;
        double interval = Math.Max(0.5, duration / 120);
        Task.Run(() =>
        {
            try
            {
                var times = new List<double>();
                for (double t = 0; t < duration; t += interval) times.Add(t);
                List<byte[]> frames = MediaInfo.Filmstrip(path, times, 64, out int width);
                var images = frames.Select(pixels =>
                {
                    BitmapSource image = BitmapSource.Create(width, 64, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
                    image.Freeze();
                    return image;
                }).ToList();
                Dispatcher.BeginInvoke(() =>
                {
                    _filmstrips[path] = (interval, images);
                    InvalidateVisual();
                });
            }
            catch (Exception)
            {
                // Missing file: the block stays plain.
            }
        });
    }

    private void LoadWaveform(string path)
    {
        if (!_loading.Add(path)) return;
        Task.Run(() =>
        {
            float[] peaks = MediaInfo.Peaks(path, 50);
            Dispatcher.BeginInvoke(() =>
            {
                _waveforms[path] = peaks;
                InvalidateVisual();
            });
        });
    }

    /// <summary>Forget a cached waveform, for a voiceover recorded into an existing file name.</summary>
    public void ForgetMedia(string path)
    {
        _waveforms.Remove(path);
        _filmstrips.Remove(path);
        _loading.Remove(path);
    }

    // Interaction

    private (object? Item, Part Part) HitTest(Point point)
    {
        EditorProject? project = Project;
        if (project == null || point.Y < RulerHeight) return (null, Part.Body);
        var layout = Layout();

        Part PartOf(Rect rect) => point.X - rect.Left <= Edge ? Part.Left : rect.Right - point.X <= Edge ? Part.Right : Part.Body;

        foreach ((OverlayLayer layer, int lane) in layout.Layers)
        {
            Rect rect = new(X(layer.Start), RulerHeight + Gap + lane * LaneHeight, Math.Max(4, layer.Duration * Pps), LaneHeight);
            if (rect.Contains(point)) return (layer, PartOf(rect));
        }
        foreach ((VideoClip clip, double start) in project.Sequence())
        {
            Rect rect = new(X(start), layout.VideoTop, clip.Duration * Pps, VideoHeight);
            if (rect.Contains(point)) return (clip, PartOf(rect));
        }
        foreach ((AudioClip clip, int lane) in layout.Audio)
        {
            Rect rect = new(X(clip.Start), layout.AudioTop + lane * AudioHeight, Math.Max(4, clip.Duration * Pps), AudioHeight);
            if (rect.Contains(point)) return (clip, PartOf(rect));
        }
        return (null, Part.Body);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point point = e.GetPosition(this);
        if (_drag == null)
        {
            (object? item, Part part) = HitTest(point);
            Cursor = item != null && part != Part.Body ? Cursors.SizeWE : item != null ? Cursors.Hand : point.Y < RulerHeight ? Cursors.IBeam : Cursors.Arrow;
            return;
        }
        if (_model == null) return;

        double delta = (point.X - _drag.StartX) / Pps;
        if (Math.Abs(point.X - _drag.StartX) > 2) _drag.Moved = true;
        if (!_drag.Moved) return;

        switch (_drag.Mode)
        {
            case Mode.Scrub:
                _model.Playhead = Seconds(point.X);
                break;
            case Mode.Move:
                ApplyMove(_drag, delta, point);
                break;
            case Mode.TrimLeft:
            case Mode.TrimRight:
                ApplyTrim(_drag, delta);
                break;
        }
        _model.Changed();
    }

    private double Snap(double seconds, object? except)
    {
        EditorProject project = Project!;
        var targets = new List<double> { 0, _model!.Playhead };
        foreach ((VideoClip clip, double start) in project.Sequence())
        {
            targets.Add(start);
            targets.Add(start + clip.Duration);
        }
        targets.AddRange(project.Layers.Where(l => l != except).SelectMany(l => new[] { l.Start, l.End }));
        targets.AddRange(project.Audio.Where(a => a != except).SelectMany(a => new[] { a.Start, a.End }));
        double threshold = 8 / Pps;
        double best = targets.OrderBy(t => Math.Abs(t - seconds)).First();
        return Math.Abs(best - seconds) <= threshold ? best : seconds;
    }

    private void ApplyMove(Drag drag, double delta, Point point)
    {
        switch (drag.Item)
        {
            case OverlayLayer layer:
            {
                double length = drag.B - drag.A;
                double start = Snap(Math.Max(0, drag.A + delta), layer);
                double snappedEnd = Snap(start + length, layer);
                if (Math.Abs(snappedEnd - (start + length)) > 0.0001 && Math.Abs(start - (drag.A + delta)) < 0.0001) start = snappedEnd - length;
                layer.Start = Math.Max(0, start);
                layer.End = layer.Start + length;
                break;
            }
            case AudioClip clip:
                clip.Start = Math.Max(0, Snap(drag.A + delta, clip));
                break;
            case VideoClip clip:
            {
                EditorProject project = Project!;
                double time = Seconds(point.X);
                int index = 0;
                foreach ((VideoClip other, double start) in project.Sequence())
                {
                    if (time > start + other.Duration / 2) index++;
                }
                drag.DropIndex = index;
                break;
            }
        }
    }

    private void ApplyTrim(Drag drag, double delta)
    {
        bool left = drag.Mode == Mode.TrimLeft;
        switch (drag.Item)
        {
            case VideoClip clip:
                if (left) clip.In = Math.Clamp(drag.A + delta * clip.Speed, 0, clip.Out - 0.1);
                else clip.Out = Math.Clamp(drag.B + delta * clip.Speed, clip.In + 0.1, clip.SourceDuration > 0 ? clip.SourceDuration : double.MaxValue);
                break;
            case OverlayLayer layer:
                if (left) layer.Start = Math.Clamp(Snap(drag.A + delta, layer), 0, layer.End - 0.2);
                else layer.End = Math.Max(layer.Start + 0.2, Snap(drag.B + delta, layer));
                break;
            case AudioClip clip:
                if (left)
                {
                    double shift = Math.Clamp(delta, -drag.C, clip.Out - 0.1 - drag.C);
                    shift = Math.Max(shift, -drag.A);
                    clip.In = drag.C + shift;
                    clip.Start = drag.A + shift;
                }
                else
                {
                    clip.Out = Math.Clamp(drag.B + delta, clip.In + 0.1, clip.SourceDuration > 0 ? clip.SourceDuration : double.MaxValue);
                }
                break;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_model == null) return;
        Focus();
        Point point = e.GetPosition(this);
        (object? item, Part part) = HitTest(point);
        if (item == null)
        {
            _drag = new Drag { Mode = Mode.Scrub, StartX = point.X, Moved = true };
            _model.Playhead = Seconds(point.X);
            if (point.Y >= RulerHeight) _model.Selected = null;
        }
        else
        {
            _model.Selected = item;
            var drag = new Drag { Item = item, StartX = point.X, Mode = part switch { Part.Left => Mode.TrimLeft, Part.Right => Mode.TrimRight, _ => Mode.Move } };
            switch (item)
            {
                case VideoClip clip: drag.A = clip.In; drag.B = clip.Out; break;
                case OverlayLayer layer: drag.A = layer.Start; drag.B = layer.End; break;
                case AudioClip clip: drag.A = clip.Start; drag.B = clip.Out; drag.C = clip.In; break;
            }
            _drag = drag;
        }
        CaptureMouse();
        _model.Changed();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Drag? drag = _drag;
        _drag = null;
        ReleaseMouseCapture();
        if (drag == null || _model == null) return;
        if (drag.Mode == Mode.Move && drag.Item is VideoClip clip && drag.DropIndex >= 0)
        {
            _model.MoveVideo(clip, drag.DropIndex);
            return;
        }
        if (drag.Moved && drag.Mode != Mode.Scrub) _model.Commit(drag.Mode == Mode.Move ? "drag" : "trim");
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_model == null || Keyboard.Modifiers != ModifierKeys.Control) return;
        _model.PixelsPerSecond *= e.Delta > 0 ? 1.2 : 1 / 1.2;
        e.Handled = true;
    }
}

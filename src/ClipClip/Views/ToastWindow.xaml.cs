using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClipClip.Engine.Capture;

namespace ClipClip.Views;

/// <summary>Click-through, never takes focus from a game, and hidden from capture so a toast from
/// one clip can never appear in the next.</summary>
public partial class ToastWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    private const uint WdaExcludeFromCapture = 0x11;

    private readonly DispatcherTimer _life;

    public ToastWindow(string title, string detail, bool success)
    {
        InitializeComponent();
        TitleText.Text = title;
        DetailText.Text = detail;
        if (!success)
        {
            Badge.Background = (Brush)FindResource("Warn");
            Glyph.Data = Geometry.Parse("M12,6.5 V13.5 M12,17 V17.2");
            Glyph.Stroke = Brushes.Black;
        }

        _life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(success ? 3 : 6) };
        _life.Tick += (_, _) => Dismiss();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        SetWindowLongW(handle, GwlExStyle, GetWindowLongW(handle, GwlExStyle) | WsExTransparent | WsExToolWindow | WsExNoActivate);
        SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DisplayInfo display = Displays.Primary();
        var transform = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformFromDevice;
        Point corner = transform.Transform(new Point(display.X + display.Width, display.Y + display.Height));
        Left = corner.X - ActualWidth - 8;
        Top = corner.Y - ActualHeight - 56;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        _life.Start();
    }

    private void Dismiss()
    {
        _life.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => Close();
        Root.BeginAnimation(OpacityProperty, fade);
    }

    [DllImport("user32")] private static extern int GetWindowLongW(nint window, int index);
    [DllImport("user32")] private static extern int SetWindowLongW(nint window, int index, int value);
    [DllImport("user32")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
}

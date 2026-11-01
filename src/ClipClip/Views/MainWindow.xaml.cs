using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

using ClipClip.Engine;
using ClipClip.Engine.Core;
using ClipClip.ViewModels;

namespace ClipClip.Views;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int CornerRound = 2;

    private readonly MainViewModel _viewModel;

    public MainWindow(ClipController controller)
    {
        InitializeComponent();
        _viewModel = new MainViewModel(controller, Dispatcher);
        _viewModel.PageChanged += FadeInPage;
        DataContext = _viewModel;

        SourceInitialized += OnSourceInitialized;
        StateChanged += OnStateChanged;
        Loaded += (_, _) => _viewModel.SetVisible(true);
        Closed += (_, _) =>
        {
            Log.Info("Window closed");
            _viewModel.Dispose();
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        int dark = 1;
        int corner = CornerRound;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // A maximized chrome window overhangs the screen edge by the resize border.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
        _viewModel.SetVisible(WindowState != WindowState.Minimized);
    }

    private void FadeInPage()
    {
        var fade = new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Pages.BeginAnimation(OpacityProperty, fade);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Log.Info("Window close button pressed");
        App.Current.OnWindowCloseRequested();
    }

    [DllImport("dwmapi")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}

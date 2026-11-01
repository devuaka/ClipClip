using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClipClip.Engine;
using ClipClip.Engine.Core;
using ClipClip.Services;
using ClipClip.Views;

namespace ClipClip;

/// <summary>
/// The app lives in the tray. The main window exists only while it is open: closing it frees the
/// UI and its thumbnails, so nothing renders and little memory is held while a game runs.
/// </summary>
public partial class App : Application
{
    private readonly EventWaitHandle _showSignal;
    private readonly bool _startMinimized;
    private TrayIcon? _tray;
    private Toasts? _toasts;
    private MainWindow? _window;
    private RegisteredWaitHandle? _showWait;
    private bool _exiting;

    public App(EventWaitHandle showSignal, bool startMinimized)
    {
        _showSignal = showSignal;
        _startMinimized = startMinimized;
    }

    public static new App Current => (App)Application.Current;

    public ClipController Controller { get; private set; } = null!;

    public Window? MainWindowOrNull => _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledUiException;

        AppSettings settings = SettingsStore.Load();
        try
        {
            Controller = new ClipController(settings);
        }
        catch (Exception ex)
        {
            Log.Error("ClipClip could not start", ex);
            MessageBox.Show("ClipClip could not start.\n\n" + ex.Message, "ClipClip", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _toasts = new Toasts(settings);
        _tray = new TrayIcon(Controller, ShowMainWindow, ExitApp);

        Controller.ClipSaved += result => Dispatcher.BeginInvoke(() => _toasts.Show(result));
        Controller.OpenRequested += () => Dispatcher.BeginInvoke(ShowMainWindow);
        Controller.StateChanged += () => Dispatcher.BeginInvoke(() => _tray?.Refresh());

        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, Timeout.Infinite, false);

        Controller.Start();
        if (!_startMinimized) ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        if (_exiting) return;
        if (_window == null)
        {
            _window = new MainWindow(Controller);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>The window's close button. Either drops to the tray or quits, per the setting.</summary>
    public void OnWindowCloseRequested()
    {
        AppSettings settings = Controller.Settings;
        if (!settings.MinimizeToTray)
        {
            ExitApp();
            return;
        }

        _window?.Close();
        if (!settings.TrayHintShown)
        {
            settings.TrayHintShown = true;
            SettingsStore.Save(settings);
            _tray?.Hint("ClipClip is still running", "It keeps recording from the system tray. Right-click the icon to exit.");
        }
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _showWait?.Unregister(null);
        _window?.Close();
        _tray?.Dispose();
        _toasts?.Dispose();

        // Finishing queued saves can take a moment. The UI is already gone by then.
        Task.Run(() =>
        {
            Controller.Dispose();
            Dispatcher.BeginInvoke(() => Shutdown());
        });
    }

    private void OnUnhandledUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A UI fault must not take the recording down with it.
        Log.Error("Unhandled UI error", e.Exception);
        e.Handled = true;
    }
}

using System;
using System.Windows.Threading;
using ClipClip.Engine;

namespace ClipClip.ViewModels;

public enum Page { Home, Clips, Games, Settings }

/// <summary>Exists only while the window is open, and polls the engine only while it is visible.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ClipController _controller;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private readonly Action _stateChanged;
    private readonly Action<ClipResult> _clipSaved;
    private Page _page;
    private string? _notice;
    private readonly DispatcherTimer _noticeTimer;

    public MainViewModel(ClipController controller, Dispatcher dispatcher)
    {
        _controller = controller;
        _dispatcher = dispatcher;
        Home = new HomeViewModel(controller, this);
        Clips = new ClipsViewModel(controller, this);
        Games = new GamesViewModel(controller, this);
        Settings = new SettingsViewModel(controller, this);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            Notice = null;
        };

        _stateChanged = () => dispatcher.BeginInvoke(Tick);
        _clipSaved = _ => dispatcher.BeginInvoke(() => ClipSaved?.Invoke());
        controller.StateChanged += _stateChanged;
        controller.ClipSaved += _clipSaved;
        Tick();
    }

    public HomeViewModel Home { get; }
    public ClipsViewModel Clips { get; }
    public GamesViewModel Games { get; }
    public SettingsViewModel Settings { get; }

    public event Action? ClipSaved;
    public event Action? PageChanged;

    public Page Page
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            Notify(nameof(IsHome));
            Notify(nameof(IsClips));
            Notify(nameof(IsGames));
            Notify(nameof(IsSettings));
            if (value != Page.Clips) Clips.ClosePlayer();
            if (value != Page.Settings) Settings.StopMeter();
            PageChanged?.Invoke();
        }
    }

    public bool IsHome { get => _page == Page.Home; set { if (value) Page = Page.Home; } }
    public bool IsClips { get => _page == Page.Clips; set { if (value) Page = Page.Clips; } }
    public bool IsGames { get => _page == Page.Games; set { if (value) Page = Page.Games; } }
    public bool IsSettings { get => _page == Page.Settings; set { if (value) Page = Page.Settings; } }

    public string? Notice
    {
        get => _notice;
        private set => Set(ref _notice, value);
    }

    public void ShowNotice(string text)
    {
        Notice = text;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    /// <summary>Stops the poll while the window is minimized.</summary>
    public void SetVisible(bool visible)
    {
        if (visible) _timer.Start();
        else _timer.Stop();
    }

    private void Tick()
    {
        Home.Update();
        Games.Update();
        Settings.Update();
    }

    public void Dispose()
    {
        _timer.Stop();
        _noticeTimer.Stop();
        _controller.StateChanged -= _stateChanged;
        _controller.ClipSaved -= _clipSaved;
        Home.Dispose();
        Clips.Dispose();
        Games.Dispose();
        Settings.Dispose();
    }
}

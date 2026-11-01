using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ClipClip.Engine.Core;
using Microsoft.Win32;

namespace ClipClip.Engine.Detection;

public enum GameSource { Custom, Library, KnownGame, Engine, Fullscreen }

public sealed record DetectedGame(string Name, string ExePath, int ProcessId, nint Window, GameSource Source, bool IsForeground, bool IsFullscreen);

public sealed class CustomGame
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed record DetectionRules(bool AutoDetect, IReadOnlyList<CustomGame> CustomGames, IReadOnlySet<string> IgnoredExes);

/// <summary>
/// Polls about once a second. The foreground window is judged by where its exe lives and what is
/// on disk beside it, while custom games are matched against the process list, so they are
/// recognised on launch even before they take focus. A game stays active until its process exits.
/// </summary>
public sealed class GameDetector : IDisposable
{
    private static readonly Dictionary<string, string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RobloxPlayerBeta"] = "Roblox",
        ["League of Legends"] = "League of Legends",
        ["VALORANT-Win64-Shipping"] = "VALORANT",
        ["FortniteClient-Win64-Shipping"] = "Fortnite",
        ["cs2"] = "Counter-Strike 2",
        ["GTA5"] = "Grand Theft Auto V",
        ["GTA5_Enhanced"] = "Grand Theft Auto V",
        ["Overwatch"] = "Overwatch",
        ["Wow"] = "World of Warcraft",
        ["FC26"] = "EA Sports FC 26",
        ["RocketLeague"] = "Rocket League",
    };

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, GameEvidence> _evidenceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _ownProcessId = Environment.ProcessId;

    private DetectionRules _rules;
    private IReadOnlyList<InstalledGame> _installed = [];
    private Dictionary<string, string> _windowsGames = new(StringComparer.OrdinalIgnoreCase);
    private DetectedGame? _current;

    public GameDetector(DetectionRules rules)
    {
        _rules = rules;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ClipClip Detector", Priority = ThreadPriority.BelowNormal };
    }

    /// <summary>Fires on change of game, window, focus or fullscreen state.</summary>
    public event Action<DetectedGame?>? Changed;
    public event Action<IReadOnlyList<InstalledGame>>? LibraryScanned;

    public DetectedGame? Current { get { lock (_gate) return _current; } }
    public IReadOnlyList<InstalledGame> Installed { get { lock (_gate) return _installed; } }

    public void Start() => _thread.Start();

    public void UpdateRules(DetectionRules rules)
    {
        lock (_gate) _rules = rules;
    }

    private void Loop()
    {
        RefreshLibrary();
        int polls = 0;
        do
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                Log.Warn("Game detection poll failed: " + ex.Message);
            }
            if (++polls % 300 == 0) RefreshLibrary();
        }
        while (!_stop.Wait(1000));
    }

    private void RefreshLibrary()
    {
        IReadOnlyList<InstalledGame> installed = GameLibrary.Scan();
        Dictionary<string, string> windowsGames = ReadWindowsGames();
        lock (_gate)
        {
            _installed = installed;
            _windowsGames = windowsGames;
        }
        Log.Info($"Game library: {installed.Count} installed, {windowsGames.Count} known to Windows");
        LibraryScanned?.Invoke(installed);
    }

    private static Dictionary<string, string> ReadWindowsGames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using RegistryKey? children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            if (children == null) return result;
            foreach (string id in children.GetSubKeyNames())
            {
                using RegistryKey? child = children.OpenSubKey(id);
                if (child?.GetValue("MatchedExeFullPath") is string path && path.Length > 0)
                    result[path] = FriendlyName(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the Windows game list: " + ex.Message);
        }
        return result;
    }

    private void Poll()
    {
        DetectionRules rules;
        DetectedGame? previous;
        lock (_gate)
        {
            rules = _rules;
            previous = _current;
        }

        nint foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out uint foregroundPid);

        DetectedGame? next = CheckCurrent(previous, foreground, (int)foregroundPid, rules);
        next ??= FindCustomGame(rules, foreground);
        if (next == null && rules.AutoDetect && foreground != 0 && foregroundPid != 0)
            next = ClassifyForeground(foreground, (int)foregroundPid, rules);

        lock (_gate)
        {
            if (next == previous) return;
            _current = next;
        }
        if (next?.Name != previous?.Name)
            Log.Info(next == null ? $"Game closed: {previous?.Name}" : $"Game detected: {next.Name} ({next.Source}) {next.ExePath}");
        Changed?.Invoke(next);
    }

    private DetectedGame? CheckCurrent(DetectedGame? current, nint foreground, int foregroundPid, DetectionRules rules)
    {
        if (current == null || !ProcessAlive(current.ProcessId, current.ExePath)) return null;
        if (rules.IgnoredExes.Contains(current.ExePath)) return null;

        if (current.Source == GameSource.Custom)
        {
            bool stillEnabled = rules.CustomGames.Any(g => g.Enabled && PathsEqual(g.ExePath, current.ExePath));
            if (!stillEnabled) return null;
        }

        nint window = foregroundPid == current.ProcessId && IsCapturable(foreground)
            ? foreground
            : FindMainWindow(current.ProcessId);
        if (window == 0) window = current.Window;

        return current with
        {
            Window = window,
            IsForeground = foregroundPid == current.ProcessId,
            IsFullscreen = IsFullscreenWindow(window),
        };
    }

    private DetectedGame? FindCustomGame(DetectionRules rules, nint foreground)
    {
        CustomGame[] enabled = rules.CustomGames.Where(g => g.Enabled).ToArray();
        if (enabled.Length == 0) return null;

        foreach ((int pid, string exeName) in ProcessList.Snapshot())
        {
            foreach (CustomGame game in enabled)
            {
                if (!string.Equals(Path.GetFileName(game.ExePath), exeName, StringComparison.OrdinalIgnoreCase)) continue;
                string? path = ImagePath(pid);
                if (path == null || !PathsEqual(path, game.ExePath)) continue;

                nint window = FindMainWindow(pid);
                if (window == 0) continue;
                GetWindowThreadProcessId(foreground, out uint foregroundPid);
                return new DetectedGame(game.Name, path, pid, window, GameSource.Custom,
                    foregroundPid == pid, IsFullscreenWindow(window));
            }
        }
        return null;
    }

    private DetectedGame? ClassifyForeground(nint window, int pid, DetectionRules rules)
    {
        if (pid == _ownProcessId || !IsCapturable(window)) return null;
        string? path = ImagePath(pid);
        if (path == null) return null;
        if (rules.IgnoredExes.Contains(path)) return null;

        string stem = Path.GetFileNameWithoutExtension(path);

        if (stem.Equals("javaw", StringComparison.OrdinalIgnoreCase) && WindowTitle(window).StartsWith("Minecraft", StringComparison.Ordinal))
            return Found("Minecraft", GameSource.KnownGame);

        if (ExeClassifier.IsNeverGame(path)) return null;

        if (KnownGames.TryGetValue(stem, out string? known)) return Found(known, GameSource.KnownGame);

        bool fullscreen = IsFullscreenWindow(window);
        if (ExeClassifier.IsHelper(path) && !fullscreen) return null;

        IReadOnlyList<InstalledGame> installed;
        Dictionary<string, string> windowsGames;
        lock (_gate)
        {
            installed = _installed;
            windowsGames = _windowsGames;
        }

        InstalledGame? owner = installed
            .Where(g => IsInside(path, g.InstallDir))
            .MaxBy(g => g.InstallDir.Length);
        if (owner != null && rules.IgnoredExes.Contains(owner.InstallDir)) return null;

        GameEvidence evidence = EvidenceFor(path);
        if (owner != null && evidence != GameEvidence.None) return Found(owner.Name, GameSource.Library);

        if (windowsGames.TryGetValue(path, out string? windowsName) && evidence != GameEvidence.None)
            return Found(windowsName, GameSource.Library);

        if (evidence == GameEvidence.Strong || (fullscreen && evidence == GameEvidence.Weak))
            return Found(FriendlyName(path), fullscreen ? GameSource.Fullscreen : GameSource.Engine);
        return null;

        DetectedGame Found(string name, GameSource source) => new(name, path, pid, window, source, true, IsFullscreenWindow(window));
    }

    private GameEvidence EvidenceFor(string path)
    {
        lock (_evidenceCache)
        {
            if (_evidenceCache.TryGetValue(path, out GameEvidence cached)) return cached;
            GameEvidence result = ExeClassifier.Evidence(path);
            _evidenceCache[path] = result;
            return result;
        }
    }

    private static string FriendlyName(string path)
    {
        try
        {
            string? product = FileVersionInfo.GetVersionInfo(path).ProductName?.Trim();
            if (!string.IsNullOrEmpty(product) && !product.Contains("UnrealGame", StringComparison.OrdinalIgnoreCase) &&
                !product.Contains("Unity", StringComparison.OrdinalIgnoreCase))
                return product;
        }
        catch
        {
            // Fall through to the file name.
        }
        return Path.GetFileNameWithoutExtension(path).Replace("-Win64-Shipping", "").Replace('_', ' ');
    }

    private static bool IsInside(string path, string directory)
    {
        string prefix = directory.TrimEnd('\\', '/') + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool ProcessAlive(int pid, string path)
    {
        string? current = ImagePath(pid);
        return current != null && PathsEqual(current, path);
    }

    private static string? ImagePath(int pid)
    {
        // Limited information is the least intrusive access there is, and anti-cheat permits it.
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == 0) return null;
        try
        {
            var builder = new StringBuilder(1024);
            int size = builder.Capacity;
            return QueryFullProcessImageNameW(process, 0, builder, ref size) ? builder.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public static nint FindMainWindow(int pid)
    {
        nint best = 0;
        long bestArea = 0;
        EnumWindowsProc callback = (window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != pid || !IsCapturable(window)) return true;
            GetWindowRect(window, out Rect rect);
            long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea)
            {
                bestArea = area;
                best = window;
            }
            return true;
        };
        EnumWindows(callback, 0);
        GC.KeepAlive(callback);
        return best;
    }

    private static bool IsCapturable(nint window)
    {
        if (window == 0 || !IsWindowVisible(window) || IsIconic(window)) return false;
        if (GetWindow(window, GwOwner) != 0) return false;
        long extended = GetWindowLongPtrW(window, GwlExStyle);
        if ((extended & WsExToolWindow) != 0) return false;
        if (DwmGetWindowAttribute(window, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        GetWindowRect(window, out Rect rect);
        return rect.Right - rect.Left >= 320 && rect.Bottom - rect.Top >= 240;
    }

    private static bool IsFullscreenWindow(nint window)
    {
        if (window == 0) return false;
        long style = GetWindowLongPtrW(window, GwlStyle);
        if ((style & WsCaption) == WsCaption) return false;

        GetWindowRect(window, out Rect rect);
        nint monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return false;
        return rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top &&
               rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
    }

    private static string WindowTitle(nint window)
    {
        var builder = new StringBuilder(256);
        GetWindowTextW(window, builder, builder.Capacity);
        return builder.ToString();
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive) _thread.Join(2000);
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int GwlStyle = -16, GwlExStyle = -20;
    private const long WsCaption = 0x00C00000, WsExToolWindow = 0x80;
    private const uint GwOwner = 4;
    private const int DwmwaCloaked = 14;

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32")] private static extern nint GetForegroundWindow();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32")] private static extern bool IsIconic(nint window);
    [DllImport("user32")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32")] private static extern long GetWindowLongPtrW(nint window, int index);
    [DllImport("user32")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder text, int max);
    [DllImport("dwmapi")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("kernel32", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder name, ref int size);
}

internal static class ProcessList
{
    private const uint SnapProcess = 2;

    /// <summary>Names only, from the kernel snapshot. No process is opened.</summary>
    public static List<(int Pid, string ExeName)> Snapshot()
    {
        var result = new List<(int, string)>(256);
        nint snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == -1) return result;
        try
        {
            var entry = new ProcessEntry { Size = Marshal.SizeOf<ProcessEntry>() };
            bool more = Process32FirstW(snapshot, ref entry);
            while (more)
            {
                result.Add(((int)entry.ProcessId, entry.ExeFile));
                more = Process32NextW(snapshot, ref entry);
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public int Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32", SetLastError = true)] private static extern bool CloseHandle(nint handle);
}

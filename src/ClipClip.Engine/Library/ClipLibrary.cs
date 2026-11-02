using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipClip.Engine.Core;
using ClipClip.Engine.Media;

namespace ClipClip.Engine.Library;

public sealed class ClipInfo
{
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime Modified { get; set; }
    public DateTime Created { get; set; }
    public double DurationSeconds { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Game { get; set; }
    public string? ThumbnailPath { get; set; }
    public bool Corrupt { get; set; }
    public bool IsFavorite { get; set; }

    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>
/// The folder is the library: clips stay ordinary MP4 files. A cache keyed on path, size and
/// modified time saves reprobing every clip at startup, and rebuilds itself if it goes missing.
/// </summary>
public sealed class ClipLibrary : IDisposable
{
    private readonly object _gate = new();
    private readonly System.Threading.Timer _debounce;
    private Dictionary<string, ClipInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private string _directory;
    private int _refreshing;

    public ClipLibrary(string directory)
    {
        _directory = directory;
        _debounce = new System.Threading.Timer(_ => Refresh());
        LoadCache();
        Watch();
    }

    /// <summary>Raised off the UI thread after the set of clips has changed.</summary>
    public event Action? Changed;

    public string Directory => _directory;

    public IReadOnlyList<ClipInfo> Clips
    {
        get
        {
            lock (_gate) return _cache.Values.Where(c => !c.Corrupt).OrderByDescending(c => c.Created).ToList();
        }
    }

    public void SetDirectory(string directory)
    {
        if (string.Equals(directory, _directory, StringComparison.OrdinalIgnoreCase)) return;
        _directory = directory;
        Watch();
        Refresh();
    }

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            _watcher = new FileSystemWatcher(_directory, "*.mp4")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnFolderChanged;
            _watcher.Deleted += OnFolderChanged;
            _watcher.Renamed += OnFolderChanged;
            _watcher.Changed += OnFolderChanged;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not watch the clip folder: " + ex.Message);
        }
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs args) => _debounce.Change(600, Timeout.Infinite);

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            _debounce.Change(800, Timeout.Infinite);
            return;
        }

        try
        {
            ScanFolder();
            SaveCache();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("Library refresh failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void ScanFolder()
    {
        var found = new Dictionary<string, ClipInfo>(StringComparer.OrdinalIgnoreCase);
        if (System.IO.Directory.Exists(_directory))
        {
            foreach (string path in System.IO.Directory.EnumerateFiles(_directory, "*.mp4"))
            {
                if (System.IO.Path.GetFileName(path).StartsWith(".clipclip-tmp-", StringComparison.Ordinal)) continue;
                var file = new FileInfo(path);

                ClipInfo? known;
                lock (_gate) _cache.TryGetValue(path, out known);
                bool unchanged = known != null && known.SizeBytes == file.Length && known.Modified == file.LastWriteTimeUtc;
                ClipInfo info = unchanged ? known! : Probe(file);
                if (info.ThumbnailPath == null || !File.Exists(info.ThumbnailPath)) EnsureThumbnail(info);
                found[path] = info;
            }
        }
        lock (_gate) _cache = found;
    }

    private static ClipInfo Probe(FileInfo file)
    {
        var info = new ClipInfo
        {
            Path = file.FullName,
            SizeBytes = file.Length,
            Modified = file.LastWriteTimeUtc,
            Created = file.CreationTime,
        };
        try
        {
            ClipProbe probe = ClipProbe.Read(file.FullName, false);
            info.DurationSeconds = probe.DurationSeconds;
            info.Width = probe.Width;
            info.Height = probe.Height;
            info.Game = probe.Game;
        }
        catch (Exception ex)
        {
            // Likely a file still being copied into the folder, or not a real video.
            info.Corrupt = true;
            Log.Warn($"Skipping unreadable clip {file.Name}: {ex.Message}");
        }
        return info;
    }

    private static void EnsureThumbnail(ClipInfo info)
    {
        if (info.Corrupt) return;
        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.ThumbDir);
            string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{info.Path}|{info.SizeBytes}|{info.Modified.Ticks}")));
            string thumbnail = System.IO.Path.Combine(AppPaths.ThumbDir, key[..24] + ".jpg");
            if (!File.Exists(thumbnail))
                Thumbnailer.Create(info.Path, thumbnail, Math.Min(info.DurationSeconds / 2, 3));
            info.ThumbnailPath = thumbnail;
        }
        catch (Exception ex)
        {
            Log.Warn($"Thumbnail failed for {info.Title}: {ex.Message}");
        }
    }

    public void SetFavorite(ClipInfo clip, bool favorite)
    {
        clip.IsFavorite = favorite;
        SaveCache();
    }

    /// <summary>Renames the file. The game tag lives inside the MP4, so it follows along.</summary>
    public string Rename(ClipInfo clip, string newTitle)
    {
        string clean = RecordingEngine.Sanitize(newTitle);
        string target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(clip.Path)!, clean + ".mp4");
        if (string.Equals(target, clip.Path, StringComparison.OrdinalIgnoreCase)) return clip.Path;
        if (File.Exists(target)) throw new IOException($"A clip named \"{clean}\" already exists.");

        File.Move(clip.Path, target);
        lock (_gate)
        {
            _cache.Remove(clip.Path);
            clip.Path = target;
            _cache[target] = clip;
        }
        SaveCache();
        Changed?.Invoke();
        return target;
    }

    /// <summary>Sends the clip to the Recycle Bin, so a mistaken delete can be undone.</summary>
    public void Delete(ClipInfo clip)
    {
        Recycle(clip.Path);
        lock (_gate) _cache.Remove(clip.Path);
        if (clip.ThumbnailPath != null)
        {
            try { File.Delete(clip.ThumbnailPath); }
            catch (IOException) { /* Cached thumbnail, safe to leave. */ }
        }
        SaveCache();
        Changed?.Invoke();
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(AppPaths.LibraryCacheFile)) return;
            var entries = JsonSerializer.Deserialize<List<ClipInfo>>(File.ReadAllText(AppPaths.LibraryCacheFile));
            if (entries == null) return;
            lock (_gate) _cache = entries.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("Library cache ignored: " + ex.Message);
        }
    }

    private void SaveCache()
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.DataDir);
            List<ClipInfo> snapshot;
            lock (_gate) snapshot = _cache.Values.ToList();
            string temp = AppPaths.LibraryCacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
            File.Move(temp, AppPaths.LibraryCacheFile, true);
        }
        catch (Exception ex)
        {
            Log.Warn("Library cache not saved: " + ex.Message);
        }
    }

    private static void Recycle(string path)
    {
        var operation = new ShFileOpStruct
        {
            Func = FoDelete,
            From = path + "\0\0",
            Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
        };
        int result = SHFileOperationW(ref operation);
        if (result != 0 || operation.AnyOperationsAborted)
            throw new IOException($"Could not move the clip to the Recycle Bin (code {result}).");
    }

    public void Dispose()
    {
        _debounce.Dispose();
        _watcher?.Dispose();
    }

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public nint Window;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public nint NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct operation);
}

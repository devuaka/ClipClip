using System.Collections.Concurrent;
using System.Text;

namespace ClipClip.Engine.Core;

/// <summary>Non-blocking file log. Engine threads never wait on disk to write a line.</summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 4096);
    private static readonly object InitLock = new();
    private static bool _started;

    public static string? FilePath { get; private set; }
    public static bool EchoToConsole { get; set; }

    public static void Info(string message) => Add("INFO", message);
    public static void Warn(string message) => Add("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Add("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Add(string level, string message)
    {
        EnsureStarted();
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (EchoToConsole) Console.WriteLine(line);
        Queue.TryAdd(line);
    }

    private static void EnsureStarted()
    {
        if (_started) return;
        lock (InitLock)
        {
            if (_started) return;
            _started = true;
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                FilePath = Path.Combine(AppPaths.LogDir, "clipclip.log");
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 5_000_000)
                    File.Move(FilePath, FilePath + ".old", true);
            }
            catch
            {
                FilePath = null;
            }
            new Thread(Pump) { IsBackground = true, Name = "ClipClip Log" }.Start();
        }
    }

    private static void Pump()
    {
        foreach (string line in Queue.GetConsumingEnumerable())
        {
            if (FilePath == null) continue;
            try { File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* A log failure must never affect recording. */ }
        }
    }
}

public static class AppPaths
{
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("CLIPCLIP_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipClipData");

    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string ThumbDir => Path.Combine(DataDir, "thumbs");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LibraryCacheFile => Path.Combine(DataDir, "library.json");

    public static string DefaultClipDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ClipClip");

    public static string VoiceModelDir => Path.Combine(AppContext.BaseDirectory, "vosk-model");
}

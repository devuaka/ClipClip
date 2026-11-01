using System.Text.Json;
using System.Text.Json.Serialization;
using ClipClip.Engine.Detection;
using ClipClip.Engine.Media;
using Microsoft.Win32;

namespace ClipClip.Engine.Core;

public sealed record HotkeySpec(int VirtualKey, bool Ctrl = false, bool Alt = false, bool Shift = false, bool Win = false)
{
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string KeyName(int key) => key switch
    {
        >= 0x70 and <= 0x87 => "F" + (key - 0x6F),
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x60 and <= 0x69 => "Num " + (key - 0x60),
        0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x13 => "Pause", 0x14 => "Caps Lock",
        0x20 => "Space", 0x21 => "Page Up", 0x22 => "Page Down", 0x23 => "End", 0x24 => "Home",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down", 0x2C => "Print Screen",
        0x2D => "Insert", 0x2E => "Delete", 0x91 => "Scroll Lock",
        0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`",
        0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
        _ => $"Key {key}",
    };
}

public sealed class AppSettings
{
    public int ClipSeconds { get; set; } = 30;
    public HotkeySpec ClipHotkey { get; set; } = new(0x77); // F8
    public HotkeySpec? OpenHotkey { get; set; } = new(0x77, Ctrl: true, Shift: true); // Ctrl+Shift+F8

    public int Fps { get; set; } = 60;
    public ResolutionMode Resolution { get; set; } = ResolutionMode.P1080;
    public QualityPreset Quality { get; set; } = QualityPreset.High;
    public EncoderPreference Encoder { get; set; } = EncoderPreference.Auto;
    public bool CaptureCursor { get; set; } = true;
    public string? MonitorDevice { get; set; }

    public bool RecordOutsideGames { get; set; }
    public bool AutoDetectGames { get; set; } = true;
    public List<CustomGame> CustomGames { get; set; } = [];
    public List<string> IgnoredExes { get; set; } = [];
    public List<CustomGame> SeenGames { get; set; } = [];

    public bool SystemAudioEnabled { get; set; } = true;
    public float SystemVolume { get; set; } = 1f;
    public bool IncludeMicrophone { get; set; }
    public string? MicrophoneId { get; set; }
    public float MicrophoneVolume { get; set; } = 1f;
    public bool MicrophoneMuted { get; set; }

    public bool VoiceEnabled { get; set; }
    public string VoicePhrase { get; set; } = "clip that";
    public double VoiceSensitivity { get; set; } = 0.5;

    public string ClipDirectory { get; set; } = AppPaths.DefaultClipDir;
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool ClipSound { get; set; } = true;
    public bool TrayHintShown { get; set; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            // Keep the unreadable file for the user to inspect, then start from defaults.
            Log.Error("Settings unreadable, using defaults", ex);
            try { File.Move(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bad", true); }
            catch (IOException) { /* Defaults still apply. */ }
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            string temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            Log.Error("Settings not saved", ex);
        }
    }
}

public static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipClip";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) != null;
    }

    public static void Set(bool enabled, string exePath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{exePath}\" --minimized");
        else key.DeleteValue(ValueName, false);
    }
}

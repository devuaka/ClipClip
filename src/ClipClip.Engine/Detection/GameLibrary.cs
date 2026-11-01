using System.Text.Json;
using System.Text.RegularExpressions;
using ClipClip.Engine.Core;
using Microsoft.Win32;

namespace ClipClip.Engine.Detection;

public sealed record InstalledGame(string Name, string InstallDir, string Store);

/// <summary>Reads what the store launchers say is installed. Only local manifests are read.</summary>
public static class GameLibrary
{
    private static readonly Regex NonGameName = new(
        @"redistributable|proton|steam linux runtime|steamworks|steamvr|dedicated server|\bsdk\b|wallpaper engine",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<InstalledGame> Scan()
    {
        var games = new List<InstalledGame>();
        Try("Steam", () => ScanSteam(games));
        Try("Epic", () => ScanEpic(games));
        Try("GOG", () => ScanGog(games));
        Try("Xbox", () => ScanXbox(games));
        return games
            .Where(g => !NonGameName.IsMatch(g.Name))
            .DistinctBy(g => g.InstallDir.ToLowerInvariant())
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Try(string store, Action scan)
    {
        try { scan(); }
        catch (Exception ex) { Log.Warn($"{store} library scan failed: {ex.Message}"); }
    }

    private static void ScanSteam(List<InstalledGame> games)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamPath") is not string steamPath) return;

        var libraries = new List<string> { steamPath };
        string folders = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(folders))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(folders), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
        }

        foreach (string library in libraries.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (string manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text = File.ReadAllText(manifest);
                string? name = VdfValue(text, "name");
                string? dir = VdfValue(text, "installdir");
                if (name == null || dir == null) continue;
                string path = Path.Combine(apps, "common", dir);
                if (Directory.Exists(path)) games.Add(new InstalledGame(name, path, "Steam"));
            }
        }
    }

    private static string? VdfValue(string text, string key)
    {
        Match match = Regex.Match(text, $"\"{key}\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static void ScanEpic(List<InstalledGame> games)
    {
        string manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(manifests)) return;

        foreach (string file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("DisplayName", out JsonElement name) &&
                root.TryGetProperty("InstallLocation", out JsonElement location) &&
                location.GetString() is { Length: > 0 } path && Directory.Exists(path))
            {
                games.Add(new InstalledGame(name.GetString() ?? Path.GetFileName(path), path, "Epic"));
            }
        }
    }

    private static void ScanGog(List<InstalledGame> games)
    {
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (root == null) return;
        foreach (string id in root.GetSubKeyNames())
        {
            using RegistryKey? game = root.OpenSubKey(id);
            if (game?.GetValue("path") is string path && Directory.Exists(path))
                games.Add(new InstalledGame(game.GetValue("gameName") as string ?? Path.GetFileName(path), path, "GOG"));
        }
    }

    private static void ScanXbox(List<InstalledGame> games)
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            string root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(root)) continue;
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(directory);
                if (!name.Equals("GameSave", StringComparison.OrdinalIgnoreCase))
                    games.Add(new InstalledGame(name, directory, "Xbox"));
            }
        }
    }
}

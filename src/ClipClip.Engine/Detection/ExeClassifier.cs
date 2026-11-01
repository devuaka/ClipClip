using System.Reflection.PortableExecutable;
using System.Text;

namespace ClipClip.Engine.Detection;

/// <summary>
/// Judges an executable from files on disk only. Game processes are never opened for reading,
/// because anti-cheat drivers watch for exactly that.
/// </summary>
internal enum GameEvidence { None, Weak, Strong }

internal static class ExeClassifier
{
    private static readonly HashSet<string> NeverGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "applicationframehost",
        "textinputhost", "lockapp", "taskmgr", "systemsettings", "mmc", "regedit", "cmd", "powershell", "pwsh",
        "windowsterminal", "conhost", "notepad", "mspaint", "snippingtool", "calculatorapp",
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "iexplore", "msedgewebview2",
        "vlc", "mpc-hc", "mpc-hc64", "mpc-be64", "wmplayer", "video.ui", "microsoft.media.player", "potplayermini64",
        "spotify", "itunes", "discord", "slack", "teams", "ms-teams", "zoom", "telegram", "whatsapp", "signal",
        "winword", "excel", "powerpnt", "outlook", "onenote", "acrobat", "acrord32", "sumatrapdf",
        "code", "devenv", "rider64", "idea64", "pycharm64", "unity", "unity hub", "unrealeditor", "ue4editor",
        "blender", "photoshop", "illustrator", "afterfx", "premiere pro", "resolve", "obs64", "medal", "clipclip",
        "steam", "steamwebhelper", "steamtours", "epicgameslauncher", "galaxyclient", "battle.net", "origin",
        "eadesktop", "ubisoftconnect", "upc", "riotclientservices", "xboxpcapp", "gamingservices",
        "wallpaper32", "wallpaper64", "wallpaperengine", "lively", "rainmeter", "virtualdesktop.streamer",
        "ovrserver_x64", "oculusclient", "vrserver", "vrmonitor", "vrcompositor",
        "sunshine", "parsecd", "anydesk", "teamviewer", "mstsc", "vmware", "virtualboxvm",
        "nvcontainer", "nvidia app", "nvidia overlay", "amdrsserv", "radeonsoftware",
    };

    private static readonly string[] HelperWords =
        ["crash", "launcher", "setup", "unins", "install", "redist", "updater", "bootstrap", "reporter", "easyanticheat", "beclient", "vcredist", "dxsetup"];

    private static readonly string[] EngineFiles =
    [
        "UnityPlayer.dll", "steam_api64.dll", "steam_api.dll", "EOSSDK-Win64-Shipping.dll", "bink2w64.dll",
        "binkw64.dll", "fmod.dll", "fmodex64.dll", "fmodstudio.dll", "PhysX3_x64.dll", "PhysX_64.dll", "nvngx_dlss.dll",
        "libxess.dll", "amd_fidelityfx_dx12.dll", "GameAssembly.dll", "galaxy64.dll", "discord_game_sdk.dll",
    ];

    private static readonly string[] GraphicsImports =
        ["d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll", "vulkan-1.dll", "xinput1_3.dll", "xinput1_4.dll", "xinput9_1_0.dll", "dinput8.dll"];

    public static bool IsNeverGame(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        if (NeverGames.Contains(name)) return true;

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return exePath.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Launchers and crash reporters live in game folders but are not the game.</summary>
    public static bool IsHelper(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        return HelperWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// How much the files around this exe say it is a game. Strong means a game engine sits beside
    /// it, which is enough on its own for a windowed game. Weak means it only uses a graphics or
    /// gamepad API directly, which also fits ordinary 3D apps, so it needs fullscreen as well.
    /// Electron shells are never evidence, however they are built.
    /// </summary>
    public static GameEvidence Evidence(string exePath)
    {
        try
        {
            string directory = Path.GetDirectoryName(exePath)!;
            if (File.Exists(Path.Combine(directory, "resources", "app.asar")) ||
                File.Exists(Path.Combine(directory, "v8_context_snapshot.bin")))
                return GameEvidence.None;

            if (EngineFiles.Any(file => File.Exists(Path.Combine(directory, file)))) return GameEvidence.Strong;
            string dataFolder = Path.Combine(directory, Path.GetFileNameWithoutExtension(exePath) + "_Data");
            if (Directory.Exists(dataFolder)) return GameEvidence.Strong;
            if (directory.Contains(@"\Binaries\Win64", StringComparison.OrdinalIgnoreCase)) return GameEvidence.Strong;

            return ImportsAny(exePath, GraphicsImports) ? GameEvidence.Weak : GameEvidence.None;
        }
        catch
        {
            return GameEvidence.None;
        }
    }

    private static bool ImportsAny(string exePath, string[] libraries)
    {
        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new PEReader(stream);
        PEHeaders headers = reader.PEHeaders;
        if (headers.PEHeader == null) return false;

        DirectoryEntry imports = headers.PEHeader.ImportTableDirectory;
        if (imports.Size == 0) return false;

        byte[] table = ReadRva(stream, headers, imports.RelativeVirtualAddress, imports.Size);
        for (int offset = 0; offset + 20 <= table.Length; offset += 20)
        {
            int nameRva = BitConverter.ToInt32(table, offset + 12);
            if (nameRva == 0) break;
            byte[] raw = ReadRva(stream, headers, nameRva, 64);
            int length = Array.IndexOf(raw, (byte)0);
            string library = Encoding.ASCII.GetString(raw, 0, length < 0 ? raw.Length : length);
            if (libraries.Contains(library, StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static byte[] ReadRva(FileStream stream, PEHeaders headers, int rva, int size)
    {
        foreach (SectionHeader section in headers.SectionHeaders)
        {
            if (rva < section.VirtualAddress || rva >= section.VirtualAddress + section.VirtualSize) continue;
            var buffer = new byte[size];
            stream.Seek(rva - section.VirtualAddress + section.PointerToRawData, SeekOrigin.Begin);
            stream.ReadAtLeast(buffer, size, false);
            return buffer;
        }
        return new byte[size];
    }
}

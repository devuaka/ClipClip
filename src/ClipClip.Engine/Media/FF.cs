using System.Runtime.InteropServices;
using System.Text;
using ClipClip.Engine.Core;
using FFmpeg.AutoGen;

namespace ClipClip.Engine.Media;

/// <summary>FFmpeg bootstrap and small helpers shared by the encoders and muxer.</summary>
public static unsafe class FF
{
    private static readonly object Gate = new();
    private static bool _ready;

    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_ready) return;
            ffmpeg.RootPath = AppContext.BaseDirectory;
            uint version = ffmpeg.avcodec_version();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            InstallLogCallback();
            _ready = true;
            Log.Info($"FFmpeg avcodec {version >> 16}.{(version >> 8) & 0xFF}.{version & 0xFF} loaded");
        }
    }

    private static av_log_set_callback_callback? _logCallback;

    private static void InstallLogCallback()
    {
        _logCallback = (ptr, level, format, args) =>
        {
            if (level > ffmpeg.AV_LOG_WARNING) return;
            const int size = 1024;
            byte* buffer = stackalloc byte[size];
            int prefix = 1;
            ffmpeg.av_log_format_line(ptr, level, format, args, buffer, size, &prefix);
            string text = Marshal.PtrToStringUTF8((nint)buffer)?.TrimEnd() ?? "";
            if (text.Length > 0) Log.Warn("ffmpeg: " + text);
        };
        ffmpeg.av_log_set_callback(_logCallback);
    }

    public static string Error(int code)
    {
        const int size = 256;
        byte* buffer = stackalloc byte[size];
        ffmpeg.av_strerror(code, buffer, size);
        return Marshal.PtrToStringUTF8((nint)buffer) ?? code.ToString();
    }

    public static void Check(int code, string what)
    {
        if (code < 0) throw new FFmpegException($"{what}: {Error(code)}", code);
    }

    public static void SetOption(void* target, string key, string value)
    {
        int result = ffmpeg.av_opt_set(target, key, value, ffmpeg.AV_OPT_SEARCH_CHILDREN);
        if (result < 0) Log.Warn($"Encoder option {key}={value} rejected: {Error(result)}");
    }

    public static string Utf8(byte* text) => Marshal.PtrToStringUTF8((nint)text) ?? "";

    public static string SafeAscii(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text) builder.Append(c < 128 ? c : '_');
        return builder.ToString();
    }
}

public sealed class FFmpegException(string message, int code) : Exception(message)
{
    public int Code { get; } = code;
}

// hwcontext_d3d11va.h is not part of the generated bindings. Layouts match FFmpeg 9.0.
[StructLayout(LayoutKind.Sequential)]
internal struct AVD3D11VADeviceContext
{
    public nint Device;
    public nint DeviceContext;
    public nint VideoDevice;
    public nint VideoContext;
    public nint Lock;
    public nint Unlock;
    public nint LockCtx;
    public uint BindFlags;
    public uint MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVD3D11VAFramesContext
{
    public nint Texture;
    public uint BindFlags;
    public uint MiscFlags;
    public nint TextureInfos;
}

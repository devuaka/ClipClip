using System.Runtime.InteropServices;

namespace ClipClip.Engine.Capture;

public sealed record DisplayInfo(nint Handle, string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary)
{
    public string Label => $"{Width} x {Height}{(IsPrimary ? "  (primary)" : "")}";
}

public enum CaptureTargetKind { Monitor, Window }

public sealed record CaptureTarget(CaptureTargetKind Kind, nint Handle, string Label)
{
    public static CaptureTarget ForMonitor(DisplayInfo display) => new(CaptureTargetKind.Monitor, display.Handle, display.DeviceName);
    public static CaptureTarget ForWindow(nint hwnd, string label) => new(CaptureTargetKind.Window, hwnd, label);
}

public static class Displays
{
    public static IReadOnlyList<DisplayInfo> Enumerate()
    {
        var result = new List<DisplayInfo>();
        MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                result.Add(new DisplayInfo(monitor, info.Device,
                    info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top,
                    (info.Flags & 1) != 0));
            }
            return true;
        };
        EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        return result;
    }

    public static DisplayInfo Primary() => Enumerate().First(d => d.IsPrimary);

    public static DisplayInfo ForWindow(nint hwnd)
    {
        nint monitor = MonitorFromWindow(hwnd, 2);
        IReadOnlyList<DisplayInfo> all = Enumerate();
        return all.FirstOrDefault(d => d.Handle == monitor) ?? all.First(d => d.IsPrimary);
    }

    /// <summary>Stored by device name, since monitor handles do not survive a reboot.</summary>
    public static DisplayInfo Resolve(string? deviceName)
    {
        IReadOnlyList<DisplayInfo> all = Enumerate();
        return all.FirstOrDefault(d => d.DeviceName == deviceName) ?? all.First(d => d.IsPrimary);
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [DllImport("user32")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);
}

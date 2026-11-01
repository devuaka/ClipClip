using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipClip.Engine.Core;

/// <summary>
/// The single timeline everything is stamped on: absolute QPC in microseconds.
/// WASAPI packet times and WGC frame times are both QPC based, so audio and video
/// line up without any shared start point.
/// </summary>
public static class Clock
{
    private static readonly double UsPerTick = 1_000_000.0 / Stopwatch.Frequency;

    public static long NowUs() => (long)(Stopwatch.GetTimestamp() * UsPerTick);
}

/// <summary>Sub-millisecond thread sleep, without changing the global timer resolution.</summary>
public sealed class PrecisionTimer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 2;
    private const uint TimerAllAccess = 0x1F0003;

    private readonly nint _handle;

    public PrecisionTimer()
    {
        _handle = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (_handle == 0)
            _handle = CreateWaitableTimerExW(0, null, 0, TimerAllAccess);
    }

    public void SleepUs(long us)
    {
        if (us <= 0) return;
        if (_handle == 0)
        {
            Thread.Sleep((int)Math.Max(1, us / 1000));
            return;
        }
        long due = -us * 10;
        if (SetWaitableTimer(_handle, ref due, 0, 0, 0, false))
            WaitForSingleObject(_handle, (uint)(us / 1000 + 100));
    }

    public void Dispose()
    {
        if (_handle != 0) CloseHandle(_handle);
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWaitableTimerExW(nint attrs, string? name, uint flags, uint access);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetWaitableTimer(nint timer, ref long dueTime, int period, nint completion, nint arg, bool resume);

    [DllImport("kernel32", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint ms);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}

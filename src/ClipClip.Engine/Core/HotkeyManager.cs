using System.Runtime.InteropServices;

namespace ClipClip.Engine.Core;

public enum HotkeyResult { Ok, InUse, Reserved }

/// <summary>
/// Global hotkeys on their own message thread, so a busy UI can never delay a clip. RegisterHotKey
/// fails if another app owns the combination, which is the conflict check. MOD_NOREPEAT stops a
/// held key firing repeatedly.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const uint WmHotkey = 0x0312, WmApp = 0x8000, WmQuit = 0x0012;
    private const uint ModAlt = 1, ModControl = 2, ModShift = 4, ModWin = 8, ModNoRepeat = 0x4000;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, Action> _handlers = new();
    private readonly Queue<(Func<HotkeyResult> Work, TaskCompletionSource<HotkeyResult> Done)> _work = new();
    private uint _threadId;

    public HotkeyManager()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "ClipClip Hotkeys", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Registers or replaces the binding in <paramref name="slot"/>. Null clears it.</summary>
    public HotkeyResult Register(int slot, HotkeySpec? hotkey, Action handler)
    {
        return Invoke(() =>
        {
            UnregisterHotKey(0, slot);
            lock (_gate) _handlers.Remove(slot);
            if (hotkey == null) return HotkeyResult.Ok;

            // F12 is reserved for the kernel debugger and fails unpredictably.
            if (hotkey.VirtualKey == 0x7B && !hotkey.Ctrl && !hotkey.Alt && !hotkey.Shift && !hotkey.Win)
                return HotkeyResult.Reserved;

            if (!RegisterHotKey(0, slot, Modifiers(hotkey), (uint)hotkey.VirtualKey))
                return HotkeyResult.InUse;

            lock (_gate) _handlers[slot] = handler;
            return HotkeyResult.Ok;
        });
    }

    /// <summary>True if the combination can be registered right now.</summary>
    public HotkeyResult Probe(HotkeySpec hotkey)
    {
        const int probeSlot = 0xBFFF;
        return Invoke(() =>
        {
            if (!RegisterHotKey(0, probeSlot, Modifiers(hotkey), (uint)hotkey.VirtualKey)) return HotkeyResult.InUse;
            UnregisterHotKey(0, probeSlot);
            return HotkeyResult.Ok;
        });
    }

    private static uint Modifiers(HotkeySpec hotkey) =>
        ModNoRepeat | (hotkey.Ctrl ? ModControl : 0) | (hotkey.Alt ? ModAlt : 0) | (hotkey.Shift ? ModShift : 0) | (hotkey.Win ? ModWin : 0);

    private HotkeyResult Invoke(Func<HotkeyResult> work)
    {
        var done = new TaskCompletionSource<HotkeyResult>();
        lock (_gate) _work.Enqueue((work, done));
        PostThreadMessageW(_threadId, WmApp, 0, 0);
        return done.Task.GetAwaiter().GetResult();
    }

    private void Pump()
    {
        _threadId = GetCurrentThreadId();
        // Forces the thread's message queue to exist before anyone posts to it.
        PeekMessageW(out _, 0, 0, 0, 0);
        _ready.Set();

        while (GetMessageW(out Msg message, 0, 0, 0) > 0)
        {
            if (message.Message == WmApp)
            {
                while (true)
                {
                    (Func<HotkeyResult> Work, TaskCompletionSource<HotkeyResult> Done) item;
                    lock (_gate)
                    {
                        if (_work.Count == 0) break;
                        item = _work.Dequeue();
                    }
                    item.Done.SetResult(item.Work());
                }
            }
            else if (message.Message == WmHotkey)
            {
                Action? handler;
                lock (_gate) _handlers.TryGetValue((int)message.WParam, out handler);
                try { handler?.Invoke(); }
                catch (Exception ex) { Log.Error("Hotkey handler failed", ex); }
            }
        }
    }

    public void Dispose()
    {
        PostThreadMessageW(_threadId, WmQuit, 0, 0);
        _thread.Join(1000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32", SetLastError = true)] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32")] private static extern int GetMessageW(out Msg message, nint window, uint min, uint max);
    [DllImport("user32")] private static extern bool PeekMessageW(out Msg message, nint window, uint min, uint max, uint remove);
    [DllImport("user32")] private static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();
}

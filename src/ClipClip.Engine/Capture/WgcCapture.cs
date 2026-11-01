using System.Runtime.InteropServices;
using ClipClip.Engine.Core;
using Vortice.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ClipClip.Engine.Capture;

/// <summary>
/// Windows.Graphics.Capture source. WGC reads from the compositor, so nothing is injected into
/// the game and anti-cheat has nothing to object to. Frames are dropped here rather than queued:
/// only the newest one is ever kept for the encoder thread to pick up.
/// </summary>
public sealed class WgcCapture : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly object _gate = new();
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;
    private Direct3D11CaptureFrame? _latest;
    private bool _disposed;

    public WgcCapture(GpuDevice gpu, CaptureTarget target, bool includeCursor, int maxFps)
    {
        _gpu = gpu;
        Target = target;
        _item = target.Kind == CaptureTargetKind.Window
            ? CreateItemForWindow(target.Handle)
            : CreateItemForMonitor(target.Handle);
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            gpu.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _poolSize);
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = includeCursor;

        const string sessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";
        if (ApiInformation.IsPropertyPresent(sessionType, nameof(GraphicsCaptureSession.IsBorderRequired)))
        {
            try { _session.IsBorderRequired = false; }
            catch (Exception ex) { Log.Warn("Could not disable the capture border: " + ex.Message); }
        }
        if (ApiInformation.IsPropertyPresent(sessionType, "MinUpdateInterval"))
        {
            // Stops the compositor doing capture work for frames the encoder would discard.
            try { _session.MinUpdateInterval = TimeSpan.FromSeconds(1.0 / (maxFps + 1)); }
            catch (Exception ex) { Log.Warn("Could not set the capture rate: " + ex.Message); }
        }

        _pool.FrameArrived += OnFrameArrived;
        _item.Closed += OnItemClosed;
        _session.StartCapture();
        StartedUs = Clock.NowUs();
        Log.Info($"Capture started: {target.Kind} {target.Label} ({_poolSize.Width}x{_poolSize.Height})");
    }

    public CaptureTarget Target { get; }
    public long StartedUs { get; }
    public bool HasReceivedFrame { get; private set; }
    public bool ItemClosed { get; private set; }

    /// <summary>Hands the newest frame to the caller, who owns disposing it. Null if none is new.</summary>
    public Direct3D11CaptureFrame? TakeLatest()
    {
        lock (_gate)
        {
            Direct3D11CaptureFrame? frame = _latest;
            _latest = null;
            return frame;
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed) return;
            Direct3D11CaptureFrame? newest = null;
            while (sender.TryGetNextFrame() is { } frame)
            {
                newest?.Dispose();
                newest = frame;
            }
            if (newest == null) return;

            HasReceivedFrame = true;
            SizeInt32 size = newest.ContentSize;
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                // Game resized or changed display mode. The pool follows, the encoder does not need to.
                if (size.Width > 0 && size.Height > 0)
                {
                    _poolSize = size;
                    sender.Recreate(_gpu.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
                }
            }

            _latest?.Dispose();
            _latest = newest;
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        ItemClosed = true;
        Log.Info("Capture item closed: " + Target.Label);
    }

    public static ID3D11Texture2D TextureOf(Direct3D11CaptureFrame frame)
    {
        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid iid = typeof(ID3D11Texture2D).GUID;
        Marshal.ThrowExceptionForHR(access.GetInterface(ref iid, out nint pointer));
        return new ID3D11Texture2D(pointer);
    }

    private static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemIid;
        Marshal.ThrowExceptionForHR(interop.CreateForWindow(hwnd, ref iid, out nint pointer));
        return WrapItem(pointer);
    }

    private static GraphicsCaptureItem CreateItemForMonitor(nint monitor)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemIid;
        Marshal.ThrowExceptionForHR(interop.CreateForMonitor(monitor, ref iid, out nint pointer));
        return WrapItem(pointer);
    }

    private static GraphicsCaptureItem WrapItem(nint pointer)
    {
        try { return GraphicsCaptureItem.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _latest?.Dispose();
            _latest = null;
        }
        _pool.FrameArrived -= OnFrameArrived;
        _item.Closed -= OnItemClosed;
        _session.Dispose();
        _pool.Dispose();
    }

    [ComImport, System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(nint window, ref Guid iid, out nint result);
        [PreserveSig] int CreateForMonitor(nint monitor, ref Guid iid, out nint result);
    }

    [ComImport, System.Runtime.InteropServices.Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig] int GetInterface(ref Guid iid, out nint result);
    }
}

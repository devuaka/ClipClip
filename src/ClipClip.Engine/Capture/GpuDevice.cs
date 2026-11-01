using System.Runtime.InteropServices;
using ClipClip.Engine.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ClipClip.Engine.Capture;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel }

public sealed record GpuInfo(string Name, GpuVendor Vendor);

/// <summary>One D3D11 device shared by capture, colour conversion and the hardware encoder,
/// which is what keeps the whole path on the GPU with no CPU readback.</summary>
public sealed class GpuDevice : IDisposable
{
    private const int VendorNvidia = 0x10DE, VendorAmd = 0x1002, VendorIntel = 0x8086;

    private GpuDevice(ID3D11Device device, ID3D11DeviceContext context, GpuInfo info)
    {
        Device = device;
        Context = context;
        Info = info;
        VideoDevice = device.QueryInterface<ID3D11VideoDevice>();
        VideoContext = context.QueryInterface<ID3D11VideoContext1>();

        using (var multithread = device.QueryInterface<ID3D11Multithread>())
            multithread.SetMultithreadProtected(true);

        using var dxgi = device.QueryInterface<IDXGIDevice>();
        // Keeps capture work moving when a game saturates the GPU.
        dxgi.SetGPUThreadPriority(7);
        WinRtDevice = CreateWinRtDevice(dxgi);
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public ID3D11VideoDevice VideoDevice { get; }
    public ID3D11VideoContext1 VideoContext { get; }
    public IDirect3DDevice WinRtDevice { get; }
    public GpuInfo Info { get; }

    /// <summary>Uses the adapter driving <paramref name="monitor"/>, or the first adapter of
    /// <paramref name="requiredVendor"/> when a specific encoder was requested.</summary>
    public static GpuDevice Create(nint monitor, GpuVendor? requiredVendor)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? chosen = null;
        IDXGIAdapter1? fallback = null;

        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
        {
            AdapterDescription1 description = adapter.Description1;
            bool software = (description.Flags & AdapterFlags.Software) != 0;
            if (software)
            {
                adapter.Dispose();
                continue;
            }

            bool matches = requiredVendor is { } vendor
                ? VendorOf(description.VendorId) == vendor
                : DrivesMonitor(adapter, monitor);

            if (matches && chosen == null) chosen = adapter;
            else if (fallback == null) fallback = adapter;
            else adapter.Dispose();
        }

        IDXGIAdapter1? selected = chosen ?? fallback;
        if (selected == null) throw new InvalidOperationException("No Direct3D 11 GPU was found.");
        if (chosen != null) fallback?.Dispose();

        try
        {
            AdapterDescription1 description = selected.Description1;
            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1];
            D3D11.D3D11CreateDevice(selected, DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                levels, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();

            var info = new GpuInfo(description.Description.TrimEnd('\0'), VendorOf(description.VendorId));
            Log.Info($"D3D11 device on {info.Name}");
            return new GpuDevice(device, context, info);
        }
        finally
        {
            selected.Dispose();
        }
    }

    public static IReadOnlyList<GpuInfo> ListAdapters()
    {
        var result = new List<GpuInfo>();
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
        {
            using (adapter)
            {
                AdapterDescription1 description = adapter.Description1;
                if ((description.Flags & AdapterFlags.Software) == 0)
                    result.Add(new GpuInfo(description.Description.TrimEnd('\0'), VendorOf(description.VendorId)));
            }
        }
        return result;
    }

    private static GpuVendor VendorOf(uint id) => (int)id switch
    {
        VendorNvidia => GpuVendor.Nvidia,
        VendorAmd => GpuVendor.Amd,
        VendorIntel => GpuVendor.Intel,
        _ => GpuVendor.Unknown,
    };

    private static bool DrivesMonitor(IDXGIAdapter1 adapter, nint monitor)
    {
        for (uint i = 0; adapter.EnumOutputs(i, out IDXGIOutput output).Success; i++)
        {
            using (output)
            {
                if (output.Description.Monitor == monitor) return true;
            }
        }
        return false;
    }

    private static IDirect3DDevice CreateWinRtDevice(IDXGIDevice dxgi)
    {
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out nint inspectable);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    public void Dispose()
    {
        (WinRtDevice as IDisposable)?.Dispose();
        VideoContext.Dispose();
        VideoDevice.Dispose();
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
    }

    [DllImport("d3d11", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}

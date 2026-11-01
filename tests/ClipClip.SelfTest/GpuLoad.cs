using System.Diagnostics;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using System.Windows.Forms;
using ClipClip.Engine;
using ClipClip.Engine.Audio;
using ClipClip.Engine.Capture;
using ClipClip.Engine.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ClipClip.SelfTest;

/// <summary>
/// fpsimpact: a GPU-bound D3D11 flip-model renderer in a second process, like an uncapped game.
/// Its frame rate is compared across recording off, on, off.
/// </summary>
internal static class GpuLoad
{
    private const int CopiesPerFrame = 120;

    /// <summary>The renderer. Prints frames per second, one line a second.</summary>
    public static int Render(int seconds)
    {
        using var form = new Form
        {
            Text = "ClipClip GPU load", ClientSize = new Size(960, 540), StartPosition = FormStartPosition.Manual,
            Location = new Point(60, 60), ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.FixedToolWindow,
        };
        form.Show();

        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        using (device)
        using (context)
        using (IDXGIDevice dxgi = device.QueryInterface<IDXGIDevice>())
        using (IDXGIAdapter adapter = dxgi.GetAdapter())
        using (IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>())
        using (IDXGISwapChain1 chain = factory.CreateSwapChainForHwnd(device, form.Handle, new SwapChainDescription1
               {
                   Width = 960, Height = 540, Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
                   BufferUsage = Usage.RenderTargetOutput, SwapEffect = SwapEffect.FlipDiscard,
                   SampleDescription = new SampleDescription(1, 0),
               }))
        using (ID3D11Texture2D back = chain.GetBuffer<ID3D11Texture2D>(0))
        using (ID3D11RenderTargetView view = device.CreateRenderTargetView(back))
        {
            var description = new Texture2DDescription
            {
                Width = 3840, Height = 2160, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
            };
            using ID3D11Texture2D a = device.CreateTexture2D(description);
            using ID3D11Texture2D b = device.CreateTexture2D(description);

            var total = Stopwatch.StartNew();
            var second = Stopwatch.StartNew();
            int frames = 0;
            while (total.Elapsed.TotalSeconds < seconds)
            {
                Application.DoEvents();
                for (int i = 0; i < CopiesPerFrame; i++) context.CopyResource(i % 2 == 0 ? a : b, i % 2 == 0 ? b : a);
                float shade = (float)(total.Elapsed.TotalSeconds % 1);
                context.ClearRenderTargetView(view, new Color4(shade, 0.2f, 1 - shade, 1));
                chain.Present(0, PresentFlags.None).CheckError();
                frames++;
                if (second.ElapsedMilliseconds >= 1000)
                {
                    Console.WriteLine($"FPS {frames * 1000.0 / second.ElapsedMilliseconds:F1}");
                    frames = 0;
                    second.Restart();
                }
            }
        }
        return 0;
    }

    public static int Impact()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!, "gpuload 27") { UseShellExecute = false, RedirectStandardOutput = true };
        using Process renderer = Process.Start(info)!;
        var samples = new List<double>();
        renderer.OutputDataReceived += (_, e) =>
        {
            if (e.Data?.StartsWith("FPS ") == true)
                lock (samples) samples.Add(double.Parse(e.Data[4..], System.Globalization.CultureInfo.InvariantCulture));
        };
        renderer.BeginOutputReadLine();

        double Phase(int seconds)
        {
            int start;
            lock (samples) start = samples.Count;
            Thread.Sleep(seconds * 1000);
            lock (samples)
            {
                List<double> part = samples.Skip(start + 1).ToList();
                return part.Count == 0 ? 0 : part.Average();
            }
        }

        Thread.Sleep(2000);
        double before = Phase(8);

        double during;
        var video = new VideoConfig(60, ResolutionMode.P1080, QualityPreset.High, EncoderPreference.Auto, true);
        using (var mic = new MicHub(null))
        using (var engine = new RecordingEngine(30, video, new AudioConfig(true, 1f, false, 1f, false), mic, CaptureTarget.ForMonitor(Displays.Primary())))
        {
            engine.Start();
            Thread.Sleep(1000);
            long framesBefore = engine.FramesEncoded;
            during = Phase(8);
            Console.WriteLine($"recorded {(engine.FramesEncoded - framesBefore) / 8.0:F1} fps with {engine.EncoderName}, dropped {engine.FramesDropped}");
        }

        double after = Phase(6);
        renderer.WaitForExit();
        double baseline = (before + after) / 2;
        Console.WriteLine($"FPS off {before:F1}, on {during:F1}, off {after:F1}: {(baseline - during) / baseline * 100:F1}% cost");
        return 0;
    }
}

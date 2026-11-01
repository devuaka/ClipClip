using ClipClip.Engine;
using ClipClip.Engine.Audio;
using ClipClip.Engine.Capture;
using ClipClip.Engine.Core;
using ClipClip.Engine.Media;

namespace ClipClip.SelfTest;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Tests must never touch the settings of a real install. Set before AppPaths is first read.
        Environment.SetEnvironmentVariable("CLIPCLIP_DATA_DIR", Path.Combine(OutputDir, "data"));
        Log.EchoToConsole = true;
        string command = args.Length > 0 ? args[0] : "basic";
        string[] rest = args.Skip(1).ToArray();
        try
        {
            return command switch
            {
                "basic" => Basic(rest),
                _ => Tests.Run(command, rest),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED: " + ex);
            return 1;
        }
    }

    public static string OutputDir => Path.Combine(Path.GetTempPath(), "ClipClipSelfTest");

    /// <summary>basic [encoder] [seconds to record] [clip seconds]</summary>
    private static int Basic(string[] args)
    {
        EncoderPreference encoder = args.Length > 0 ? Enum.Parse<EncoderPreference>(args[0], true) : EncoderPreference.Auto;
        int record = args.Length > 1 ? int.Parse(args[1]) : 8;
        int clip = args.Length > 2 ? int.Parse(args[2]) : 5;

        Directory.CreateDirectory(OutputDir);
        var video = new VideoConfig(60, ResolutionMode.Native, QualityPreset.High, encoder, true);
        var audio = new AudioConfig(true, 1f, false, 1f, false);
        using var mic = new MicHub(null);
        using var engine = new RecordingEngine(clip, video, audio, mic, CaptureTarget.ForMonitor(Displays.Primary()));
        engine.Start();
        for (int i = 0; i < record; i++)
        {
            Thread.Sleep(1000);
            Console.WriteLine($"t={i + 1}s enc={engine.EncoderName} frames={engine.FramesEncoded} dropped={engine.FramesDropped} " +
                              $"buffered={engine.BufferedSeconds:F1}s live={engine.BufferBytes / 1048576.0:F1}MB cap={engine.BufferCapacityBytes / 1048576.0:F0}MB");
        }

        ClipResult result = engine.SaveClip(OutputDir, "SelfTest", ClipTrigger.Automation)!.GetAwaiter().GetResult();
        Console.WriteLine(result);
        if (!result.Success) return 1;
        ClipProbe probe = ClipProbe.Read(result.Path!, true);
        Console.WriteLine($"probe: {probe.Width}x{probe.Height} {probe.DurationSeconds:F2}s audio={probe.HasAudio} decodable={probe.HasDecodableVideo} game={probe.Game}");
        return probe.HasDecodableVideo ? 0 : 1;
    }
}

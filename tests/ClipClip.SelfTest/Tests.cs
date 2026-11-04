using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using ClipClip.Engine;
using ClipClip.Engine.Audio;
using ClipClip.Engine.Capture;
using ClipClip.Engine.Core;
using ClipClip.Engine.Detection;
using ClipClip.Engine.Library;
using ClipClip.Engine.Media;
using ClipClip.Engine.Voice;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClipClip.SelfTest;

internal static class Tests
{
    public static int Run(string command, string[] args) => command switch
    {
        "devices" => Devices(),
        "multiclip" => MultiClip(),
        "soak" => Soak(args.Length > 0 ? int.Parse(args[0]) : 60),
        "thumb" => Thumb(args[0], args[1]),
        "sync" => Sync(args),
        "voicewav" => VoiceWav(args),
        "voice" => VoiceEndToEnd(args),
        "fakegame" => FakeGame.Run(args),
        "customgame" => CustomGameTest(),
        "autodetect" => AutoDetect(args),
        "micloss" => MicLoss(args),
        "resize" => ResizeTest(),
        "recovery" => Recovery(),
        "trim" => TrimTest(),
        "studio" => StudioTest(),
        "recordwindow" => RecordWindow(args[0], int.Parse(args[1]), args[2], args[3]),
        "ring" => RingTests.Run(),
        "gpuload" => GpuLoad.Render(int.Parse(args[0])),
        "fpsimpact" => GpuLoad.Impact(),
        _ => Unknown(command),
    };

    private static int Unknown(string command)
    {
        Console.WriteLine("Unknown test: " + command);
        return 2;
    }

    private static int Devices()
    {
        foreach (GpuInfo gpu in GpuDevice.ListAdapters()) Console.WriteLine($"GPU {gpu.Vendor}: {gpu.Name}");
        foreach (DisplayInfo display in Displays.Enumerate()) Console.WriteLine($"Display {display.DeviceName} {display.Label}");
        foreach (AudioDeviceInfo device in AudioDevices.Microphones()) Console.WriteLine($"MIC {device.Name}");
        foreach (AudioDeviceInfo device in AudioDevices.Outputs()) Console.WriteLine($"OUT {device.Name}");
        foreach (InstalledGame game in GameLibrary.Scan()) Console.WriteLine($"GAME [{game.Store}] {game.Name}");
        return 0;
    }

    private static RecordingEngine NewEngine(int clipSeconds, AudioConfig? audio = null, MicHub? mic = null, CaptureTarget? target = null,
        EncoderPreference encoder = EncoderPreference.Auto, ResolutionMode resolution = ResolutionMode.P1080)
    {
        var video = new VideoConfig(60, resolution, QualityPreset.High, encoder, true);
        return new RecordingEngine(clipSeconds, video, audio ?? new AudioConfig(true, 1f, false, 1f, false),
            mic ?? new MicHub(null), target ?? CaptureTarget.ForMonitor(Displays.Primary()));
    }

    /// <summary>Saving must not interrupt recording, and repeats must not make duplicates.</summary>
    private static int MultiClip()
    {
        string directory = Path.Combine(Program.OutputDir, "multi");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        Directory.CreateDirectory(directory);

        using RecordingEngine engine = NewEngine(5);
        engine.Start();
        Thread.Sleep(7000);

        long framesBefore = engine.FramesEncoded;
        long droppedBefore = engine.FramesDropped;
        var saves = new List<Task<ClipResult>>();
        var rejected = 0;
        for (int i = 0; i < 4; i++)
        {
            Task<ClipResult>? first = engine.SaveClip(directory, "Multi", ClipTrigger.Automation);
            if (first != null) saves.Add(first);
            // Simulates key bounce and hotkey plus voice firing together.
            for (int repeat = 0; repeat < 5; repeat++)
            {
                if (engine.SaveClip(directory, "Multi", ClipTrigger.Automation) == null) rejected++;
            }
            Thread.Sleep(1500);
        }
        Task.WaitAll(saves.ToArray());
        Thread.Sleep(500);

        long framesAfter = engine.FramesEncoded;
        double seconds = 6.5;
        double fpsDuring = (framesAfter - framesBefore) / seconds;
        Console.WriteLine($"accepted={saves.Count} debounced={rejected} droppedDuring={engine.FramesDropped - droppedBefore} fpsDuring={fpsDuring:F1}");

        bool ok = saves.Count == 4 && rejected == 20;
        foreach (Task<ClipResult> save in saves)
        {
            ClipResult result = save.Result;
            ClipProbe probe = ClipProbe.Read(result.Path!, true);
            Console.WriteLine($"{Path.GetFileName(result.Path)} {probe.DurationSeconds:F2}s {probe.Width}x{probe.Height} audio={probe.HasAudio} ok={probe.HasDecodableVideo}");
            ok &= probe.HasDecodableVideo && probe.HasAudio && probe.DurationSeconds is >= 4.9 and <= 6.2;
        }
        ok &= engine.IsEncoding && fpsDuring > 55;
        ok &= Directory.GetFiles(directory, ".clipclip-tmp-*").Length == 0;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>Runs well past the retention window and watches for memory growth.</summary>
    private static int Soak(int seconds)
    {
        using RecordingEngine engine = NewEngine(5);
        engine.Start();
        using Process process = Process.GetCurrentProcess();
        long baseline = 0;
        for (int i = 1; i <= seconds; i++)
        {
            Thread.Sleep(1000);
            if (i % 10 != 0) continue;
            process.Refresh();
            GC.Collect();
            if (i == 20) baseline = process.WorkingSet64;
            Console.WriteLine($"t={i}s buffered={engine.BufferedSeconds:F1}s live={engine.BufferBytes / 1048576.0:F1}MB ws={process.WorkingSet64 / 1048576}MB dropped={engine.FramesDropped}");
        }
        process.Refresh();
        double growth = (process.WorkingSet64 - baseline) / 1048576.0;
        Console.WriteLine($"working set growth after buffer filled: {growth:F1} MB");
        bool ok = engine.BufferedSeconds is > 8 and < 10.5 && growth < 40;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    private static int Thumb(string video, string output)
    {
        Thumbnailer.Create(video, output);
        Console.WriteLine($"{output}: {new FileInfo(output).Length} bytes");
        return 0;
    }

    private static MMDevice FindRender(string text)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .First(d => d.FriendlyName.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private static AudioDeviceInfo FindMic(string text) =>
        AudioDevices.Microphones().First(d => d.Name.Contains(text, StringComparison.OrdinalIgnoreCase));

    private static string FindOutputId(string text) =>
        AudioDevices.Outputs().First(d => d.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Id;

    /// <summary>
    /// sync [systemOut] [micOut] [micIn]. Every second a window flips white while a 1 kHz tone
    /// plays to the loopback device and 3 kHz plays through a virtual cable into the mic input.
    /// Measuring those events in the saved clip gives real video/system/mic offsets.
    /// </summary>
    private static int Sync(string[] args)
    {
        string systemOut = args.Length > 0 ? args[0] : "Line 1";
        string micOut = args.Length > 1 ? args[1] : "CABLE Input";
        string micIn = args.Length > 2 ? args[2] : "CABLE Output";

        string directory = Path.Combine(Program.OutputDir, "sync");
        Directory.CreateDirectory(directory);

        using var form = new Form
        {
            Text = "ClipClip sync test", Width = 480, Height = 360, BackColor = Color.Black,
            StartPosition = FormStartPosition.Manual, Location = new Point(40, 40),
            ShowInTaskbar = false,
        };
        form.Show();
        Application.DoEvents();

        using var mic = new MicHub(FindMic(micIn).Id);
        var audio = new AudioConfig(true, 1f, true, 1f, false, FindOutputId(systemOut));
        using RecordingEngine engine = NewEngine(8, audio, mic, CaptureTarget.ForWindow(form.Handle, "sync"), resolution: ResolutionMode.P720);
        engine.Start();

        using var systemTone = new TonePlayer(FindRender(systemOut), 1000);
        using var micTone = new TonePlayer(FindRender(micOut), 3000);

        var watch = Stopwatch.StartNew();
        long next = 1500;
        long whiteUntil = 0;
        int events = 0;
        while (watch.ElapsedMilliseconds < 9000 || whiteUntil != 0)
        {
            Application.DoEvents();
            if (watch.ElapsedMilliseconds >= next)
            {
                form.BackColor = Color.White;
                form.Refresh();
                systemTone.Beep();
                micTone.Beep();
                whiteUntil = next + 200;
                next += 1000;
                events++;
            }
            if (whiteUntil != 0 && watch.ElapsedMilliseconds >= whiteUntil)
            {
                form.BackColor = Color.Black;
                form.Refresh();
                whiteUntil = 0;
            }
            Thread.Sleep(1);
        }
        Thread.Sleep(400);

        ClipResult result = engine.SaveClip(directory, "Sync", ClipTrigger.Automation)!.GetAwaiter().GetResult();
        Console.WriteLine($"{result.Path} {result.Seconds:F2}s events={events}");
        if (!result.Success) return 1;

        SyncReport report = ClipAnalysis.Measure(result.Path!);
        Console.WriteLine($"flashes={report.Flashes.Count} system beeps={report.SystemBeeps.Count} mic beeps={report.MicBeeps.Count}");
        var videoToSystem = ClipAnalysis.Offsets(report.Flashes, report.SystemBeeps);
        var videoToMic = ClipAnalysis.Offsets(report.Flashes, report.MicBeeps);
        var systemToMic = ClipAnalysis.Offsets(report.SystemBeeps, report.MicBeeps);
        Console.WriteLine($"video->system ms: {Summary(videoToSystem)}");
        Console.WriteLine($"video->mic ms:    {Summary(videoToMic)}");
        Console.WriteLine($"system->mic ms:   {Summary(systemToMic)}");

        bool ok = videoToSystem.Count >= 4 && systemToMic.Count >= 4 &&
                  Math.Abs(videoToSystem.Average()) < 60 && Spread(videoToSystem) < 40 && Spread(systemToMic) < 40;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    private static string Summary(List<double> values) =>
        values.Count == 0 ? "none" : $"mean {values.Average():F1}  min {values.Min():F1}  max {values.Max():F1}  n={values.Count}";

    private static double Spread(List<double> values) => values.Count == 0 ? 0 : values.Max() - values.Min();

    private static short[] ReadWav16k(string path)
    {
        using var reader = new WaveFileReader(path);
        if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1)
            throw new InvalidDataException("Expected 16 kHz mono 16-bit: " + reader.WaveFormat);
        byte[] bytes = new byte[reader.Length];
        reader.ReadExactly(bytes);
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    /// <summary>voicewav phrase file... : direct recognizer accuracy check.</summary>
    private static int VoiceWav(string[] args)
    {
        string phrase = args[0];
        foreach (string file in args.Skip(1))
        {
            IReadOnlyList<VoiceHit> hits = VoiceTrigger.Recognize(phrase, ReadWav16k(file), 0.5);
            string summary = hits.Count == 0 ? "no match" : string.Join(", ", hits.Select(h => $"{h.Confidence:F2}{(h.Accepted ? "*" : "")}"));
            Console.WriteLine($"{Path.GetFileName(file),-28} {summary}");
        }
        return 0;
    }

    /// <summary>voice wav [micOut] [micIn]: real path. Plays speech into a virtual cable and
    /// expects the controller to save a clip with the Voice trigger, alongside hotkey clipping.</summary>
    private static int VoiceEndToEnd(string[] args)
    {
        string wav = args[0];
        string micOut = args.Length > 1 ? args[1] : "CABLE Input";
        string micIn = args.Length > 2 ? args[2] : "CABLE Output";

        string directory = Path.Combine(Program.OutputDir, "voice");
        Directory.CreateDirectory(directory);

        var settings = new AppSettings
        {
            ClipSeconds = 5, RecordOutsideGames = true, VoiceEnabled = true, MicrophoneId = FindMic(micIn).Id,
            ClipDirectory = directory, Resolution = ResolutionMode.P720, ClipHotkey = new HotkeySpec(0x86), OpenHotkey = null,
        };
        using var controller = new ClipController(settings);
        var saved = new List<ClipResult>();
        controller.ClipSaved += result => { lock (saved) saved.Add(result); };
        controller.VoiceHeard += hit => Console.WriteLine($"heard: {hit.Text} conf={hit.Confidence:F2} accepted={hit.Accepted}");
        controller.Start();

        for (int i = 0; i < 100 && controller.VoiceStatus != VoiceState.Listening; i++) Thread.Sleep(100);
        Console.WriteLine($"voice status: {controller.VoiceStatus} mode={controller.Mode}");
        Thread.Sleep(3000);

        var watch = Stopwatch.StartNew();
        using (var player = new WasapiOut(FindRender(micOut), AudioClientShareMode.Shared, false, 50))
        using (var reader = new WaveFileReader(wav))
        {
            player.Init(reader);
            player.Play();
            while (player.PlaybackState == PlaybackState.Playing) Thread.Sleep(20);
        }
        double playedMs = watch.Elapsed.TotalMilliseconds;

        while (watch.ElapsedMilliseconds < playedMs + 4000)
        {
            lock (saved) if (saved.Count > 0) break;
            Thread.Sleep(50);
        }
        double latencyMs = watch.Elapsed.TotalMilliseconds - playedMs;

        ClipResult? voiceClip;
        lock (saved) voiceClip = saved.FirstOrDefault(r => r.Trigger == ClipTrigger.Voice);
        Console.WriteLine($"clip: {voiceClip} (saved {latencyMs:F0} ms after speech ended)");
        bool ok = voiceClip is { Success: true } && controller.Engine is { IsEncoding: true };
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>Custom game: recognised on launch, recorded, clip tagged, stops when it exits.</summary>
    private static int CustomGameTest()
    {
        string directory = Path.Combine(Program.OutputDir, "custom");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        Directory.CreateDirectory(directory);

        string exe = Environment.ProcessPath!;
        var settings = new AppSettings
        {
            ClipSeconds = 4, RecordOutsideGames = false, AutoDetectGames = false, ClipDirectory = directory,
            Resolution = ResolutionMode.P720, ClipHotkey = new HotkeySpec(0x86), OpenHotkey = null,
        };
        settings.CustomGames.Add(new CustomGame { Name = "Fake Game", ExePath = exe, Enabled = true });

        using var controller = new ClipController(settings);
        var clip = new TaskCompletionSource<ClipResult>();
        controller.ClipSaved += r => clip.TrySetResult(r);
        controller.Start();
        Thread.Sleep(1500);
        bool waitingBefore = controller.Mode == RecordingMode.WaitingForGame && controller.Engine == null;
        Console.WriteLine($"before launch: {controller.Mode}");

        using Process game = Process.Start(new ProcessStartInfo(exe, "fakegame red 30") { UseShellExecute = false })!;
        bool detected = WaitFor(() => controller.CurrentGame != null, 6000);
        Console.WriteLine($"detected: {controller.CurrentGame}");
        bool recording = WaitFor(() => controller.Mode == RecordingMode.Recording, 5000);
        Console.WriteLine($"mode: {controller.Mode} target: {controller.Engine?.CurrentTarget}");
        Thread.Sleep(6000);

        controller.ClipNow(ClipTrigger.Automation);
        ClipResult result = clip.Task.GetAwaiter().GetResult();
        Console.WriteLine(result);

        ClipProbe probe = ClipProbe.Read(result.Path!, true);
        (double r, double g, double b) = ClipAnalysis.AverageColor(result.Path!);
        Console.WriteLine($"game tag={probe.Game} {probe.DurationSeconds:F2}s avg rgb=({r:F0},{g:F0},{b:F0})");

        game.Kill();
        bool cleared = WaitFor(() => controller.CurrentGame == null, 4000);
        bool stopped = WaitFor(() => controller.Mode == RecordingMode.WaitingForGame, 4000);
        Console.WriteLine($"after exit: game={controller.CurrentGame?.Name ?? "none"} mode={controller.Mode} bufferKept={controller.Engine?.BufferedSeconds:F1}s");

        bool ok = waitingBefore && detected && recording && result.Success && probe.Game == "Fake Game" && r > g + 40 && r > b + 40 && cleared && stopped;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>autodetect exe [exe args]: launches a real game build and checks automatic detection.</summary>
    private static int AutoDetect(string[] args)
    {
        string directory = Path.Combine(Program.OutputDir, "auto");
        Directory.CreateDirectory(directory);
        var settings = new AppSettings
        {
            ClipSeconds = 6, RecordOutsideGames = false, AutoDetectGames = true, ClipDirectory = directory,
            Resolution = ResolutionMode.P1080, ClipHotkey = new HotkeySpec(0x86), OpenHotkey = null,
        };
        using var controller = new ClipController(settings);
        var clip = new TaskCompletionSource<ClipResult>();
        controller.ClipSaved += r => clip.TrySetResult(r);
        controller.Start();
        Thread.Sleep(1500);
        Console.WriteLine($"before: {controller.Mode}");

        using Process game = Process.Start(new ProcessStartInfo(args[0], string.Join(' ', args.Skip(1))) { UseShellExecute = false })!;
        bool detected = WaitFor(() => controller.CurrentGame != null, 45000);
        Console.WriteLine($"detected={detected}: {controller.CurrentGame}");
        bool recording = detected && WaitFor(() => controller.Mode == RecordingMode.Recording, 8000);
        Console.WriteLine($"mode={controller.Mode} target={controller.Engine?.CurrentTarget} enc={controller.Engine?.EncoderName}");
        if (recording)
        {
            Thread.Sleep(10000);
            RecordingEngine engine = controller.Engine!;
            Console.WriteLine($"frames={engine.FramesEncoded} dropped={engine.FramesDropped}");
            controller.ClipNow(ClipTrigger.Automation);
            ClipResult result = clip.Task.GetAwaiter().GetResult();
            Console.WriteLine(result);
            if (result.Path != null)
            {
                ClipProbe probe = ClipProbe.Read(result.Path, true);
                Console.WriteLine($"tag={probe.Game} {probe.Width}x{probe.Height} {probe.DurationSeconds:F2}s");
            }
        }
        try { game.Kill(true); }
        catch (Exception ex) { Console.WriteLine("kill: " + ex.Message); }
        bool cleared = WaitFor(() => controller.CurrentGame == null, 5000);
        Console.WriteLine($"after exit: mode={controller.Mode}");
        bool ok = detected && recording && cleared;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>micloss [micIn]: the selected mic disappears mid-recording.</summary>
    private static int MicLoss(string[] args)
    {
        string directory = Path.Combine(Program.OutputDir, "micloss");
        Directory.CreateDirectory(directory);
        string micIn = args.Length > 0 ? args[0] : "CABLE Output";
        using var mic = new MicHub(FindMic(micIn).Id);
        var audio = new AudioConfig(true, 1f, true, 1f, false);
        using RecordingEngine engine = NewEngine(4, audio, mic);
        engine.Start();
        Thread.Sleep(3000);
        Console.WriteLine($"mic: {mic.State} {mic.DeviceName}");
        bool runningBefore = mic.State == AudioSourceState.Running;

        // Pointing at an endpoint that no longer exists is what the source sees after an unplug.
        mic.SetDevice("{0.0.1.00000000}.{00000000-dead-beef-0000-000000000000}");
        bool lost = WaitFor(() => mic.State == AudioSourceState.Disconnected, 3000);
        Console.WriteLine($"after removal: {mic.State}");
        Thread.Sleep(2500);

        ClipResult during = engine.SaveClip(directory, "MicLoss", ClipTrigger.Automation)!.GetAwaiter().GetResult();
        Console.WriteLine($"clip while mic gone: {during.Success} {during.Seconds:F2}s encoding={engine.IsEncoding}");

        mic.SetDevice(FindMic(micIn).Id);
        bool back = WaitFor(() => mic.State == AudioSourceState.Running, 4000);
        Console.WriteLine($"after reconnect: {mic.State}");

        bool ok = runningBefore && lost && during.Success && engine.IsEncoding && back;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>A window changing size and fullscreen state must not restart the encoder or lose buffer.</summary>
    private static int ResizeTest()
    {
        string directory = Path.Combine(Program.OutputDir, "resize");
        Directory.CreateDirectory(directory);

        using var form = new Form { Text = "ClipClip resize test", Width = 640, Height = 360, BackColor = Color.SeaGreen, ShowInTaskbar = false };
        form.Show();
        Application.DoEvents();

        using RecordingEngine engine = NewEngine(6, target: CaptureTarget.ForWindow(form.Handle, "resize"), resolution: ResolutionMode.P720);
        engine.Start();
        Pump(2000);

        (int w, int h)[] sizes = [(1280, 720), (800, 600), (1600, 500), (500, 900), (640, 360)];
        foreach ((int w, int h) in sizes)
        {
            form.Size = new Size(w, h);
            Pump(700);
        }
        form.WindowState = FormWindowState.Maximized;
        Pump(700);
        form.WindowState = FormWindowState.Normal;
        Pump(700);

        Console.WriteLine($"frames={engine.FramesEncoded} dropped={engine.FramesDropped} buffered={engine.BufferedSeconds:F1}s size={engine.OutputSize}");
        ClipResult result = engine.SaveClip(directory, "Resize", ClipTrigger.Automation)!.GetAwaiter().GetResult();
        Console.WriteLine(result);
        ClipProbe probe = ClipProbe.Read(result.Path!, true);
        Console.WriteLine($"{probe.Width}x{probe.Height} {probe.DurationSeconds:F2}s decodable={probe.HasDecodableVideo}");

        bool ok = result.Success && probe.HasDecodableVideo && result.Seconds >= 5.9 && engine.IsEncoding;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;

        static void Pump(int ms)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
        }
    }

    /// <summary>recordwindow title seconds game dir: records one window, for demo footage.</summary>
    private static int RecordWindow(string title, int seconds, string game, string directory)
    {
        Process target = Process.GetProcesses().First(p => p.MainWindowTitle == title);
        Directory.CreateDirectory(directory);
        var audio = new AudioConfig(false, 1f, false, 1f, false);
        using RecordingEngine engine = NewEngine(seconds, audio, target: CaptureTarget.ForWindow(target.MainWindowHandle, game));
        engine.Start();
        Thread.Sleep((seconds + 2) * 1000);
        ClipResult result = engine.SaveClip(directory, game, ClipTrigger.Automation)!.GetAwaiter().GetResult();
        Console.WriteLine(result);
        return result.Success ? 0 : 1;
    }

    private static int StudioTest()
    {
        string directory = Path.Combine(Program.OutputDir, "studio");
        Directory.CreateDirectory(directory);
        string source;
        using (RecordingEngine engine = NewEngine(6))
        {
            engine.Start();
            Thread.Sleep(8000);
            source = engine.SaveClip(directory, "Studio", ClipTrigger.Automation)!.GetAwaiter().GetResult().Path!;
        }
        var project = new ClipClip.Engine.Editing.EditProject { Aspect = ClipClip.Engine.Editing.ExportAspect.Vertical, MusicPath = source, MusicVolume = 0.3f };
        project.Segments.Add(new() { SourcePath = source, In = 0.5, Out = 2.5, Caption = "Test caption", Placement = ClipClip.Engine.Editing.CaptionPlacement.Top });
        project.Segments.Add(new() { SourcePath = source, In = 3, Out = 5, Speed = 2, Volume = 0.5f });
        project.Segments.Add(new() { SourcePath = source, In = 1, Out = 2, Speed = 0.5, Muted = true });
        var watch = Stopwatch.StartNew();
        string output = ClipClip.Engine.Editing.Exporter.Export(project, directory, "studio_out", null, CancellationToken.None);
        ClipProbe probe = ClipProbe.Read(output, true);
        Console.WriteLine($"export {watch.Elapsed.TotalSeconds:F1}s -> {probe.Width}x{probe.Height} {probe.DurationSeconds:F2}s audio={probe.HasAudio} decodable={probe.HasDecodableVideo}");
        Thumbnailer.Create(output, Path.Combine(Path.GetTempPath(), "clipclip_studio_thumb.jpg"), 0.5);
        bool ok = probe.Width == 1080 && probe.Height == 1920 && probe.HasAudio && probe.HasDecodableVideo && probe.DurationSeconds is > 4.5 and < 5.6;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    private static int TrimTest()
    {
        string directory = Path.Combine(Program.OutputDir, "trim");
        Directory.CreateDirectory(directory);
        string source;
        using (RecordingEngine engine = NewEngine(7))
        {
            engine.Start();
            Thread.Sleep(9000);
            source = engine.SaveClip(directory, "Trim", ClipTrigger.Automation)!.GetAwaiter().GetResult().Path!;
        }
        string kept = ClipEditor.Trim(source, 2.2, 5.5, false);
        string silent = ClipEditor.Trim(source, 1, 4, true);
        ClipProbe a = ClipProbe.Read(kept, true);
        ClipProbe b = ClipProbe.Read(silent, true);
        Console.WriteLine($"with audio {a.DurationSeconds:F2}s audio={a.HasAudio} game={a.Game} decodable={a.HasDecodableVideo}");
        Console.WriteLine($"no audio {b.DurationSeconds:F2}s audio={b.HasAudio} decodable={b.HasDecodableVideo}");
        bool ok = a.HasAudio && !b.HasAudio && a.HasDecodableVideo && b.HasDecodableVideo &&
                  a.DurationSeconds is > 3 and < 4.8 && b.DurationSeconds is > 2.5 and < 3.6 && a.Game == "Trim";
        Directory.Delete(directory, true);
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>A failed video session must restart itself, keep clipping, and fall back to
    /// software if the hardware encoder keeps failing. Buffer length changes must apply live.</summary>
    private static int Recovery()
    {
        string directory = Path.Combine(Program.OutputDir, "recovery");
        Directory.CreateDirectory(directory);
        using RecordingEngine engine = NewEngine(4);
        engine.Start();
        WaitFor(() => engine.IsEncoding, 5000);
        Thread.Sleep(3000);
        string first = engine.EncoderName!;

        engine.InjectVideoFailure();
        bool down = WaitFor(() => !engine.IsEncoding, 2000);
        bool back = WaitFor(() => engine.IsEncoding, 6000);
        Thread.Sleep(2500);
        Console.WriteLine($"one failure: down={down} back={back} encoder={engine.EncoderName}");
        ClipResult clip = engine.SaveClip(directory, "Recovery", ClipTrigger.Automation)!.GetAwaiter().GetResult();
        Console.WriteLine($"clip after restart: ok={clip.Success} {clip.Seconds:F1}s");

        // Repeated failures right after starting look like a broken encoder.
        for (int i = 0; i < 2 && engine.EncoderName == first; i++)
        {
            WaitFor(() => engine.IsEncoding, 6000);
            engine.InjectVideoFailure();
            Thread.Sleep(300);
            WaitFor(() => !engine.IsEncoding, 2000);
        }
        WaitFor(() => engine.IsEncoding && engine.EncoderName != first, 8000);
        Thread.Sleep(2000);
        Console.WriteLine($"after repeats: encoder={engine.EncoderName} hardware={engine.EncoderIsHardware}");
        Thread.Sleep(1000);
        Task<ClipResult>? demoted = engine.SaveClip(directory, "Demoted", ClipTrigger.Automation);
        ClipResult software = demoted!.GetAwaiter().GetResult();
        Console.WriteLine($"software clip: ok={software.Success} {software.Seconds:F1}s");

        engine.SetClipSeconds(12);
        Thread.Sleep(9000);
        ClipResult longer = engine.SaveClip(directory, "Longer", ClipTrigger.Automation)!.GetAwaiter().GetResult();
        Console.WriteLine($"length raised live: {longer.Seconds:F1}s buffered={engine.BufferedSeconds:F1}s");

        bool ok = down && back && clip.Success && software.Success && !engine.EncoderIsHardware && longer.Seconds >= 8.5;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}

/// <summary>Plays short sine bursts with an already-running stream, for low jitter.</summary>
internal sealed class TonePlayer : IDisposable
{
    private readonly WasapiOut _output;
    private readonly BufferedWaveProvider _provider;
    private readonly byte[] _burst;

    public TonePlayer(MMDevice device, double frequency)
    {
        _output = new WasapiOut(device, AudioClientShareMode.Shared, false, 20);
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(_output.OutputWaveFormat.SampleRate, _output.OutputWaveFormat.Channels);
        _provider = new BufferedWaveProvider(format) { BufferDuration = TimeSpan.FromSeconds(2), ReadFully = true, DiscardOnBufferOverflow = true };
        _output.Init(_provider);
        _output.Play();

        int frames = format.SampleRate / 10;
        var samples = new float[frames * format.Channels];
        for (int i = 0; i < frames; i++)
        {
            float value = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / format.SampleRate));
            for (int channel = 0; channel < format.Channels; channel++)
                samples[i * format.Channels + channel] = value;
        }
        _burst = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, _burst, 0, _burst.Length);
    }

    public void Beep() => _provider.AddSamples(_burst, 0, _burst.Length);

    public void Dispose()
    {
        _output.Stop();
        _output.Dispose();
    }
}

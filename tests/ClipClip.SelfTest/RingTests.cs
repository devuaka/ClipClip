using ClipClip.Engine.Media;

namespace ClipClip.SelfTest;

/// <summary>Ring correctness with synthetic packets whose bytes are checked after every path.</summary>
internal static unsafe class RingTests
{
    private const long Second = 1_000_000;

    public static int Run()
    {
        bool ok = true;
        foreach (bool disk in new[] { false, true })
        {
            ok &= Check($"wrap and integrity (disk={disk})", () => WrapIntegrity(disk));
            ok &= Check($"plan starts on keyframe (disk={disk})", () => PlanKeyframe(disk));
        }
        ok &= Check("evicted packet is refused", EvictedRead);
        ok &= Check("failed write leaves no file", FailedWriteLeavesNothing);
        ok &= Check("resize keeps newest in order", ResizeKeepsOrder);
        ok &= Check("low bitrate stays in a small span", SpanFollowsBitrate);
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    private static bool Check(string name, Func<bool> test)
    {
        bool result;
        try { result = test(); }
        catch (Exception ex)
        {
            Console.WriteLine("  " + ex);
            result = false;
        }
        Console.WriteLine($"{(result ? "ok  " : "FAIL")} {name}");
        return result;
    }

    private static byte[] Payload(int index, int size)
    {
        var data = new byte[size];
        new Random(index).NextBytes(data);
        return data;
    }

    private static void Add(PacketRing ring, StreamKind kind, int index, int size, long ptsUs, bool key, int epoch = 1)
    {
        byte[] data = Payload(index, size);
        fixed (byte* pointer = data) ring.Append(kind, pointer, size, ptsUs, key, epoch);
    }

    private static bool Matches(PacketRing ring, in PacketMeta packet, int index)
    {
        byte[] expected = Payload(index, packet.Size);
        var actual = new byte[packet.Size];
        fixed (byte* pointer = actual)
        {
            if (!ring.TryRead(packet, pointer)) return false;
        }
        return expected.AsSpan().SequenceEqual(actual);
    }

    private static bool WrapIntegrity(bool disk)
    {
        using var ring = new PacketRing(2 * 1024 * 1024, TimeSpan.FromSeconds(600), disk);
        var random = new Random(7);
        var sizes = new Dictionary<long, int>();
        for (int i = 0; i < 20000; i++)
        {
            int size = random.Next(200, 60000);
            long pts = i * Second / 60;
            Add(ring, StreamKind.Video, i, size, pts, i % 5 == 0);
            sizes[pts] = i;
        }

        ClipPlan plan = ring.Plan(long.MaxValue / 2, long.MaxValue - 1)!;
        Console.WriteLine($"  packets={plan.Video.Length} live={ring.LiveBytes} cap={ring.CapacityBytes} disk={ring.IsDiskBacked}");
        if (plan.Video.Length < 20) return false;
        foreach (PacketMeta packet in plan.Video)
            if (!Matches(ring, packet, sizes[packet.PtsUs]))
            {
                Console.WriteLine($"  mismatch at seq {packet.Seq} pts {packet.PtsUs}");
                return false;
            }
        return ring.LiveBytes <= ring.CapacityBytes && ring.IsDiskBacked == disk;
    }

    private static bool PlanKeyframe(bool disk)
    {
        using var ring = new PacketRing(16 * 1024 * 1024, TimeSpan.FromSeconds(30), disk);
        for (int i = 0; i < 1200; i++)
        {
            Add(ring, StreamKind.Video, i, 500, i * Second / 60, i % 60 == 0);
            if (i % 2 == 0) Add(ring, StreamKind.Audio, i + 100000, 100, i * Second / 60, true);
        }

        long end = 1199 * Second / 60;
        ClipPlan plan = ring.Plan(end, 5 * Second)!;
        bool startsOnKey = plan.Video[0].Key;
        bool coversRequest = plan.StartUs <= end - 5 * Second && plan.StartUs > end - 6 * Second - Second / 60;
        bool audioInside = plan.Audio.All(a => a.PtsUs >= plan.StartUs && a.PtsUs <= end) && plan.Audio.Length > 100;
        return startsOnKey && coversRequest && audioInside && plan.Video[^1].PtsUs == end;
    }

    private static bool EvictedRead()
    {
        using var ring = new PacketRing(1024 * 1024, TimeSpan.FromSeconds(600));
        Add(ring, StreamKind.Video, 0, 1000, 0, true);
        ClipPlan plan = ring.Plan(Second, Second)!;
        for (int i = 1; i < 3000; i++) Add(ring, StreamKind.Video, i, 2000, i * Second / 60, false);
        byte* buffer = stackalloc byte[1000];
        return !ring.TryRead(plan.Video[0], buffer);
    }

    private static bool FailedWriteLeavesNothing()
    {
        string directory = Path.Combine(Program.OutputDir, "failed");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "clip.mp4");

        using var ring = new PacketRing(1024 * 1024, TimeSpan.FromSeconds(600));
        for (int i = 0; i < 120; i++) Add(ring, StreamKind.Video, i, 1000, i * Second / 60, i % 60 == 0);
        ClipPlan plan = ring.Plan(2 * Second, 2 * Second)!;
        for (int i = 120; i < 4000; i++) Add(ring, StreamKind.Video, i, 2000, i * Second / 60, false);

        var video = new VideoStreamInfo { Width = 64, Height = 64, Fps = 60, BitRate = 1000, ExtraData = [0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x68, 0x42] };
        try
        {
            ClipWriter.Write(ring, plan, video, null, target, null);
            return false;
        }
        catch (Exception ex) when (ex is IOException or FFmpegException or InvalidDataException)
        {
            Console.WriteLine("  refused: " + ex.Message);
        }
        return Directory.GetFiles(directory).Length == 0;
    }

    private static bool ResizeKeepsOrder()
    {
        using var ring = new PacketRing(8 * 1024 * 1024, TimeSpan.FromSeconds(600));
        for (int i = 0; i < 1000; i++) Add(ring, StreamKind.Video, i, 4000, i * Second / 60, i % 60 == 0);

        ring.Reconfigure(2 * 1024 * 1024, TimeSpan.FromSeconds(600));
        ClipPlan? small = ring.Plan(long.MaxValue / 2, long.MaxValue - 1);
        if (small == null) return false;
        foreach (PacketMeta packet in small.Video)
            if (!Matches(ring, packet, (int)Math.Round(packet.PtsUs * 60.0 / Second))) return false;

        ring.Reconfigure(32 * 1024 * 1024, TimeSpan.FromSeconds(600));
        ClipPlan big = ring.Plan(long.MaxValue / 2, long.MaxValue - 1)!;
        bool ordered = big.Video.Zip(big.Video.Skip(1)).All(p => p.First.PtsUs < p.Second.PtsUs);
        for (int i = 1000; i < 1100; i++) Add(ring, StreamKind.Video, i, 4000, i * Second / 60, i % 60 == 0);
        return ordered && ring.BufferedSeconds > 1;
    }

    private static bool SpanFollowsBitrate()
    {
        using var ring = new PacketRing(256 * 1024 * 1024, TimeSpan.FromSeconds(10));
        for (int i = 0; i < 120000; i++) Add(ring, StreamKind.Video, i % 5000, 8000, i * Second / 60, i % 60 == 0);
        // 8 KB at 60 fps for 10 s is 480 KB/s. Time eviction must keep this far below capacity.
        return ring.LiveBytes < 8 * 1024 * 1024 && ring.BufferedSeconds is > 8 and <= 10.1;
    }
}

using System.Runtime.InteropServices;
using ClipClip.Engine.Core;
using Microsoft.Win32.SafeHandles;

namespace ClipClip.Engine.Media;

public enum StreamKind : byte { Video, Audio }

public readonly record struct PacketMeta(long Seq, long Offset, int Size, long PtsUs, StreamKind Kind, bool Key, int Epoch);

/// <summary>Which packets make up a clip. Taken under the ring lock, so it is a stable view.</summary>
public sealed class ClipPlan
{
    public required PacketMeta[] Video { get; init; }
    public required PacketMeta[] Audio { get; init; }
    public required int Epoch { get; init; }
    public long StartUs => Video[0].PtsUs;
    public long EndUs => Video[^1].PtsUs;
    public double DurationSeconds => (EndUs - StartUs) / 1_000_000.0;
}

/// <summary>
/// The instant replay buffer: encoded packets in one preallocated block, oldest evicted first.
/// Nothing touches disk in the common case, and saving a clip only reads packets back out, so
/// recording never restarts. Long or high bitrate buffers use a delete-on-close mapped temp file
/// through the same pointer interface, so RAM use stays bounded.
/// </summary>
public sealed unsafe class PacketRing : IDisposable
{
    private readonly object _lock = new();
    private byte* _memory;
    private long _capacity;
    private long _retentionUs;
    private BackingKind _backing;
    private nint _mapping;
    private SafeFileHandle? _file;

    private PacketMeta[] _meta = new PacketMeta[8192];
    private long _firstSeq;
    private long _nextSeq;
    private long _writePos;
    private long _liveBytes;
    private long _newestVideoUs = long.MinValue;
    private long _oldestVideoKeyUs = long.MaxValue;

    private enum BackingKind { Memory, File }

    public PacketRing(long capacityBytes, TimeSpan retention, bool forceDisk = false)
    {
        Allocate(capacityBytes, forceDisk);
        _retentionUs = (long)retention.TotalMicroseconds;
    }

    public long CapacityBytes { get { lock (_lock) return _capacity; } }
    public long LiveBytes { get { lock (_lock) return _liveBytes; } }
    public bool IsDiskBacked { get { lock (_lock) return _backing == BackingKind.File; } }
    public long NewestVideoUs { get { lock (_lock) return _newestVideoUs; } }

    /// <summary>Seconds of video currently available, measured from the oldest keyframe.</summary>
    public double BufferedSeconds
    {
        get
        {
            lock (_lock)
            {
                if (_newestVideoUs == long.MinValue || _oldestVideoKeyUs == long.MaxValue) return 0;
                return Math.Max(0, (_newestVideoUs - _oldestVideoKeyUs) / 1_000_000.0);
            }
        }
    }

    public static long DiskThresholdBytes
    {
        get
        {
            long installed = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return Math.Min(2L * 1024 * 1024 * 1024, installed / 4);
        }
    }

    public void Append(StreamKind kind, byte* data, int size, long ptsUs, bool key, int epoch)
    {
        lock (_lock)
        {
            if (size > _capacity / 4) return;

            long horizon = Math.Max(ptsUs, _newestVideoUs) - _retentionUs;
            while (_firstSeq < _nextSeq && Tail().PtsUs < horizon) PopTail();

            long position = _writePos;
            if (ShouldWrap(position, size))
            {
                // The write head is jumping over everything above it, so those packets go first.
                while (_firstSeq < _nextSeq && Tail().Offset >= position) PopTail();
                position = 0;
            }
            while (_firstSeq < _nextSeq && Overlaps(Tail(), position, size)) PopTail();

            Buffer.MemoryCopy(data, _memory + position, _capacity - position, size);

            if (_nextSeq - _firstSeq == _meta.Length) GrowMeta();
            var meta = new PacketMeta(_nextSeq, position, size, ptsUs, kind, key, epoch);
            _meta[_nextSeq % _meta.Length] = meta;
            _nextSeq++;
            _writePos = position + size;
            _liveBytes += size;

            if (kind == StreamKind.Video)
            {
                if (ptsUs > _newestVideoUs) _newestVideoUs = ptsUs;
                if (key && _oldestVideoKeyUs == long.MaxValue) _oldestVideoKeyUs = ptsUs;
            }
        }
    }

    /// <summary>Wrap at the physical end, or early if there is room at the start, so the
    /// touched span tracks the real bitrate rather than the worst case the block was sized for.</summary>
    private bool ShouldWrap(long position, int size)
    {
        if (position + size > _capacity) return true;
        if (_firstSeq == _nextSeq) return false;
        long tailOffset = Tail().Offset;
        if (tailOffset > position) return false;
        long softLimit = Math.Max(32L * 1024 * 1024, _liveBytes + _liveBytes / 4);
        return position > softLimit && tailOffset > size + 1024 * 1024;
    }

    private ref PacketMeta Tail() => ref _meta[_firstSeq % _meta.Length];

    private static bool Overlaps(in PacketMeta packet, long position, int size) =>
        packet.Offset < position + size && position < packet.Offset + packet.Size;

    private void PopTail()
    {
        PacketMeta tail = Tail();
        _firstSeq++;
        _liveBytes -= tail.Size;
        if (tail.Kind == StreamKind.Video && tail.Key)
            RecomputeOldestKey();
    }

    private void RecomputeOldestKey()
    {
        _oldestVideoKeyUs = long.MaxValue;
        for (long seq = _firstSeq; seq < _nextSeq; seq++)
        {
            ref PacketMeta packet = ref _meta[seq % _meta.Length];
            if (packet.Kind == StreamKind.Video && packet.Key)
            {
                _oldestVideoKeyUs = packet.PtsUs;
                return;
            }
        }
    }

    private void GrowMeta()
    {
        var larger = new PacketMeta[_meta.Length * 2];
        for (long seq = _firstSeq; seq < _nextSeq; seq++)
            larger[seq % larger.Length] = _meta[seq % _meta.Length];
        _meta = larger;
    }

    /// <summary>
    /// Selects the packets for the <paramref name="durationUs"/> ending at <paramref name="endUs"/>.
    /// Video must start on a keyframe, so the clip starts at the last keyframe at or before the
    /// requested start and comes out up to one GOP longer instead of shorter. Only the newest
    /// encoder epoch is used, because streams from two encoder instances cannot share one file.
    /// </summary>
    public ClipPlan? Plan(long endUs, long durationUs)
    {
        lock (_lock)
        {
            long wantedStart = endUs - durationUs;
            int epoch = -1;
            for (long seq = _nextSeq - 1; seq >= _firstSeq; seq--)
            {
                ref PacketMeta packet = ref _meta[seq % _meta.Length];
                if (packet.Kind == StreamKind.Video && packet.PtsUs <= endUs)
                {
                    epoch = packet.Epoch;
                    break;
                }
            }
            if (epoch < 0) return null;

            long startSeq = -1;
            long fallbackSeq = -1;
            for (long seq = _firstSeq; seq < _nextSeq; seq++)
            {
                ref PacketMeta packet = ref _meta[seq % _meta.Length];
                if (packet.Kind != StreamKind.Video || !packet.Key || packet.Epoch != epoch) continue;
                if (fallbackSeq < 0) fallbackSeq = seq;
                if (packet.PtsUs <= wantedStart) startSeq = seq;
                else break;
            }
            if (startSeq < 0) startSeq = fallbackSeq;
            if (startSeq < 0) return null;

            long startUs = _meta[startSeq % _meta.Length].PtsUs;
            var video = new List<PacketMeta>(4096);
            var audio = new List<PacketMeta>(2048);
            for (long seq = _firstSeq; seq < _nextSeq; seq++)
            {
                ref PacketMeta packet = ref _meta[seq % _meta.Length];
                if (packet.Kind == StreamKind.Video)
                {
                    if (seq >= startSeq && packet.Epoch == epoch && packet.PtsUs <= endUs) video.Add(packet);
                }
                else if (packet.PtsUs >= startUs && packet.PtsUs <= endUs)
                {
                    audio.Add(packet);
                }
            }
            if (video.Count == 0) return null;
            return new ClipPlan { Video = video.ToArray(), Audio = audio.ToArray(), Epoch = epoch };
        }
    }

    /// <summary>Copies one packet out. False means it was evicted, which the writer treats as failure.</summary>
    public bool TryRead(in PacketMeta packet, byte* destination)
    {
        lock (_lock)
        {
            if (packet.Seq < _firstSeq || packet.Seq >= _nextSeq) return false;
            Buffer.MemoryCopy(_memory + packet.Offset, destination, packet.Size, packet.Size);
            return true;
        }
    }

    /// <summary>Changes retention or capacity, keeping buffered packets that still fit.</summary>
    public void Reconfigure(long capacityBytes, TimeSpan retention)
    {
        lock (_lock)
        {
            _retentionUs = (long)retention.TotalMicroseconds;
            if (capacityBytes == _capacity) return;

            byte* oldMemory = _memory;
            long oldCapacity = _capacity;
            BackingKind oldBacking = _backing;
            nint oldMapping = _mapping;
            SafeFileHandle? oldFile = _file;
            PacketMeta[] oldMeta = _meta;
            long firstSeq = _firstSeq;
            long nextSeq = _nextSeq;

            Allocate(capacityBytes);
            _meta = new PacketMeta[Math.Max(8192, oldMeta.Length)];
            _firstSeq = _nextSeq = _writePos = _liveBytes = 0;
            _oldestVideoKeyUs = long.MaxValue;
            long newestVideo = _newestVideoUs;
            _newestVideoUs = long.MinValue;

            // Keep the newest packets that fit, in their original order.
            long keepFrom = nextSeq;
            long total = 0;
            while (keepFrom > firstSeq)
            {
                ref PacketMeta previous = ref oldMeta[(keepFrom - 1) % oldMeta.Length];
                if (total + previous.Size > capacityBytes * 9 / 10) break;
                total += previous.Size;
                keepFrom--;
            }
            for (long seq = keepFrom; seq < nextSeq; seq++)
            {
                ref PacketMeta packet = ref oldMeta[seq % oldMeta.Length];
                Buffer.MemoryCopy(oldMemory + packet.Offset, _memory + _writePos, _capacity - _writePos, packet.Size);
                _meta[_nextSeq % _meta.Length] = packet with { Seq = _nextSeq, Offset = _writePos };
                _nextSeq++;
                _writePos += packet.Size;
                _liveBytes += packet.Size;
                if (packet.Kind == StreamKind.Video)
                {
                    if (packet.PtsUs > _newestVideoUs) _newestVideoUs = packet.PtsUs;
                    if (packet.Key && _oldestVideoKeyUs == long.MaxValue) _oldestVideoKeyUs = packet.PtsUs;
                }
            }
            if (_newestVideoUs == long.MinValue) _newestVideoUs = newestVideo;

            Free(oldMemory, oldCapacity, oldBacking, oldMapping, oldFile);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _firstSeq = _nextSeq = _writePos = _liveBytes = 0;
            _newestVideoUs = long.MinValue;
            _oldestVideoKeyUs = long.MaxValue;
        }
    }

    private void Allocate(long capacityBytes, bool forceDisk = false)
    {
        if (forceDisk || capacityBytes >= DiskThresholdBytes)
        {
            try
            {
                AllocateFile(capacityBytes);
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Disk-backed buffer unavailable, using RAM: {ex.Message}");
            }
        }

        _memory = (byte*)VirtualAlloc(0, (nuint)capacityBytes, MemCommit | MemReserve, PageReadWrite);
        if (_memory == null)
            throw new OutOfMemoryException($"Could not reserve {capacityBytes / (1024 * 1024)} MB for the replay buffer.");
        _capacity = capacityBytes;
        _backing = BackingKind.Memory;
        _mapping = 0;
        _file = null;
    }

    private void AllocateFile(long capacityBytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ClipClip-buffer-{Environment.ProcessId}-{Guid.NewGuid():N}.bin");
        SafeFileHandle file = CreateFileW(path, GenericRead | GenericWrite, 0, 0, CreateNew,
            FileAttributeTemporary | FileFlagDeleteOnClose, 0);
        if (file.IsInvalid) throw new IOException("CreateFile failed: " + Marshal.GetLastWin32Error());

        nint mapping = CreateFileMappingW(file, 0, PageReadWrite, (uint)(capacityBytes >> 32), (uint)capacityBytes, null);
        if (mapping == 0)
        {
            file.Dispose();
            throw new IOException("CreateFileMapping failed: " + Marshal.GetLastWin32Error());
        }

        void* view = MapViewOfFile(mapping, FileMapAllAccess, 0, 0, (nuint)capacityBytes);
        if (view == null)
        {
            CloseHandle(mapping);
            file.Dispose();
            throw new IOException("MapViewOfFile failed: " + Marshal.GetLastWin32Error());
        }

        _memory = (byte*)view;
        _capacity = capacityBytes;
        _backing = BackingKind.File;
        _mapping = mapping;
        _file = file;
        Log.Info($"Replay buffer is disk backed: {capacityBytes / (1024 * 1024)} MB");
    }

    private static void Free(byte* memory, long capacity, BackingKind backing, nint mapping, SafeFileHandle? file)
    {
        if (memory == null) return;
        if (backing == BackingKind.Memory)
        {
            VirtualFree(memory, 0, MemRelease);
        }
        else
        {
            UnmapViewOfFile(memory);
            CloseHandle(mapping);
            file?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Free(_memory, _capacity, _backing, _mapping, _file);
            _memory = null;
            _capacity = 0;
            _firstSeq = _nextSeq = 0;
        }
    }

    private const uint MemCommit = 0x1000, MemReserve = 0x2000, MemRelease = 0x8000, PageReadWrite = 4;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, CreateNew = 1;
    private const uint FileAttributeTemporary = 0x100, FileFlagDeleteOnClose = 0x04000000, FileMapAllAccess = 0xF001F;

    [DllImport("kernel32", SetLastError = true)]
    private static extern void* VirtualAlloc(nint address, nuint size, uint type, uint protect);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool VirtualFree(void* address, nuint size, uint type);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFileMappingW(SafeFileHandle file, nint security, uint protect, uint maxHigh, uint maxLow, string? name);

    [DllImport("kernel32", SetLastError = true)]
    private static extern void* MapViewOfFile(nint mapping, uint access, uint offsetHigh, uint offsetLow, nuint size);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool UnmapViewOfFile(void* address);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}

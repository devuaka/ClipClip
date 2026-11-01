namespace ClipClip.Engine.Audio;

/// <summary>
/// Stereo samples placed by absolute position on the shared timeline, not by arrival order.
/// Each packet goes where its QPC timestamp says, so an idle output device (no loopback packets)
/// or a mic with a different clock can never pull audio away from video. Small jitter is ignored
/// to avoid clicks; anything past the drift limit snaps back to the timestamp.
/// </summary>
internal sealed class TimelineBuffer
{
    private const int CapacityFrames = 48000 * 4;
    private const int DriftLimitFrames = 960; // 20 ms

    private readonly float[] _samples = new float[CapacityFrames * 2];
    private readonly object _gate = new();
    private long _writePosition = -1;

    public unsafe void Write(float* stereo, int frames, long position)
    {
        lock (_gate)
        {
            if (_writePosition < 0) _writePosition = position;

            long delta = position - _writePosition;
            if (Math.Abs(delta) > DriftLimitFrames)
            {
                if (delta >= CapacityFrames) Array.Clear(_samples);
                else if (delta > 0) ZeroRange(_writePosition, (int)delta);
                _writePosition = position;
            }

            for (int i = 0; i < frames; i++)
            {
                int slot = (int)((_writePosition + i) % CapacityFrames) * 2;
                _samples[slot] = stereo[i * 2];
                _samples[slot + 1] = stereo[i * 2 + 1];
            }
            _writePosition += frames;
        }
    }

    private void ZeroRange(long position, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            int slot = (int)((position + i) % CapacityFrames) * 2;
            _samples[slot] = 0;
            _samples[slot + 1] = 0;
        }
    }

    /// <summary>Reads into <paramref name="destination"/>. Positions with no data read as silence.</summary>
    public void Read(Span<float> destination, long position)
    {
        int frames = destination.Length / 2;
        lock (_gate)
        {
            long oldest = _writePosition - CapacityFrames;
            for (int i = 0; i < frames; i++)
            {
                long current = position + i;
                if (_writePosition < 0 || current >= _writePosition || current < oldest)
                {
                    destination[i * 2] = 0;
                    destination[i * 2 + 1] = 0;
                }
                else
                {
                    int slot = (int)(current % CapacityFrames) * 2;
                    destination[i * 2] = _samples[slot];
                    destination[i * 2 + 1] = _samples[slot + 1];
                }
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _writePosition = -1;
            Array.Clear(_samples);
        }
    }
}

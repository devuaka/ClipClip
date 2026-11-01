using ClipClip.Engine.Voice;
using NAudio.Wave;

namespace ClipClip.Engine.Audio;

public sealed record MicTestResult(float Peak, VoiceHit? Phrase);

/// <summary>Records a few seconds from the mic, checks the phrase with the real detector, then
/// plays the recording back. Nothing is written to disk.</summary>
public sealed class MicTest
{
    private readonly MicHub _mic;
    private readonly List<float> _samples = new(48000 * 2 * 4);
    private readonly AudioSink _sink;

    public MicTest(MicHub mic)
    {
        _mic = mic;
        unsafe { _sink = OnSamples; }
    }

    public async Task<MicTestResult> RunAsync(int seconds, string phrase, double sensitivity, bool playback, CancellationToken token)
    {
        _mic.Subscribe(_sink);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), token);
        }
        finally
        {
            _mic.Unsubscribe(_sink);
        }

        float[] recording;
        lock (_samples) recording = _samples.ToArray();
        if (recording.Length == 0) return new MicTestResult(0, null);

        float peak = recording.Max(Math.Abs);
        VoiceHit? hit = await Task.Run(() => Detect(recording, phrase, sensitivity), token);
        if (playback) await Task.Run(() => Play(recording, token), token);
        return new MicTestResult(peak, hit);
    }

    private static VoiceHit? Detect(float[] stereo, string phrase, double sensitivity)
    {
        const int decimation = 3;
        var mono = new short[stereo.Length / 2 / decimation];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (int j = 0; j < decimation; j++) sum += stereo[(i * decimation + j) * 2];
            mono[i] = (short)Math.Clamp(sum / decimation * 32767f, -32768f, 32767f);
        }
        return VoiceTrigger.Recognize(phrase, mono, sensitivity).OrderByDescending(h => h.Accepted).FirstOrDefault();
    }

    private static void Play(float[] stereo, CancellationToken token)
    {
        byte[] bytes = new byte[stereo.Length * 4];
        Buffer.BlockCopy(stereo, 0, bytes, 0, bytes.Length);
        var provider = new RawSourceWaveStream(new MemoryStream(bytes), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        using var output = new WasapiOut();
        output.Init(provider);
        output.Play();
        while (output.PlaybackState == PlaybackState.Playing && !token.IsCancellationRequested) Thread.Sleep(30);
        output.Stop();
    }

    private unsafe void OnSamples(float* stereo, int frames, long timeUs)
    {
        lock (_samples)
        {
            for (int i = 0; i < frames * 2; i++) _samples.Add(stereo[i]);
        }
    }
}

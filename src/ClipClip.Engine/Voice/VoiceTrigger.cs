using System.Threading.Channels;
using ClipClip.Engine.Audio;
using ClipClip.Engine.Core;
using Vosk;

namespace ClipClip.Engine.Voice;

public enum VoiceState { Off, Loading, Listening, MicUnavailable, Failed }

/// <summary>
/// Runs the phrase spotter on live microphone audio. All recognition is offline with the bundled
/// Vosk model. Audio stays in this process and is discarded once decoded.
/// </summary>
public sealed unsafe class VoiceTrigger : IDisposable
{
    private const int Decimation = 3; // 48 kHz capture to 16 kHz

    private static readonly object ModelGate = new();
    private static Model? _model;

    private readonly MicHub _mic;
    private readonly AudioSink _sink;
    private readonly short[] _monoScratch = new short[8192];
    private Channel<byte[]>? _queue;
    private Task? _worker;
    private CancellationTokenSource? _cancel;
    private PhraseSpotter? _spotter;
    private string _phrase;
    private double _sensitivity;
    private long _lastTriggerUs;
    private float _carrySum;
    private int _carryCount;

    public VoiceTrigger(MicHub mic, string phrase, double sensitivity)
    {
        _mic = mic;
        _phrase = Normalize(phrase);
        _sensitivity = sensitivity;
        _sink = OnSamples;
    }

    public VoiceState State { get; private set; } = VoiceState.Off;
    public string? FailureReason { get; private set; }

    /// <summary>A confident match. The main output.</summary>
    public event Action<VoiceHit>? Triggered;

    /// <summary>Every candidate, including rejected ones, for the mic test UI.</summary>
    public event Action<VoiceHit>? Heard;

    public static string Normalize(string phrase) =>
        string.Join(' ', new string(phrase.ToLowerInvariant().Where(c => char.IsLetter(c) || c == ' ' || c == '\'').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The model only knows its fixed vocabulary, so a phrase is checked word by word.</summary>
    public static IReadOnlyList<string> UnknownWords(string phrase)
    {
        Model model = LoadModel();
        return Normalize(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => model.FindWord(word) < 0).ToList();
    }

    public void Configure(string phrase, double sensitivity)
    {
        string normalized = Normalize(phrase);
        bool restart = normalized != _phrase;
        _phrase = normalized;
        _sensitivity = sensitivity;
        if (_spotter is { } spotter) spotter.Sensitivity = sensitivity;
        if (restart && _worker != null)
        {
            Stop();
            Start();
        }
    }

    public void Start()
    {
        if (_worker != null) return;
        State = VoiceState.Loading;
        FailureReason = null;
        _cancel = new CancellationTokenSource();
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        CancellationToken token = _cancel.Token;
        _worker = Task.Factory.StartNew(() => Run(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Stop()
    {
        _mic.Unsubscribe(_sink);
        _cancel?.Cancel();
        _queue?.Writer.TryComplete();
        try { _worker?.Wait(3000); }
        catch (AggregateException) { /* Cancellation. */ }
        _worker = null;
        _cancel?.Dispose();
        _cancel = null;
        State = VoiceState.Off;
    }

    private static Model LoadModel()
    {
        lock (ModelGate)
        {
            if (_model != null) return _model;
            string path = AppPaths.VoiceModelDir;
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Voice model is missing: " + path);
            Vosk.Vosk.SetLogLevel(-1);
            _model = new Model(path);
            return _model;
        }
    }

    private void Run(CancellationToken token)
    {
        try
        {
            using var spotter = new PhraseSpotter(LoadModel(), _phrase) { Sensitivity = _sensitivity };
            _spotter = spotter;
            _mic.Subscribe(_sink);
            State = VoiceState.Listening;
            Log.Info($"Voice trigger listening for \"{_phrase}\"");

            var hits = new List<VoiceHit>(2);
            ChannelReader<byte[]> reader = _queue!.Reader;
            while (!token.IsCancellationRequested)
            {
                if (!reader.TryRead(out byte[]? chunk))
                {
                    State = _mic.State == AudioSourceState.Disconnected ? VoiceState.MicUnavailable : VoiceState.Listening;
                    if (!reader.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult()) break;
                    continue;
                }

                spotter.Accept(chunk, chunk.Length, hits);
                foreach (VoiceHit hit in hits) Report(hit);
                hits.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            State = VoiceState.Failed;
            FailureReason = ex.Message;
            Log.Error("Voice trigger failed", ex);
        }
        finally
        {
            _spotter = null;
        }
    }

    private void Report(VoiceHit hit)
    {
        Heard?.Invoke(hit);
        Log.Info($"Voice candidate \"{hit.Text}\" confidence {hit.Confidence:F2} accepted={hit.Accepted}");
        if (!hit.Accepted) return;

        long now = Clock.NowUs();
        if (now - _lastTriggerUs < 1_500_000) return;
        _lastTriggerUs = now;
        Triggered?.Invoke(hit);
    }

    /// <summary>Runs recorded audio through the same detector as live speech.</summary>
    public static IReadOnlyList<VoiceHit> Recognize(string phrase, short[] mono16k, double sensitivity)
    {
        var hits = new List<VoiceHit>();
        using var spotter = new PhraseSpotter(LoadModel(), Normalize(phrase)) { Sensitivity = sensitivity };
        byte[] bytes = new byte[mono16k.Length * 2];
        Buffer.BlockCopy(mono16k, 0, bytes, 0, bytes.Length);
        const int chunk = 3200;
        for (int offset = 0; offset < bytes.Length; offset += chunk)
        {
            byte[] part = bytes[offset..Math.Min(bytes.Length, offset + chunk)];
            spotter.Accept(part, part.Length, hits);
        }
        spotter.Finish(hits);
        return hits;
    }

    private void OnSamples(float* stereo, int frames, long timeUs)
    {
        Channel<byte[]>? queue = _queue;
        if (queue == null) return;

        // Mono, then a box filter across each 3 samples down to 16 kHz.
        int written = 0;
        for (int i = 0; i < frames && written < _monoScratch.Length; i++)
        {
            _carrySum += (stereo[i * 2] + stereo[i * 2 + 1]) * 0.5f;
            if (++_carryCount < Decimation) continue;
            float mono = _carrySum / Decimation;
            _carrySum = 0;
            _carryCount = 0;
            _monoScratch[written++] = (short)Math.Clamp(mono * 32767f, -32768f, 32767f);
        }
        if (written == 0) return;

        byte[] chunk = new byte[written * 2];
        Buffer.BlockCopy(_monoScratch, 0, chunk, 0, chunk.Length);
        queue.Writer.TryWrite(chunk);
    }

    public void Dispose() => Stop();
}

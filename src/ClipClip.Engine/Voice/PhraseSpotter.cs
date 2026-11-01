using System.Text.Json;
using Vosk;

namespace ClipClip.Engine.Voice;

public sealed record VoiceHit(string Text, double Confidence, bool Accepted);

/// <summary>
/// Two-stage phrase detection. A grammar holding only the phrase runs all the time because it is
/// very cheap, but it reports full confidence for anything vaguely similar. So each candidate is
/// re-decoded from the last few seconds of audio with the full vocabulary, which can actually tell
/// "clip that" from "flip the hat" and gives a real confidence for the sensitivity setting.
/// </summary>
internal sealed class PhraseSpotter : IDisposable
{
    public const int SampleRate = 16000;
    private const int HistoryBytes = SampleRate * 2 * 4;

    private readonly string _phrase;
    private readonly string[] _words;
    private readonly VoskRecognizer _grammar;
    private readonly VoskRecognizer _verifier;
    private readonly byte[] _history = new byte[HistoryBytes];
    private long _historyWritten;

    public PhraseSpotter(Model model, string phrase)
    {
        _phrase = phrase;
        _words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _grammar = new VoskRecognizer(model, SampleRate, JsonSerializer.Serialize(new[] { phrase, "[unk]" }));
        _grammar.SetWords(true);
        _verifier = new VoskRecognizer(model, SampleRate);
        _verifier.SetWords(true);
    }

    public double Sensitivity { get; set; } = 0.5;

    /// <summary>Confidence the full decode must reach. At maximum sensitivity the check is
    /// skipped, for voices or accents the full vocabulary struggles with.</summary>
    public static double ThresholdFor(double sensitivity) => 0.9 - 0.7 * Math.Clamp(sensitivity, 0, 1);

    public void Accept(byte[] chunk, int length, List<VoiceHit> hits)
    {
        Remember(chunk, length);
        if (_grammar.AcceptWaveform(chunk, length)) Check(_grammar.Result(), hits);
    }

    public void Finish(List<VoiceHit> hits) => Check(_grammar.FinalResult(), hits);

    private void Remember(byte[] chunk, int length)
    {
        int offset = (int)(_historyWritten % HistoryBytes);
        int first = Math.Min(length, HistoryBytes - offset);
        Buffer.BlockCopy(chunk, 0, _history, offset, first);
        if (first < length) Buffer.BlockCopy(chunk, first, _history, 0, length - first);
        _historyWritten += length;
    }

    private void Check(string json, List<VoiceHit> hits)
    {
        if (!ContainsPhrase(json, out _)) return;

        if (Sensitivity >= 0.999)
        {
            hits.Add(new VoiceHit(_phrase, 1, true));
            return;
        }

        // Full decode of everything heard recently, in time order.
        byte[] audio = RecentAudio();
        _verifier.Reset();
        _verifier.AcceptWaveform(audio, audio.Length);
        bool heard = ContainsPhrase(_verifier.FinalResult(), out double confidence);
        hits.Add(new VoiceHit(_phrase, heard ? confidence : 0, heard && confidence >= ThresholdFor(Sensitivity)));
    }

    private byte[] RecentAudio()
    {
        int available = (int)Math.Min(_historyWritten, HistoryBytes);
        var result = new byte[available];
        int start = (int)((_historyWritten - available) % HistoryBytes);
        int first = Math.Min(available, HistoryBytes - start);
        Buffer.BlockCopy(_history, start, result, 0, first);
        if (first < available) Buffer.BlockCopy(_history, 0, result, first, available - first);
        return result;
    }

    private bool ContainsPhrase(string json, out double confidence)
    {
        confidence = 0;
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out JsonElement words)) return false;

        var heard = new List<(string Word, double Confidence)>();
        foreach (JsonElement word in words.EnumerateArray())
            heard.Add((word.GetProperty("word").GetString() ?? "", word.GetProperty("conf").GetDouble()));

        bool found = false;
        for (int start = 0; start + _words.Length <= heard.Count; start++)
        {
            bool match = true;
            double total = 0;
            for (int i = 0; i < _words.Length; i++)
            {
                match &= heard[start + i].Word == _words[i];
                total += heard[start + i].Confidence;
            }
            if (!match) continue;
            found = true;
            confidence = Math.Max(confidence, total / _words.Length);
        }
        return found;
    }

    public void Dispose()
    {
        _grammar.Dispose();
        _verifier.Dispose();
    }
}

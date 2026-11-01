using System;
using System.IO;
using System.Media;
using ClipClip.Engine;
using ClipClip.Engine.Core;
using ClipClip.Views;

namespace ClipClip.Services;

/// <summary>Clip feedback. Our own overlay is used because Windows toasts are suppressed during
/// games. Failures always show, whatever the notification setting, since a lost clip matters.</summary>
public sealed class Toasts : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SoundPlayer _chime = new(BuildChime());
    private ToastWindow? _current;

    public Toasts(AppSettings settings)
    {
        _settings = settings;
        _chime.Load();
    }

    public void Show(ClipResult result)
    {
        if (result.Success && _settings.ClipSound) _chime.Play();
        if (result.Success && !_settings.Notifications) return;

        _current?.Close();
        string title = result.Success ? "Clip saved" : "Clip not saved";
        string detail = result.Success
            ? $"{Math.Round(result.Seconds)}s{(result.Game != null ? "  ·  " + result.Game : "")}"
            : result.Error ?? "Something went wrong.";
        _current = new ToastWindow(title, detail, result.Success);
        _current.Closed += (_, _) => _current = null;
        _current.Show();
    }

    /// <summary>Two rising tones, built here so there is no audio asset to ship.</summary>
    private static MemoryStream BuildChime()
    {
        const int rate = 44100;
        double[] tones = [880, 1318.5];
        int noteSamples = rate * 9 / 100;
        int total = noteSamples * tones.Length + rate / 10;
        var pcm = new short[total];
        for (int n = 0; n < tones.Length; n++)
        {
            int start = n * noteSamples;
            for (int i = 0; i < noteSamples + rate / 10 && start + i < total; i++)
            {
                double t = (double)i / rate;
                double envelope = Math.Min(1, t * 400) * Math.Exp(-t * 18);
                pcm[start + i] += (short)(Math.Sin(2 * Math.PI * tones[n] * t) * envelope * 5200);
            }
        }

        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true))
        {
            int bytes = pcm.Length * 2;
            writer.Write("RIFF"u8);
            writer.Write(36 + bytes);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(bytes);
            foreach (short sample in pcm) writer.Write(sample);
        }
        stream.Position = 0;
        return stream;
    }

    public void Dispose() => _chime.Dispose();
}

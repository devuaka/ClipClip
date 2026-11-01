using ClipClip.Engine.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ClipClip.Engine.Audio;

public sealed record AudioDeviceInfo(string Id, string Name);

public static class AudioDevices
{
    public static IReadOnlyList<AudioDeviceInfo> Microphones() => List(DataFlow.Capture);
    public static IReadOnlyList<AudioDeviceInfo> Outputs() => List(DataFlow.Render);

    private static IReadOnlyList<AudioDeviceInfo> List(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device) result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Audio device list failed: " + ex.Message);
        }
        return result;
    }

    private static readonly object NameGate = new();
    private static (string? Id, string Name, long ExpiresUs) _cachedName;

    /// <summary>Display name of a mic, cached briefly because the UI asks several times a second.</summary>
    public static string MicrophoneName(string? id)
    {
        lock (NameGate)
        {
            long now = Clock.NowUs();
            if (_cachedName.Name != null && _cachedName.Id == id && now < _cachedName.ExpiresUs) return _cachedName.Name;

            string name;
            try
            {
                using MMDevice? device = Resolve(id, DataFlow.Capture);
                name = device?.FriendlyName ?? "Not connected";
            }
            catch (Exception)
            {
                name = "Not available";
            }
            _cachedName = (id, name, now + 5_000_000);
            return name;
        }
    }

    /// <summary>Null id means the Windows default. A saved id that has gone away returns null,
    /// so a removed mic shows as disconnected rather than silently using a different one.</summary>
    internal static MMDevice? Resolve(string? id, DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (string.IsNullOrEmpty(id))
        {
            return enumerator.HasDefaultAudioEndpoint(flow, Role.Console)
                ? enumerator.GetDefaultAudioEndpoint(flow, Role.Console)
                : null;
        }

        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            if (device.ID == id) return device;
            device.Dispose();
        }
        return null;
    }
}

/// <summary>Raises <see cref="Changed"/> when devices appear, vanish or the default moves.</summary>
public sealed class AudioDeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public AudioDeviceWatcher() => _enumerator.RegisterEndpointNotificationCallback(this);

    public event Action? Changed;

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Changed?.Invoke();
    public void OnDeviceAdded(string pwstrDeviceId) => Changed?.Invoke();
    public void OnDeviceRemoved(string deviceId) => Changed?.Invoke();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (role == Role.Console) Changed?.Invoke();
    }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(this);
        _enumerator.Dispose();
    }
}

/// <summary>
/// The microphone is opened only while something needs it (mix, voice trigger or mic test), and
/// is shared by all of them. When nothing subscribes, the device is closed.
/// </summary>
public sealed unsafe class MicHub : IDisposable
{
    private readonly object _gate = new();
    private volatile AudioSink[] _subscribers = [];
    private readonly WasapiSource _source;
    private string? _deviceId;
    private float _peak;

    public MicHub(string? deviceId)
    {
        _deviceId = deviceId;
        _source = new WasapiSource("Mic", false, () => AudioDevices.Resolve(_deviceId, DataFlow.Capture), Dispatch);
    }

    public AudioSourceState State => _source.State;
    public string? DeviceName => _source.DeviceName;

    /// <summary>Peak level since the last read, 0 to 1, for meters.</summary>
    public float TakePeak()
    {
        float peak = _peak;
        _peak = 0;
        return peak;
    }

    public void Subscribe(AudioSink sink)
    {
        lock (_gate)
        {
            if (_subscribers.Contains(sink)) return;
            _subscribers = [.. _subscribers, sink];
            if (_subscribers.Length == 1) _source.Start();
        }
    }

    public void Unsubscribe(AudioSink sink)
    {
        lock (_gate)
        {
            if (!_subscribers.Contains(sink)) return;
            _subscribers = _subscribers.Where(s => s != sink).ToArray();
            // The capture thread reads the array without this lock, so joining it here is safe.
            if (_subscribers.Length == 0) _source.Stop();
        }
    }

    public void SetDevice(string? deviceId)
    {
        if (_deviceId == deviceId) return;
        _deviceId = deviceId;
        _source.Restart();
    }

    public void Refresh() => _source.Restart();

    private void Dispatch(float* stereo, int frames, long timeUs)
    {
        float peak = _peak;
        for (int i = 0; i < frames * 2; i++)
        {
            float value = Math.Abs(stereo[i]);
            if (value > peak) peak = value;
        }
        _peak = peak;

        foreach (AudioSink subscriber in _subscribers) subscriber(stereo, frames, timeUs);
    }

    public void Dispose() => _source.Stop();
}

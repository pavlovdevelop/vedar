using Vedar.Audio.Dsp;
using Vedar.Core.Abstractions;

namespace Vedar.Audio.Testing;

/// <summary>
/// Plays a WAV file (or raw samples) as if it came from a microphone. Used by automated tests and the
/// benchmark tool; frames are delivered in 30 ms blocks, optionally in real time.
/// </summary>
public sealed class SimulatedAudioInput : IAudioInput
{
    private readonly List<AudioDeviceInfo> _devices;

    public SimulatedAudioInput(params AudioDeviceInfo[] devices)
    {
        _devices = devices.Length > 0 ? devices.ToList() : [new AudioDeviceInfo("sim-default", "Simulated microphone", true)];
    }

    public string? CurrentDeviceId { get; private set; }
    public bool IsCapturing { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public bool FailOnStart { get; set; }
    public List<string?> StartedDevices { get; } = [];

    public event EventHandler<AudioFrameEventArgs>? FrameAvailable;
    public event EventHandler<AudioDeviceErrorEventArgs>? DeviceError;

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => _devices;

    public void AddDevice(AudioDeviceInfo device) => _devices.Add(device);

    public void RemoveDevice(string id) => _devices.RemoveAll(d => d.Id == id);

    public Task StartAsync(string? deviceId, CancellationToken cancellationToken)
    {
        if (FailOnStart)
        {
            throw new AudioDeviceException("Simulated failure.");
        }

        var device = deviceId is null ? _devices.FirstOrDefault(d => d.IsDefault) ?? _devices.FirstOrDefault() : _devices.FirstOrDefault(d => d.Id == deviceId) ?? _devices.FirstOrDefault(d => d.IsDefault);
        if (device is null)
        {
            throw new AudioDeviceException("No microphone.");
        }

        CurrentDeviceId = device.Id;
        IsCapturing = true;
        StartCount++;
        StartedDevices.Add(device.Id);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (IsCapturing)
        {
            StopCount++;
        }

        IsCapturing = false;
        CurrentDeviceId = null;
        return Task.CompletedTask;
    }

    /// <summary>Pushes samples (16 kHz mono) through FrameAvailable in 30 ms frames.</summary>
    public async Task FeedAsync(float[] samples16k, bool realTime = false, CancellationToken cancellationToken = default)
    {
        const int frame = 480;
        for (int i = 0; i < samples16k.Length; i += frame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCapturing)
            {
                return;
            }

            var len = Math.Min(frame, samples16k.Length - i);
            FrameAvailable?.Invoke(this, new AudioFrameEventArgs(samples16k.AsSpan(i, len).ToArray()));
            if (realTime)
            {
                await Task.Delay(30, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task FeedWavAsync(string path, bool realTime = false, CancellationToken cancellationToken = default)
    {
        var wav = WavFile.ReadMono(path);
        var samples = StreamingResampler.Resample(wav.Samples, wav.SampleRate, AudioFrameEventArgs.SampleRate);
        return FeedAsync(samples, realTime, cancellationToken);
    }

    public void SimulateDeviceLoss() => DeviceError?.Invoke(this, new AudioDeviceErrorEventArgs("Simulated device loss.", null));

    public void Dispose() { }
}

/// <summary>Audio output that records what would be played. Playback takes real time unless <see cref="Instant"/> is set.</summary>
public sealed class RecordingAudioOutput : IAudioOutput
{
    private readonly object _gate = new();

    public List<(float[] Samples, int SampleRate)> Played { get; } = [];
    public int CancelledCount { get; private set; }
    public bool Instant { get; set; } = true;
    public double SpeedFactor { get; set; } = 1.0;
    public string? DeviceId { get; set; }
    public float Volume { get; set; } = 1f;
    public bool IsPlaying { get; private set; }

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => [new AudioDeviceInfo("rec", "Recording output", true)];

    public async Task PlayAsync(float[] samples, int sampleRate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Played.Add((samples, sampleRate));
        }

        if (Instant)
        {
            return;
        }

        IsPlaying = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(samples.Length / (double)sampleRate / SpeedFactor), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CancelledCount++;
            throw;
        }
        finally
        {
            IsPlaying = false;
        }
    }

    public double TotalSecondsPlayed
    {
        get { lock (_gate) { return Played.Sum(p => p.Samples.Length / (double)p.SampleRate); } }
    }

    public void Dispose() { }
}

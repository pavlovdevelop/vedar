namespace Vedar.Core.Abstractions;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>A block of captured audio, always 16 kHz mono float in the range [-1, 1].</summary>
public sealed class AudioFrameEventArgs(float[] samples) : EventArgs
{
    public const int SampleRate = 16000;
    public float[] Samples { get; } = samples;
}

public sealed class AudioDeviceErrorEventArgs(string message, Exception? exception) : EventArgs
{
    public string Message { get; } = message;
    public Exception? Exception { get; } = exception;
}

public class AudioDeviceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Microphone capture. Implementations deliver 16 kHz mono float frames.</summary>
public interface IAudioInput : IDisposable
{
    IReadOnlyList<AudioDeviceInfo> GetDevices();
    string? CurrentDeviceId { get; }
    bool IsCapturing { get; }

    /// <summary>Starts capturing from the given device (null = system default).</summary>
    /// <exception cref="AudioDeviceException">No device or the device cannot be opened.</exception>
    Task StartAsync(string? deviceId, CancellationToken cancellationToken);
    Task StopAsync();

    event EventHandler<AudioFrameEventArgs>? FrameAvailable;
    /// <summary>Raised when the device disappears or fails while capturing.</summary>
    event EventHandler<AudioDeviceErrorEventArgs>? DeviceError;
}

/// <summary>Audio playback with immediate stop.</summary>
public interface IAudioOutput : IDisposable
{
    IReadOnlyList<AudioDeviceInfo> GetDevices();
    /// <summary>Output device id, null = system default. Takes effect on the next playback.</summary>
    string? DeviceId { get; set; }
    /// <summary>Software volume 0..1.</summary>
    float Volume { get; set; }

    /// <summary>
    /// While true, VEDAR's voice is played about 15 dB quieter — it gives way when the user starts to speak.
    /// Takes effect within ~0.1 s, also in the middle of a sound.
    /// </summary>
    bool Ducked { get; set; }

    /// <summary>How much quieter (dB) the voice is while <see cref="Ducked"/>.</summary>
    public const double DuckDb = 15;

    /// <summary>
    /// Plays mono float samples. Completes when playback ends. Cancelling the token stops playback immediately
    /// and completes the task with <see cref="OperationCanceledException"/>.
    /// </summary>
    Task PlayAsync(float[] samples, int sampleRate, CancellationToken cancellationToken);
}

using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Vedar.Audio.Dsp;
using Vedar.Core.Abstractions;

namespace Vedar.Audio.Windows;

/// <summary>
/// Microphone capture with WASAPI (shared mode). Converts the device format to 16 kHz mono float.
/// Follows the Windows default microphone when no device is selected, and reports device loss.
/// Audio is only kept in memory; nothing is written to disk.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioInput : IAudioInput
{
    private readonly ILogger<WasapiAudioInput> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly DefaultDeviceWatcher _watcher;
    private WasapiCapture? _capture;
    private StreamingResampler? _resampler;
    private WaveFormat? _format;
    private bool _followDefault;
    private bool _stopping;

    public WasapiAudioInput(ILogger<WasapiAudioInput> logger)
    {
        _logger = logger;
        _watcher = new DefaultDeviceWatcher(this);
        try
        {
            _enumerator.RegisterEndpointNotificationCallback(_watcher);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not register for device notifications.");
        }
    }

    public string? CurrentDeviceId { get; private set; }
    public bool IsCapturing => _capture is not null;

    public event EventHandler<AudioFrameEventArgs>? FrameAvailable;
    public event EventHandler<AudioDeviceErrorEventArgs>? DeviceError;

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        string? defaultId = null;
        try
        {
            defaultId = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID;
        }
        catch (Exception)
        {
            // No default device.
        }

        return _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, d.ID == defaultId))
            .ToList();
    }

    public Task StartAsync(string? deviceId, CancellationToken cancellationToken) =>
        // COM objects are created on a thread-pool (MTA) thread, never on the WPF UI thread.
        Task.Run(async () =>
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StopCore();
                var device = ResolveDevice(deviceId);
                _followDefault = deviceId is null || device.ID != deviceId;
                var capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 60);
                _format = capture.WaveFormat;
                _resampler = new StreamingResampler(_format.SampleRate, AudioFrameEventArgs.SampleRate);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                _stopping = false;
                capture.StartRecording();
                _capture = capture;
                CurrentDeviceId = device.ID;
                _logger.LogInformation("Microphone started: {Name} ({Rate} Hz, {Channels} ch, {Encoding}).", device.FriendlyName, _format.SampleRate, _format.Channels, _format.Encoding);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not AudioDeviceException)
            {
                StopCore();
                throw new AudioDeviceException("Cannot open the microphone.", ex);
            }
            finally
            {
                _gate.Release();
            }
        }, cancellationToken);

    private MMDevice ResolveDevice(string? deviceId)
    {
        if (deviceId is not null)
        {
            try
            {
                var d = _enumerator.GetDevice(deviceId);
                if (d.State == DeviceState.Active)
                {
                    return d;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Selected microphone not found; using the default.");
            }
        }

        try
        {
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }
        catch (Exception ex)
        {
            throw new AudioDeviceException("No microphone is connected.", ex);
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(StopCore).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StopCore()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null)
        {
            return;
        }

        _stopping = true;
        capture.DataAvailable -= OnDataAvailable;
        try
        {
            capture.StopRecording();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StopRecording failed.");
        }

        capture.RecordingStopped -= OnRecordingStopped;
        capture.Dispose();
        CurrentDeviceId = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _format;
        var resampler = _resampler;
        if (format is null || resampler is null || e.BytesRecorded == 0)
        {
            return;
        }

        float[] interleaved;
        var bytes = e.Buffer.AsSpan(0, e.BytesRecorded);
        if (format.Encoding == WaveFormatEncoding.IeeeFloat || (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT))
        {
            interleaved = new float[bytes.Length / 4];
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).CopyTo(interleaved);
        }
        else if (format.BitsPerSample == 16)
        {
            interleaved = AudioMath.Pcm16ToFloat(bytes);
        }
        else if (format.BitsPerSample == 32)
        {
            interleaved = new float[bytes.Length / 4];
            var ints = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes);
            for (int i = 0; i < interleaved.Length; i++)
            {
                interleaved[i] = ints[i] / 2147483648f;
            }
        }
        else
        {
            return;
        }

        var mono = AudioMath.DownmixToMono(interleaved, format.Channels);
        var frame = resampler.Process(mono);
        if (frame.Length > 0)
        {
            FrameAvailable?.Invoke(this, new AudioFrameEventArgs(frame));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        _logger.LogWarning(e.Exception, "Microphone stopped unexpectedly.");
        _capture = null;
        DeviceError?.Invoke(this, new AudioDeviceErrorEventArgs("The microphone stopped working.", e.Exception));
    }

    private void OnDefaultCaptureDeviceChanged()
    {
        if (!_followDefault || _capture is null)
        {
            return;
        }

        _logger.LogInformation("Default microphone changed; switching.");
        _ = Task.Run(async () =>
        {
            try
            {
                await StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DeviceError?.Invoke(this, new AudioDeviceErrorEventArgs("Cannot switch to the new default microphone.", ex));
            }
        });
    }

    public void Dispose()
    {
        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(_watcher);
        }
        catch
        {
            // Ignore.
        }

        StopCore();
        _enumerator.Dispose();
        _gate.Dispose();
    }

    private sealed class DefaultDeviceWatcher(WasapiAudioInput owner) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Capture && role == Role.Console)
            {
                owner.OnDefaultCaptureDeviceChanged();
            }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}

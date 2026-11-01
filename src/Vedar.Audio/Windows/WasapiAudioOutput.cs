using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Vedar.Audio.Dsp;
using Vedar.Core.Abstractions;

namespace Vedar.Audio.Windows;

/// <summary>
/// Plays synthesized speech through WASAPI shared mode. Samples are resampled to the device mix format,
/// so no Windows format conversion is involved. Cancelling stops playback within one buffer (~50 ms).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioOutput : IAudioOutput
{
    private readonly ILogger<WasapiAudioOutput> _logger;
    private readonly SemaphoreSlim _playLock = new(1, 1);
    private readonly MMDeviceEnumerator _enumerator = new();
    private float _volume = 0.9f;

    public WasapiAudioOutput(ILogger<WasapiAudioOutput> logger) => _logger = logger;

    public string? DeviceId { get; set; }

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        string? defaultId = null;
        try
        {
            defaultId = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID;
        }
        catch (Exception)
        {
            // No default device.
        }

        return _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, d.ID == defaultId))
            .ToList();
    }

    public async Task PlayAsync(float[] samples, int sampleRate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _playLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => PlayCoreAsync(samples, sampleRate, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _playLock.Release();
        }
    }

    private async Task PlayCoreAsync(float[] samples, int sampleRate, CancellationToken ct)
    {
        var device = ResolveDevice();
        var mix = device.AudioClient.MixFormat;
        int rate = mix.SampleRate, channels = mix.Channels;

        // 30 ms lead-in avoids clipping the first syllable on devices that wake up slowly.
        var withLead = new float[samples.Length + sampleRate * 30 / 1000];
        samples.CopyTo(withLead, withLead.Length - samples.Length);
        AudioMath.ApplyGain(withLead, _volume);
        var resampled = StreamingResampler.Resample(withLead, sampleRate, rate);
        var interleaved = new float[resampled.Length * channels];
        for (int i = 0; i < resampled.Length; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                interleaved[i * channels + c] = resampled[i];
            }
        }

        var provider = new FloatArrayWaveProvider(interleaved, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
        using var player = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 60);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                done.TrySetException(new AudioDeviceException("Audio playback failed.", e.Exception));
            }
            else
            {
                done.TrySetResult();
            }
        };
        player.Init(provider);
        await using (ct.Register(() =>
        {
            try
            {
                player.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Stop failed.");
            }
        }))
        {
            player.Play();
            await done.Task.ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    private MMDevice ResolveDevice()
    {
        if (DeviceId is not null)
        {
            try
            {
                var d = _enumerator.GetDevice(DeviceId);
                if (d.State == DeviceState.Active)
                {
                    return d;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Selected output device not found; using the default.");
            }
        }

        try
        {
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        }
        catch (Exception ex)
        {
            throw new AudioDeviceException("No audio output device is available.", ex);
        }
    }

    public void Dispose()
    {
        _enumerator.Dispose();
        _playLock.Dispose();
    }

    /// <summary>Feeds a float buffer to NAudio; returns 0 at the end so playback stops by itself.</summary>
    private sealed class FloatArrayWaveProvider(float[] data, WaveFormat format) : IWaveProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = format;

        public int Read(byte[] buffer, int offset, int count)
        {
            int floats = Math.Min(count / 4, data.Length - _position);
            if (floats <= 0)
            {
                return 0;
            }

            Buffer.BlockCopy(data, _position * 4, buffer, offset, floats * 4);
            _position += floats;
            return floats * 4;
        }
    }
}

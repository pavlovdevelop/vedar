using Microsoft.Extensions.Logging;
using Vedar.Audio.Dsp;
using Vedar.Core.Abstractions;

namespace Vedar.Speech.Vad;

/// <summary>
/// Turns the microphone stream into utterances. Audio stays in memory only and is discarded after
/// recognition; nothing is recorded to disk or sent anywhere.
/// </summary>
public sealed class VoiceListener : IVoiceListener
{
    private const int PreRollFrames = 10; // 300 ms before speech start
    private readonly IAudioInput _input;
    private readonly ISettingsStore _settings;
    private readonly ILogger<VoiceListener> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _modeLock = new(1, 1);
    private readonly EnergyVoiceActivityDetector _vad = new();
    private readonly Queue<float[]> _preRoll = new();
    private readonly List<float> _utterance = [];
    private HighPassFilter _highPass = new(AudioFrameEventArgs.SampleRate);
    private Timer? _commandTimer;
    private ListeningMode _mode = ListeningMode.Off;
    private bool _suppressed;
    private float _level;
    private long _commandGeneration;

    public VoiceListener(IAudioInput input, ISettingsStore settings, ILogger<VoiceListener> logger)
    {
        _input = input;
        _settings = settings;
        _logger = logger;
        _input.FrameAvailable += OnFrame;
        _input.DeviceError += OnDeviceError;
    }

    public ListeningMode Mode { get { lock (_gate) { return _mode; } } }

    public float Level => Volatile.Read(ref _level);

    public bool Suppressed
    {
        get { lock (_gate) { return _suppressed; } }
        set
        {
            lock (_gate)
            {
                if (_suppressed == value)
                {
                    return;
                }

                _suppressed = value;
                ResetUtterance();
            }
        }
    }

    public event EventHandler<UtteranceEventArgs>? UtteranceCaptured;
    public event EventHandler? SpeechStarted;
    public event EventHandler? CommandTimeout;
    public event EventHandler<AudioDeviceErrorEventArgs>? MicrophoneError;

    public async Task SetModeAsync(ListeningMode mode, CancellationToken cancellationToken)
    {
        await _modeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _settings.Current;
            if (settings.Microphone.Muted && mode != ListeningMode.Off)
            {
                mode = ListeningMode.Off;
            }

            lock (_gate)
            {
                _mode = mode;
                ResetUtterance();
                ConfigureVad(mode);
                StopCommandTimer();
            }

            if (mode == ListeningMode.Off)
            {
                if (_input.IsCapturing)
                {
                    await _input.StopAsync().ConfigureAwait(false);
                }

                Volatile.Write(ref _level, 0);
                return;
            }

            if (!_input.IsCapturing || _input.CurrentDeviceId is null || (settings.Microphone.DeviceId is not null && _input.CurrentDeviceId != settings.Microphone.DeviceId))
            {
                await _input.StartAsync(settings.Microphone.DeviceId, cancellationToken).ConfigureAwait(false);
            }

            if (mode == ListeningMode.Command)
            {
                StartCommandTimer(settings.Activation.CommandListenTimeoutSeconds);
            }
        }
        finally
        {
            _modeLock.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await _modeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _highPass = new HighPassFilter(AudioFrameEventArgs.SampleRate);
                ConfigureVad(_mode);
                ResetUtterance();
            }

            var wasCapturing = _input.IsCapturing || Mode != ListeningMode.Off;
            if (_input.IsCapturing)
            {
                await _input.StopAsync().ConfigureAwait(false);
            }

            if (wasCapturing && Mode != ListeningMode.Off && !_settings.Current.Microphone.Muted)
            {
                await _input.StartAsync(_settings.Current.Microphone.DeviceId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _modeLock.Release();
        }
    }

    public void FinishUtterance()
    {
        float[]? samples = null;
        ListeningMode mode;
        lock (_gate)
        {
            mode = _mode;
            if (_vad.InSpeech && _utterance.Count > 0)
            {
                samples = _utterance.ToArray();
            }

            ResetUtterance();
            StopCommandTimer();
        }

        if (samples is not null)
        {
            UtteranceCaptured?.Invoke(this, new UtteranceEventArgs(samples, mode, endedBySilence: false));
        }
        else
        {
            CommandTimeout?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ConfigureVad(ListeningMode mode)
    {
        var s = _settings.Current;
        _vad.Sensitivity = s.Microphone.Sensitivity;
        _vad.HangoverFrames = mode == ListeningMode.WakeWord ? 20 : 27;
    }

    private int MaxUtteranceSamples()
    {
        var s = _settings.Current.Recognition;
        var seconds = Mode == ListeningMode.Command ? s.MaxConversationSeconds : s.MaxCommandSeconds;
        return seconds * AudioFrameEventArgs.SampleRate;
    }

    private void OnFrame(object? sender, AudioFrameEventArgs e)
    {
        float[]? completed = null;
        bool started = false;
        ListeningMode mode;
        var settings = _settings.Current.Microphone;
        var samples = e.Samples;
        AudioMath.ApplyGain(samples, settings.Gain);

        lock (_gate)
        {
            mode = _mode;
            if (settings.NoiseFilter)
            {
                _highPass.Process(samples);
            }

            var rms = AudioMath.Rms(samples);
            var level = Math.Clamp((AudioMath.ToDecibels(rms) + 60f) / 60f, 0f, 1f);
            Volatile.Write(ref _level, 0.7f * _level + 0.3f * level);

            if (mode == ListeningMode.Off || _suppressed)
            {
                return;
            }

            var max = MaxUtteranceSamples();
            _vad.Process(samples, (evt, frame) =>
            {
                switch (evt)
                {
                    case VadEvent.SpeechStart:
                        started = true;
                        foreach (var pre in _preRoll)
                        {
                            _utterance.AddRange(pre);
                        }

                        _preRoll.Clear();
                        _utterance.AddRange(frame);
                        break;
                    case VadEvent.SpeechEnd:
                        _utterance.AddRange(frame);
                        completed = _utterance.ToArray();
                        _utterance.Clear();
                        break;
                    default:
                        if (_vad.InSpeech)
                        {
                            _utterance.AddRange(frame);
                            if (_utterance.Count >= max)
                            {
                                completed = _utterance.ToArray();
                                _utterance.Clear();
                                _vad.Reset();
                            }
                        }
                        else
                        {
                            _preRoll.Enqueue(frame);
                            while (_preRoll.Count > PreRollFrames)
                            {
                                _preRoll.Dequeue();
                            }
                        }

                        break;
                }
            });

            if (started && mode == ListeningMode.Command)
            {
                StopCommandTimer();
            }
        }

        if (started)
        {
            SpeechStarted?.Invoke(this, EventArgs.Empty);
        }

        if (completed is not null)
        {
            var speechSeconds = completed.Length / (double)AudioFrameEventArgs.SampleRate;
            if (speechSeconds < 0.35)
            {
                _logger.LogDebug("Discarded {Ms} ms blip.", (int)(speechSeconds * 1000));
                if (mode == ListeningMode.Command)
                {
                    StartCommandTimer(_settings.Current.Activation.CommandListenTimeoutSeconds);
                }

                return;
            }

            UtteranceCaptured?.Invoke(this, new UtteranceEventArgs(completed, mode, endedBySilence: true));
        }
    }

    private void ResetUtterance()
    {
        _utterance.Clear();
        _preRoll.Clear();
        _vad.Reset();
    }

    private void StartCommandTimer(int seconds)
    {
        lock (_gate)
        {
            StopCommandTimer();
            var generation = ++_commandGeneration;
            _commandTimer = new Timer(_ =>
            {
                bool fire;
                lock (_gate)
                {
                    fire = generation == _commandGeneration && _mode == ListeningMode.Command && !_vad.InSpeech;
                    if (fire)
                    {
                        StopCommandTimer();
                    }
                }

                if (fire)
                {
                    CommandTimeout?.Invoke(this, EventArgs.Empty);
                }
            }, null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
        }
    }

    private void StopCommandTimer()
    {
        _commandGeneration++;
        _commandTimer?.Dispose();
        _commandTimer = null;
    }

    private void OnDeviceError(object? sender, AudioDeviceErrorEventArgs e)
    {
        lock (_gate)
        {
            ResetUtterance();
        }

        MicrophoneError?.Invoke(this, e);
    }

    public void Dispose()
    {
        _input.FrameAvailable -= OnFrame;
        _input.DeviceError -= OnDeviceError;
        lock (_gate)
        {
            StopCommandTimer();
        }

        _modeLock.Dispose();
    }
}

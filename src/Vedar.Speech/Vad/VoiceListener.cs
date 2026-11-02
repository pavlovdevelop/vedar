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
    private const int BargeInMaxSamples = 4 * AudioFrameEventArgs.SampleRate;
    /// <summary>After 450 ms of silence the audio so far is offered for early recognition.</summary>
    private const int PauseSignalFrames = 15;
    /// <summary>After "Ведар, …" a shorter pause is enough: the "still talking" check covers longer pauses.</summary>
    private const int MaxWakeWordPauseMs = 1000;
    private readonly IAudioInput _input;
    private readonly ISettingsStore _settings;
    private readonly ILogger<VoiceListener> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _modeLock = new(1, 1);
    private readonly EnergyVoiceActivityDetector _vad = new();
    private readonly EnergyVoiceActivityDetector _bargeVad = new() { HangoverFrames = 12 };
    private readonly List<float> _bargeUtterance = [];
    private bool _bargeIn;
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
    public event EventHandler<UtteranceEventArgs>? BargeInCaptured;
    public event EventHandler? BargeInSpeechStarted;
    public event EventHandler<UtteranceEventArgs>? SpeechPaused;
    public event EventHandler? SpeechResumed;
    private bool _bargeContinuation;
    private bool _pauseSignalled;
    private int _commitAtFrames;
    private int _listeningWindowSeconds;

    public int ListeningWindowSeconds
    {
        get { lock (_gate) { return _listeningWindowSeconds; } }
        set { lock (_gate) { _listeningWindowSeconds = Math.Max(0, value); } }
    }

    public void CommitAtSilence(int silenceMs)
    {
        float[]? completed = null;
        ListeningMode mode;
        lock (_gate)
        {
            mode = _mode;
            if (_mode != ListeningMode.Command || _suppressed || !_vad.InSpeech || _utterance.Count == 0)
            {
                return;
            }

            var frames = Math.Max(1, silenceMs / FrameMs);
            if (_vad.SilenceFrames >= frames)
            {
                completed = TakeUtterance();
            }
            else
            {
                _commitAtFrames = frames;
            }
        }

        if (completed is not null)
        {
            UtteranceCaptured?.Invoke(this, new UtteranceEventArgs(completed, mode, endedBySilence: true));
        }
    }

    private const int FrameMs = 1000 * EnergyVoiceActivityDetector.FrameSamples / AudioFrameEventArgs.SampleRate;

    /// <summary>Ends the utterance now (under the lock).</summary>
    private float[] TakeUtterance()
    {
        var samples = _utterance.ToArray();
        _utterance.Clear();
        _vad.Reset();
        _pauseSignalled = false;
        _commitAtFrames = 0;
        StopCommandTimer();
        return samples;
    }

    public bool BargeInContinuation
    {
        get { lock (_gate) { return _bargeContinuation; } }
        set
        {
            lock (_gate)
            {
                _bargeContinuation = value;
                _bargeVad.HangoverFrames = value ? PauseFrames() : 12;
            }
        }
    }

    public bool BargeInEnabled
    {
        get { lock (_gate) { return _bargeIn; } }
        set
        {
            lock (_gate)
            {
                if (_bargeIn == value)
                {
                    return;
                }

                _bargeIn = value;
                _bargeUtterance.Clear();
                _bargeVad.Reset();
                _bargeVad.Sensitivity = _settings.Current.Microphone.Sensitivity;
            }
        }
    }
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
                StartCommandTimer(WindowSeconds());
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
        // In command mode this is the longest pause: a complete sentence is answered earlier (CommitAtSilence).
        // After the wake word a shorter pause is used; the orchestrator's "still talking" check covers longer ones.
        _vad.HangoverFrames = mode == ListeningMode.WakeWord
            ? Math.Max(10, Math.Min(s.Activation.EndOfSpeechPauseMs, MaxWakeWordPauseMs) / FrameMs)
            : PauseFrames();
        _pauseSignalled = false;
        _commitAtFrames = 0;
    }

    private int PauseFrames() => Math.Max(10, _settings.Current.Activation.EndOfSpeechPauseMs / FrameMs);

    private int WindowSeconds() => _listeningWindowSeconds > 0 ? _listeningWindowSeconds : _settings.Current.Activation.CommandListenTimeoutSeconds;

    private int MaxUtteranceSamples()
    {
        var s = _settings.Current.Recognition;
        var seconds = Mode == ListeningMode.Command ? s.MaxConversationSeconds : s.MaxCommandSeconds;
        return seconds * AudioFrameEventArgs.SampleRate;
    }

    private void OnFrame(object? sender, AudioFrameEventArgs e)
    {
        float[]? completed = null;
        float[]? bargeIn = null;
        float[]? paused = null;
        bool resumed = false;
        bool started = false;
        bool bargeStarted = false;
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

            if (mode == ListeningMode.Off)
            {
                return;
            }

            if (_suppressed)
            {
                if (_bargeIn)
                {
                    bargeIn = CollectBargeIn(samples, ref bargeStarted);
                }
            }
            else
            {
                Detect(samples, mode, ref started, ref completed, ref paused, ref resumed);
            }

            if (started && mode == ListeningMode.Command)
            {
                StopCommandTimer();
            }
        }

        if (bargeStarted)
        {
            BargeInSpeechStarted?.Invoke(this, EventArgs.Empty);
        }

        if (bargeIn is not null)
        {
            BargeInCaptured?.Invoke(this, new UtteranceEventArgs(bargeIn, mode, endedBySilence: true));
        }

        if (started)
        {
            SpeechStarted?.Invoke(this, EventArgs.Empty);
        }

        if (resumed)
        {
            SpeechResumed?.Invoke(this, EventArgs.Empty);
        }

        if (paused is not null && completed is null)
        {
            SpeechPaused?.Invoke(this, new UtteranceEventArgs(paused, mode, endedBySilence: false));
        }

        if (completed is not null)
        {
            var speechSeconds = completed.Length / (double)AudioFrameEventArgs.SampleRate;
            if (speechSeconds < 0.35)
            {
                _logger.LogDebug("Discarded {Ms} ms blip.", (int)(speechSeconds * 1000));
                if (mode == ListeningMode.Command)
                {
                    StartCommandTimer(WindowSeconds());
                }

                return;
            }

            UtteranceCaptured?.Invoke(this, new UtteranceEventArgs(completed, mode, endedBySilence: true));
        }
    }

    /// <summary>Barge-in: short pieces of audio heard while VEDAR speaks. Called under the lock.</summary>
    private float[]? CollectBargeIn(float[] samples, ref bool startedOut)
    {
        float[]? piece = null;
        bool started = false;
        var max = _bargeContinuation ? MaxUtteranceSamples() : BargeInMaxSamples;
        _bargeVad.Process(samples, (evt, frame) =>
        {
            if (evt == VadEvent.SpeechStart)
            {
                started = true;
                if (_bargeContinuation)
                {
                    // The beginning of the continuation (pre-roll), so that no word is cut.
                    foreach (var pre in _preRoll)
                    {
                        _bargeUtterance.AddRange(pre);
                    }

                    _preRoll.Clear();
                }
            }

            if (evt == VadEvent.SpeechStart || _bargeVad.InSpeech || evt == VadEvent.SpeechEnd)
            {
                _bargeUtterance.AddRange(frame);
            }
            else if (_bargeContinuation)
            {
                _preRoll.Enqueue(frame);
                while (_preRoll.Count > PreRollFrames)
                {
                    _preRoll.Dequeue();
                }
            }

            if ((evt == VadEvent.SpeechEnd || _bargeUtterance.Count >= max) && _bargeUtterance.Count > 0)
            {
                // At least 0.3 s for a stop word ("спри" is short, clicks are shorter). A continuation is always
                // reported, so that the orchestrator never waits for a piece that was dropped.
                if (_bargeContinuation || _bargeUtterance.Count >= AudioFrameEventArgs.SampleRate * 3 / 10)
                {
                    piece = _bargeUtterance.ToArray();
                }

                _bargeUtterance.Clear();
                if (evt != VadEvent.SpeechEnd)
                {
                    _bargeVad.Reset();
                }
            }
        });
        startedOut = started;
        return piece;
    }

    /// <summary>Normal utterance detection. Called under the lock.</summary>
    private void Detect(float[] samples, ListeningMode mode, ref bool startedOut, ref float[]? completedOut, ref float[]? pausedOut, ref bool resumedOut)
    {
        bool started = false;
        bool resumed = false;
        float[]? completed = null;
        float[]? paused = null;
        var max = MaxUtteranceSamples();
        _vad.Process(samples, (evt, frame) =>
        {
            switch (evt)
            {
                case VadEvent.SpeechStart:
                    started = true;
                    _pauseSignalled = false;
                    _commitAtFrames = 0;
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
                    _pauseSignalled = false;
                    _commitAtFrames = 0;
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
                            _pauseSignalled = false;
                            _commitAtFrames = 0;
                            break;
                        }

                        if (mode != ListeningMode.Command)
                        {
                            break;
                        }

                        var silence = _vad.SilenceFrames;
                        if (silence == 0 && _pauseSignalled)
                        {
                            // The user went on speaking: the early recognition is outdated, and so is a planned commit.
                            _pauseSignalled = false;
                            _commitAtFrames = 0;
                            resumed = true;
                            paused = null;
                        }
                        else if (silence == PauseSignalFrames && !_pauseSignalled)
                        {
                            _pauseSignalled = true;
                            paused = _utterance.ToArray();
                        }

                        if (_commitAtFrames > 0 && silence >= _commitAtFrames)
                        {
                            completed = TakeUtterance();
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

        startedOut = started;
        completedOut = completed;
        pausedOut = paused;
        resumedOut = resumed;
    }

    private void ResetUtterance()
    {
        _utterance.Clear();
        _preRoll.Clear();
        _vad.Reset();
        _pauseSignalled = false;
        _commitAtFrames = 0;
        _bargeUtterance.Clear();
        _bargeVad.Reset();
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

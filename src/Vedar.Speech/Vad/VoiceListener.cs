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

    private readonly Func<double> _clockMs;

    /// <param name="clockMs">Milliseconds of a monotonic clock (tests pass the simulated microphone's time).</param>
    public VoiceListener(IAudioInput input, ISettingsStore settings, ILogger<VoiceListener> logger, Func<double>? clockMs = null)
    {
        _input = input;
        _settings = settings;
        _logger = logger;
        _clockMs = clockMs ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
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
                if (!value)
                {
                    // Listening starts: the last 300 ms (after VEDAR's voice and its echo have died away) become the
                    // pre-roll, so a user who starts to speak right now is not cut at the first word.
                    for (int i = 0; i + EnergyVoiceActivityDetector.FrameSamples <= _quietTail.Count; i += EnergyVoiceActivityDetector.FrameSamples)
                    {
                        _preRoll.Enqueue(_quietTail.GetRange(i, EnergyVoiceActivityDetector.FrameSamples).ToArray());
                    }
                }

                _quietTail.Clear();
            }
        }
    }

    /// <summary>The last 300 ms heard while suppressed (see <see cref="Suppressed"/>).</summary>
    private readonly List<float> _quietTail = [];
    private const int QuietTailSamples = PreRollFrames * EnergyVoiceActivityDetector.FrameSamples;

    public event EventHandler<UtteranceEventArgs>? UtteranceCaptured;
    public event EventHandler<UtteranceEventArgs>? BargeInCaptured;
    public event EventHandler? BargeInSpeechStarted;
    public event EventHandler<UtteranceEventArgs>? SpeechPaused;
    public event EventHandler? SpeechResumed;
    private bool _bargeContinuation;
    private bool _pauseSignalled;
    private int _listeningWindowSeconds;

    // The user starting to speak over VEDAR's voice (see EchoAwareSpeechDetector): from then on the user's speech
    // is captured until they go quiet, whatever VEDAR does meanwhile (stops, finishes, starts listening).
    private readonly EchoAwareSpeechDetector _detector = new();
    private readonly Queue<float[]> _recentFrames = new();
    private readonly float[] _frame = new float[EchoAwareSpeechDetector.FrameSamples];
    private int _frameFill;
    private bool _interruptOnSpeech;
    private List<float>? _userSpeech;
    private int _userQuietFrames;
    private const int UserSpeechPreRollFrames = 20; // 600 ms before the user was noticed

    public int ListeningWindowSeconds
    {
        get { lock (_gate) { return _listeningWindowSeconds; } }
        set { lock (_gate) { _listeningWindowSeconds = Math.Max(0, value); } }
    }

    private const int FrameMs = 1000 * EnergyVoiceActivityDetector.FrameSamples / AudioFrameEventArgs.SampleRate;

    public event EventHandler? UserSpeechStarted;
    public event EventHandler<UtteranceEventArgs>? UserSpeechCaptured;

    public bool InterruptOnSpeech
    {
        get { lock (_gate) { return _interruptOnSpeech; } }
        set
        {
            lock (_gate)
            {
                if (_interruptOnSpeech != value)
                {
                    _interruptOnSpeech = value;
                    _detector.Reset();
                }
            }
        }
    }

    public bool UserSpeechInProgress { get { lock (_gate) { return _userSpeech is not null; } } }

    public void NotePlayback(float[] samples, int sampleRate)
    {
        var now = NowMs();
        lock (_gate)
        {
            _detector.AddReference(samples, sampleRate, now);
        }
    }

    public void SetPlaybackAttenuation(double db)
    {
        var now = NowMs();
        lock (_gate)
        {
            _detector.Attenuate(db, now);
        }
    }

    public float[]? PeekUserSpeech()
    {
        lock (_gate)
        {
            return _userSpeech?.ToArray();
        }
    }

    public void DropUserSpeech()
    {
        lock (_gate)
        {
            _userSpeech = null;
            _detector.Reset();
        }
    }

    private double NowMs() => _clockMs();

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
                if (mode == ListeningMode.Off)
                {
                    _userSpeech = null;
                }
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
        // The whole pause in both modes: VEDAR must never answer while the user is still in the middle of a sentence.
        _vad.HangoverFrames = PauseFrames();
        _detector.AbsoluteFloorDb = _vad.AbsoluteFloorDb;
        _pauseSignalled = false;
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
        float[]? userCompleted = null;
        bool userStarted = false;
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

            if (_userSpeech is not null)
            {
                // The user is speaking (noticed over VEDAR's voice): keep listening until they go quiet,
                // whatever VEDAR does meanwhile.
                userCompleted = ContinueUserSpeech(samples);
                StopCommandTimer();
            }
            else if (_suppressed)
            {
                _quietTail.AddRange(samples);
                if (_quietTail.Count > QuietTailSamples)
                {
                    _quietTail.RemoveRange(0, _quietTail.Count - QuietTailSamples);
                }

                if (_bargeIn && _interruptOnSpeech && !_bargeContinuation && WatchForUser(samples))
                {
                    userStarted = true;
                    _bargeUtterance.Clear();
                    _bargeVad.Reset();
                }
                else if (_bargeIn)
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

        if (userStarted)
        {
            UserSpeechStarted?.Invoke(this, EventArgs.Empty);
        }

        if (userCompleted is not null)
        {
            UserSpeechCaptured?.Invoke(this, new UtteranceEventArgs(userCompleted, mode, endedBySilence: true));
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

    /// <summary>
    /// While VEDAR speaks or thinks: notices the user starting to speak (frame by frame, against VEDAR's own voice).
    /// When that happens, the user's speech capture starts with the last 600 ms. Called under the lock.
    /// </summary>
    private bool WatchForUser(float[] samples)
    {
        var frames = SplitFrames(samples);
        for (int i = 0; i < frames.Count; i++)
        {
            var (frame, ms) = frames[i];
            _recentFrames.Enqueue(frame);
            while (_recentFrames.Count > UserSpeechPreRollFrames)
            {
                _recentFrames.Dequeue();
            }

            if (_detector.Process(frame, ms))
            {
                _userSpeech = [];
                foreach (var pre in _recentFrames)
                {
                    _userSpeech.AddRange(pre);
                }

                _recentFrames.Clear();
                _userQuietFrames = 0;
                for (int j = i + 1; j < frames.Count; j++)
                {
                    _userSpeech.AddRange(frames[j].Frame);
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>Adds to the user's speech; returns it when the user has been quiet for the end-of-speech pause.</summary>
    private float[]? ContinueUserSpeech(float[] samples)
    {
        var max = _settings.Current.Recognition.MaxConversationSeconds * AudioFrameEventArgs.SampleRate;
        foreach (var (frame, ms) in SplitFrames(samples))
        {
            _userSpeech!.AddRange(frame);
            _detector.Process(frame, ms);
            _userQuietFrames = _detector.LastFrame.User ? 0 : _userQuietFrames + 1;
            if (_userQuietFrames >= PauseFrames() || _userSpeech.Count >= max)
            {
                var speech = _userSpeech.ToArray();
                _userSpeech = null;
                _detector.Reset();
                return speech;
            }
        }

        return null;
    }

    /// <summary>30 ms frames with the time each was captured (the chunk ends now).</summary>
    private List<(float[] Frame, double Ms)> SplitFrames(float[] samples)
    {
        var frames = new List<(float[], double)>();
        var size = EchoAwareSpeechDetector.FrameSamples;
        var total = (_frameFill + samples.Length) / size;
        var now = NowMs();
        int i = 0, n = 0;
        while (i < samples.Length)
        {
            var take = Math.Min(size - _frameFill, samples.Length - i);
            samples.AsSpan(i, take).CopyTo(_frame.AsSpan(_frameFill));
            _frameFill += take;
            i += take;
            if (_frameFill == size)
            {
                frames.Add(((float[])_frame.Clone(), now - (total - 1 - n) * 30.0));
                _frameFill = 0;
                n++;
            }
        }

        return frames;
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
                            break;
                        }

                        if (mode != ListeningMode.Command)
                        {
                            break;
                        }

                        var silence = _vad.SilenceFrames;
                        if (silence == 0 && _pauseSignalled)
                        {
                            // The user went on speaking: the early recognition is outdated.
                            _pauseSignalled = false;
                            resumed = true;
                            paused = null;
                        }
                        else if (silence == PauseSignalFrames && !_pauseSignalled)
                        {
                            _pauseSignalled = true;
                            paused = _utterance.ToArray();
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
            _userSpeech = null;
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

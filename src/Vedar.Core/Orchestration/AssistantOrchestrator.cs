using System.Text;
using Microsoft.Extensions.Logging;
using Vedar.Core.Abstractions;
using Vedar.Core.Conversation;
using Vedar.Core.Localization;
using Vedar.Core.Settings;
using Vedar.Core.Setup;
using Vedar.Core.State;
using Vedar.Core.Text;

namespace Vedar.Core.Orchestration;

public enum Speaker { User, Assistant, System }

public sealed record TranscriptEntry(Speaker Speaker, string Text, DateTimeOffset Timestamp);

public enum MicrophoneStatus
{
    /// <summary>Capturing audio (hands-free scanning or a command).</summary>
    Active,
    /// <summary>Closed; opens on push-to-talk.</summary>
    Standby,
    /// <summary>Muted by the user. Nothing is captured.</summary>
    Muted,
    NotFound,
    Error,
}

public sealed class AssistantNotificationEventArgs(UiNotificationKind kind, string text, bool spokenByVedar) : EventArgs
{
    public UiNotificationKind Kind { get; } = kind;
    public string Text { get; } = text;
    /// <summary>True when VEDAR also says this text aloud (so a screen reader need not repeat it).</summary>
    public bool SpokenByVedar { get; } = spokenByVedar;
}

/// <summary>
/// Coordinates listening, recognition, local commands, AI conversation and speech.
/// <para>
/// Work is organised in <em>turns</em>. Only one turn runs at a time, so two AI requests for the same
/// conversation can never run concurrently. While a turn runs the microphone is suppressed, which keeps
/// VEDAR from hearing its own voice. <see cref="Stop"/> cancels the current turn immediately and always wins.
/// Every turn ends by returning to <see cref="AssistantState.Idle"/> (or listening), even after errors.
/// </para>
/// </summary>
public sealed class AssistantOrchestrator : IAsyncDisposable
{
    private const int PostSpeechTailMs = 350;

    /// <summary>Without beeps, a short "Момент." after this long without an AI answer (a person would not stay silent).</summary>
    private const int ThinkingFillerDelayMs = 2500;

    /// <summary>After an early commit, how long VEDAR still listens for the user going on before it answers.</summary>
    private static readonly TimeSpan EarlyCommitWatch = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Recognition during the pause only when the main model is this fast on average. whisper.cpp checks for
    /// cancellation only after its encoder, so a recognition cancelled because the user went on keeps the CPU
    /// busy until then (measured in a 4-core container: the next one took 6.6 s instead of 3.8 s). Up to this speed
    /// that delays an answer by at most ~0.8 s, and only in a rare case, while it usually saves ~1 s; on slower
    /// computers VEDAR simply waits for the end of the pause.
    /// </summary>
    private const double MaxRecognitionMsForSpeculation = 2500;

    private readonly ILogger<AssistantOrchestrator> _logger;
    private readonly ISettingsStore _settingsStore;
    private readonly IVoiceListener _listener;
    private readonly ISpeechRecognizer _recognizer;
    private readonly IWakeWordMatcher _wakeWord;
    private readonly ISpeechSynthesizer _synthesizer;
    private readonly IAudioOutput _output;
    private readonly ICommandDispatcher _commands;
    private readonly IConversationAi _ai;
    private readonly Localizer _loc;
    private readonly IClock _clock;
    private readonly IConversationHistoryStore? _history;
    private readonly IAutoStartManager? _autoStart;
    private readonly ApiKeyPaste? _keys;
    private readonly IUserActivityMonitor? _activity;
    private readonly SmallTalk _smallTalk;
    private VoiceSetupDialog? _setup;

    private readonly object _turnGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _turnCts;
    private Task _currentTurn = Task.CompletedTask;
    private bool _turnActive;
    private long _turnGeneration;
    private ListeningMode? _nextListeningMode;
    private bool _nextListenIsFollowUp;
    private int _nextListenWindowSeconds;
    private bool _currentListenIsFollowUp;
    private bool _listenAfterCancel;
    /// <summary>Listening after a voice stop is quiet (like after an answer); after push-to-talk it is a request.</summary>
    private bool _listenAfterCancelQuietly;
    /// <summary>VEDAR said something to the user in the current turn: afterwards it listens without the wake word.</summary>
    private bool _spokeInTurn;
    /// <summary>The turn ended the exchange (goodbye, voice test, stop key): do not listen without the wake word.</summary>
    private bool _noFollowUpAfterTurn;
    private readonly StringBuilder _turnSpeech = new();
    private string _lastTurnSpeech = string.Empty;
    private DateTime _attentiveStartedUtc;
    private DateTime _attentiveDeadlineUtc;
    private DateTime _listenStartedUtc;
    /// <summary>"Не те разбрах" was already said to something heard without the wake word; further misses stay quiet.</summary>
    private bool _notUnderstoodSaid;
    private Speculation? _speculation;
    private long _speechGeneration;
    /// <summary>Moving average of how long the main model takes (ms; 0 = not measured yet).</summary>
    private double _recognitionMsAverage;
    private int _aiRequestActive;
    private bool _cloudNoticeGiven;
    private readonly HashSet<string> _backupCloudNoticeGiven = new(StringComparer.Ordinal);
    private SpeechPlaybackQueue? _aiQueue;
    private readonly StringBuilder _echoReference = new();
    private readonly Queue<string> _recentSentences = new();
    private CancellationTokenSource? _bargeInCts;
    private int _bargeInBusy;
    private ContinuationWatch? _continuationWatch;

    /// <summary>After a cut that looks unfinished ("… за"), how long to wait for the user to go on.</summary>
    private static readonly TimeSpan UnfinishedGrace = TimeSpan.FromSeconds(2.5);

    /// <summary>The user starts speaking again while VEDAR is still working out what was said.</summary>
    private sealed class ContinuationWatch
    {
        public volatile bool Started;
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
        public TaskCompletionSource<float[]> Piece { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Recognition started during the user's pause, before the end of the utterance is certain. If the user does
    /// not go on, its result is used — so the answer comes as soon as the pause is over (or earlier, see
    /// <see cref="ShouldAnswerNow"/>), instead of only then starting to recognize.
    /// </summary>
    private sealed class Speculation(float[] samples, string? prompt, long generation, CancellationTokenSource cts)
    {
        public float[] Samples { get; } = samples;
        public string? Prompt { get; } = prompt;
        public long Generation { get; } = generation;
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public Task<RecognitionResult> Task { get; set; } = System.Threading.Tasks.Task.FromResult(RecognitionResult.Empty(TimeSpan.Zero, TimeSpan.Zero));
        public volatile bool CommitRequested;

        public void Cancel()
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
    private bool _aiNotConfiguredExplained;
    private PendingConfirmation? _pendingConfirmation;
    private MicrophoneStatus _microphoneStatus = MicrophoneStatus.Standby;
    private bool _disposed;

    private sealed class PendingConfirmation(string question, Func<CancellationToken, Task<CommandResult>> action)
    {
        public string Question { get; } = question;
        public Func<CancellationToken, Task<CommandResult>> Action { get; } = action;
        public int UnclearAnswers { get; set; }
    }

    public AssistantOrchestrator(
        ILogger<AssistantOrchestrator> logger,
        ISettingsStore settingsStore,
        IVoiceListener listener,
        ISpeechRecognizer recognizer,
        IWakeWordMatcher wakeWord,
        ISpeechSynthesizer synthesizer,
        IAudioOutput output,
        ICommandDispatcher commands,
        IConversationAi ai,
        Localizer localizer,
        IClock? clock = null,
        IConversationHistoryStore? history = null,
        IUserInterfaceHost? ui = null,
        ISecretStore? secrets = null,
        IAutoStartManager? autoStart = null,
        IUserActivityMonitor? activity = null,
        SmallTalk? smallTalk = null)
    {
        _logger = logger;
        _settingsStore = settingsStore;
        _listener = listener;
        _recognizer = recognizer;
        _wakeWord = wakeWord;
        _synthesizer = synthesizer;
        _output = output;
        _commands = commands;
        _ai = ai;
        _loc = localizer;
        _clock = clock ?? SystemClock.Instance;
        _history = history;
        _autoStart = autoStart;
        _activity = activity;
        _smallTalk = smallTalk ?? new SmallTalk(localizer, _clock);
        _keys = ui is not null && secrets is not null ? new ApiKeyPaste(ui, secrets, ai, localizer) : null;
        Conversation = new ConversationSession(settingsStore.Current.Ai.MaxHistoryMessages);

        _listener.UtteranceCaptured += OnUtteranceCaptured;
        _listener.BargeInCaptured += OnBargeInCaptured;
        _listener.BargeInSpeechStarted += OnBargeInSpeechStarted;
        _listener.CommandTimeout += OnCommandTimeout;
        _listener.MicrophoneError += OnMicrophoneError;
        _listener.SpeechStarted += OnSpeechStarted;
        _listener.SpeechPaused += OnSpeechPaused;
        _listener.SpeechResumed += OnSpeechResumed;
        _settingsStore.SettingsChanged += OnSettingsChanged;
        _ai.FallbackUsed += OnAiFallback;
    }

    public AssistantStateMachine StateMachine { get; } = new();
    public AssistantState State => StateMachine.Current;
    public ConversationSession Conversation { get; }
    public bool InConversation { get; private set; }
    public string? LastSpokenText { get; private set; }
    public AiStatus? LastAiStatus { get; private set; }
    public string? PendingConfirmationQuestion => _pendingConfirmation?.Question;
    public bool IsTurnActive { get { lock (_turnGate) { return _turnActive; } } }
    public Task CurrentTurn { get { lock (_turnGate) { return _currentTurn; } } }
    public IReadOnlyList<string> StartupProblems { get; private set; } = [];

    /// <summary>The spoken first-run setup is in progress.</summary>
    public bool InVoiceSetup => Volatile.Read(ref _setup) is not null;

    /// <summary>The spoken setup can run: speech recognition, the voice and a microphone are available.</summary>
    public bool CanRunVoiceSetup =>
        _recognizer.IsReady && _synthesizer.IsReady && !Settings.Microphone.Muted
        && MicrophoneStatus is not (MicrophoneStatus.NotFound or MicrophoneStatus.Error);

    public MicrophoneStatus MicrophoneStatus
    {
        get => _microphoneStatus;
        private set
        {
            if (_microphoneStatus != value)
            {
                _microphoneStatus = value;
                MicrophoneStatusChanged?.Invoke(this, value);
            }
        }
    }

    private VedarSettings Settings => _settingsStore.Current;

    public event EventHandler<TranscriptEntry>? TranscriptAdded;
    public event EventHandler<MicrophoneStatus>? MicrophoneStatusChanged;
    public event EventHandler<AiStatus>? AiStatusChanged;
    public event EventHandler? ConfirmationChanged;
    public event EventHandler<bool>? ConversationChanged;
    public event EventHandler<AssistantNotificationEventArgs>? Notification;
    public event EventHandler<bool>? VoiceSetupChanged;

    // ------------------------------------------------------------------ startup

    /// <summary>Loads models, opens the microphone, checks the AI service and greets the user.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var ct = linked.Token;
        var problems = new List<string>();
        ApplyAudioSettings();

        var ttsTask = InitializeSafelyAsync("speech synthesis", () => _synthesizer.InitializeAsync(ct));
        var sttTask = InitializeSafelyAsync("speech recognition", () => _recognizer.InitializeAsync(ct));
        await Task.WhenAll(ttsTask, sttTask).ConfigureAwait(false);
        if (!_recognizer.IsReady)
        {
            problems.Add(_loc.Get("speech.speechModelMissing"));
        }

        await ApplyListeningModeAsync(DefaultListeningMode(), ct).ConfigureAwait(false);
        if (MicrophoneStatus == MicrophoneStatus.NotFound)
        {
            problems.Add(_loc.Get("speech.micNotFound"));
        }

        StartupProblems = problems;
        _ = RefreshAiStatusAsync(ct);
        StateMachine.TryTransition(AssistantState.Idle, "started");
        _logger.LogInformation("VEDAR started. STT ready: {Stt}, TTS ready: {Tts}, mic: {Mic}", _recognizer.IsReady, _synthesizer.IsReady, MicrophoneStatus);

        var greeting = new StringBuilder();
        if (Settings.General.GreetOnStartup && Settings.General.FirstRunCompleted)
        {
            greeting.Append(_smallTalk.StartupGreeting(Settings.General.UserName));
        }

        foreach (var p in problems)
        {
            greeting.Append(' ').Append(p);
        }

        if (greeting.Length > 0)
        {
            var text = greeting.ToString().Trim();
            RaiseNotification(problems.Count > 0 ? UiNotificationKind.Important : UiNotificationKind.Status, text, _synthesizer.IsReady);
            if (_synthesizer.IsReady)
            {
                TryRunTurn("greeting", token => SpeakAsync(text, token, remember: false));
            }
        }
    }

    private async Task InitializeSafelyAsync(string what, Func<Task> init)
    {
        try
        {
            await init().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize {What}.", what);
        }
    }

    public async Task<AiStatus> RefreshAiStatusAsync(CancellationToken cancellationToken = default)
    {
        AiStatus status;
        try
        {
            status = await _ai.CheckStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AI status check failed.");
            status = new AiStatus(AiAvailability.Unavailable, _ai.ActiveProviderName, _ai.ActiveProviderIsCloud, ex.Message);
        }

        LastAiStatus = status;
        AiStatusChanged?.Invoke(this, status);
        return status;
    }

    // ------------------------------------------------------------------ public controls

    /// <summary>
    /// Push-to-talk hotkey. Idle → start listening. Listening → finish the utterance.
    /// Speaking/processing → interrupt and listen.
    /// </summary>
    public void PushToTalk()
    {
        if (_disposed)
        {
            return;
        }

        var state = State;
        _logger.LogDebug("Push-to-talk in state {State}.", state);
        if (state is AssistantState.Starting or AssistantState.Stopped)
        {
            RaiseNotification(UiNotificationKind.Status, _loc.Get("speech.notReady"), false);
            return;
        }

        if (Settings.Microphone.Muted)
        {
            InterruptAndRun("mic-muted-hint", ct => SpeakAsync(_loc.Format("speech.micMuted", HotkeyText(Settings.Activation.MicrophoneToggleHotkey)), ct, remember: false));
            return;
        }

        lock (_turnGate)
        {
            if (_turnActive)
            {
                _listenAfterCancel = true;
                _turnCts?.Cancel();
                return;
            }
        }

        if (_listener.Mode == ListeningMode.Command)
        {
            if (_currentListenIsFollowUp)
            {
                // VEDAR was listening quietly after an answer; the key means "I want to say something now".
                _ = BeginCommandListeningAsync(explicitActivation: true, followUp: false);
                return;
            }

            _listener.FinishUtterance();
            return;
        }

        _ = BeginCommandListeningAsync(explicitActivation: true, followUp: false);
    }

    /// <summary>Stops speech, recognition and AI requests immediately. Has priority over everything else.</summary>
    public void Stop() => Stop(listenNext: false);

    /// <param name="listenNext">After the stop, listen for a request without the wake word ("Ведар, спри" by voice).</param>
    private void Stop(bool listenNext)
    {
        _logger.LogInformation("Stop requested in state {State}.", State);
        lock (_turnGate)
        {
            _listenAfterCancel = listenNext && _turnActive && !Settings.Microphone.Muted;
            _listenAfterCancelQuietly = _listenAfterCancel;
            _nextListeningMode = null;
            _noFollowUpAfterTurn = !listenNext;
            _turnCts?.Cancel();
        }

        CancelSpeculation();
        if (_pendingConfirmation is not null)
        {
            _pendingConfirmation = null;
            ConfirmationChanged?.Invoke(this, EventArgs.Empty);
        }

        EndVoiceSetup();

        if (!IsTurnActive)
        {
            _ = ReturnToDefaultListeningAsync(playCancel: _listener.Mode == ListeningMode.Command && !_currentListenIsFollowUp);
        }
    }

    /// <summary>Typed input from the conversation window (works without a microphone).</summary>
    public Task SubmitTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.CompletedTask;
        }

        return InterruptAndRunAsync("typed", ct => HandleUserTextAsync(text.Trim(), ct));
    }

    public Task RepeatAsync() =>
        InterruptAndRunAsync("repeat", ct => SpeakAsync(LastSpokenText ?? _loc.Get("speech.repeatNothing"), ct, remember: false));

    /// <summary>Speaks arbitrary text (used for the voice test in settings).</summary>
    public Task SpeakTextAsync(string text) => InterruptAndRunAsync("speak", async ct =>
    {
        _noFollowUpAfterTurn = true; // a voice test in the settings is not a conversation
        await SpeakAsync(text, ct, remember: false, addToTranscript: false).ConfigureAwait(false);
    });

    public Task StartConversationAsync() => InterruptAndRunAsync("conversation-start", ct => StartConversationCoreAsync(mentionedChatGpt: false, ct));

    public Task EndConversationAsync() => InterruptAndRunAsync("conversation-end", async ct =>
    {
        var wasActive = InConversation;
        EndConversationCore();
        _noFollowUpAfterTurn = true;
        await SpeakAsync(wasActive ? _loc.Pick("speech.conversation.end") : _loc.Get("speech.conversation.notActive"), ct, remember: false).ConfigureAwait(false);
    });

    /// <summary>Starts the spoken setup (also by voice: "Ведар, настройка").</summary>
    public Task StartVoiceSetupAsync() => InterruptAndRunAsync("voice-setup", StartVoiceSetupCoreAsync);

    /// <summary>Takes an API key from the clipboard, stores and checks it (also by voice: "Ведар, постави ключа").</summary>
    public Task PasteApiKeyAsync() => InterruptAndRunAsync("paste-key", PasteApiKeyCoreAsync);

    /// <summary>Clears the conversation context without speaking.</summary>
    public void NewConversation()
    {
        Conversation.Clear();
        InConversation = false;
        ConversationChanged?.Invoke(this, false);
    }

    /// <summary>Answers a pending confirmation from the keyboard (Yes/No buttons).</summary>
    public Task AnswerConfirmationAsync(bool yes) =>
        InterruptAndRunAsync("confirm", ct => HandleUserTextAsync(yes ? "да" : "не", ct, fromUi: true));

    public async Task SetMicrophoneMutedAsync(bool muted, bool announce = true)
    {
        var settings = Settings;
        if (settings.Microphone.Muted != muted)
        {
            settings.Microphone.Muted = muted;
            _settingsStore.Save(settings);
        }

        if (!IsTurnActive)
        {
            await ApplyListeningModeAsync(DefaultListeningMode(), _lifetime.Token).ConfigureAwait(false);
        }

        var text = muted
            ? _loc.Format("speech.micMuted", HotkeyText(settings.Activation.MicrophoneToggleHotkey))
            : _loc.Get("speech.micUnmuted");
        RaiseNotification(UiNotificationKind.Important, text, announce && _synthesizer.IsReady);
        if (announce)
        {
            await InterruptAndRunAsync("mic-toggle", ct => SpeakAsync(text, ct, remember: false)).ConfigureAwait(false);
        }
    }

    public Task ToggleMicrophoneAsync() => SetMicrophoneMutedAsync(!Settings.Microphone.Muted);

    // ------------------------------------------------------------------ turn engine

    private bool TryRunTurn(string name, Func<CancellationToken, Task> body)
    {
        CancellationToken token;
        long generation;
        lock (_turnGate)
        {
            if (_turnActive || _disposed)
            {
                _logger.LogDebug("Turn {Name} ignored because another turn is active.", name);
                return false;
            }

            _turnActive = true;
            _nextListeningMode = null;
            _nextListenIsFollowUp = false;
            _nextListenWindowSeconds = 0;
            _listenAfterCancel = false;
            _listenAfterCancelQuietly = false;
            _spokeInTurn = false;
            _noFollowUpAfterTurn = false;
            _turnCts?.Dispose();
            _turnCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            token = _turnCts.Token;
            generation = ++_turnGeneration;
            _listener.Suppressed = true;
            lock (_echoReference)
            {
                _echoReference.Clear();
                _recentSentences.Clear();
                _turnSpeech.Clear();
            }

            CancelSpeculation();
            _currentTurn = Task.Run(() => RunTurnAsync(name, body, token, generation), CancellationToken.None);
        }

        return true;
    }

    private async Task RunTurnAsync(string name, Func<CancellationToken, Task> body, CancellationToken token, long generation)
    {
        try
        {
            await body(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogInformation("Turn {Name} was cancelled.", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Turn {Name} failed.", name);
            await RecoverFromErrorAsync(ex).ConfigureAwait(false);
        }
        finally
        {
            await EndTurnAsync(generation).ConfigureAwait(false);
        }
    }

    private async Task RecoverFromErrorAsync(Exception ex)
    {
        StateMachine.TryTransition(AssistantState.Error, ex.Message);
        RaiseNotification(UiNotificationKind.Error, _loc.Get("speech.errorGeneric"), _synthesizer.IsReady);
        if (_lifetime.IsCancellationRequested || !_synthesizer.IsReady)
        {
            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await PlayEarconAsync(Earcon.Error, cts.Token).ConfigureAwait(false);
            await SpeakAsync(_loc.Get("speech.errorGeneric"), cts.Token, remember: false, addToTranscript: false).ConfigureAwait(false);
        }
        catch (Exception speakError)
        {
            _logger.LogWarning(speakError, "Could not announce the error.");
        }
    }

    private async Task EndTurnAsync(long generation)
    {
        ListeningMode next;
        bool followUp;
        bool explicitListen;
        int window;
        lock (_turnGate)
        {
            if (_listenAfterCancel)
            {
                // After "Ведар, спри" VEDAR listens quietly, as after an answer; after push-to-talk, as for a request.
                next = ListeningMode.Command;
                followUp = _listenAfterCancelQuietly;
                explicitListen = !_listenAfterCancelQuietly;
                window = 0;
            }
            else if (_nextListeningMode is { } planned)
            {
                next = planned;
                followUp = _nextListenIsFollowUp;
                explicitListen = false;
                window = _nextListenWindowSeconds;
            }
            else if (_spokeInTurn && !_noFollowUpAfterTurn && CanListenAttentively())
            {
                // Like a person who has just said something: wait for the answer, without "Ведар" and without a beep.
                next = ListeningMode.Command;
                followUp = true;
                explicitListen = false;
                window = 0;
            }
            else
            {
                next = DefaultListeningMode();
                followUp = false;
                explicitListen = false;
                window = 0;
            }

            _listenAfterCancel = false;
            _listenAfterCancelQuietly = false;
            _nextListeningMode = null;
            _nextListenIsFollowUp = false;
            _nextListenWindowSeconds = 0;
            _turnActive = false;
        }

        lock (_echoReference)
        {
            if (_turnSpeech.Length > 0)
            {
                _lastTurnSpeech = _turnSpeech.ToString();
            }
        }

        DisableBargeIn();
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        try
        {
            // Small tail so the end of our own voice (room echo) is not captured as a command.
            await Task.Delay(PostSpeechTailMs, _lifetime.Token).ConfigureAwait(false);
            lock (_turnGate)
            {
                if (_turnActive || generation != _turnGeneration)
                {
                    return;
                }
            }

            if (next == ListeningMode.Command && !Settings.Microphone.Muted)
            {
                await BeginCommandListeningAsync(explicitActivation: explicitListen, followUp: followUp, windowSeconds: window).ConfigureAwait(false);
            }
            else
            {
                await ApplyListeningModeAsync(next == ListeningMode.Command ? DefaultListeningMode() : next, _lifetime.Token).ConfigureAwait(false);
                StateMachine.TryTransition(AssistantState.Idle);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore listening after a turn.");
            StateMachine.TryTransition(AssistantState.Idle);
        }
    }

    /// <summary>Cancels the current turn, waits briefly for it to finish and runs a new one.</summary>
    private Task InterruptAndRunAsync(string name, Func<CancellationToken, Task> body) => InterruptAndRunCoreAsync(name, body);

    private void InterruptAndRun(string name, Func<CancellationToken, Task> body) => _ = InterruptAndRunCoreAsync(name, body);

    private async Task InterruptAndRunCoreAsync(string name, Func<CancellationToken, Task> body)
    {
        Task previous;
        lock (_turnGate)
        {
            _listenAfterCancel = false;
            _nextListeningMode = null;
            _turnCts?.Cancel();
            previous = _currentTurn;
        }

        try
        {
            await previous.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Previous turn did not finish cleanly.");
        }

        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (TryRunTurn(name, body))
            {
                Task started;
                lock (_turnGate)
                {
                    started = _currentTurn;
                }

                await started.ConfigureAwait(false);
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        _logger.LogWarning("Could not start turn {Name}: assistant busy.", name);
    }

    // ------------------------------------------------------------------ listening

    private ListeningMode DefaultListeningMode()
    {
        var s = Settings;
        if (s.Microphone.Muted)
        {
            return ListeningMode.Off;
        }

        return s.Activation.HandsFreeEnabled && _recognizer.IsReady ? ListeningMode.WakeWord : ListeningMode.Off;
    }

    /// <summary>After VEDAR has spoken it may listen without the wake word (hands-free on, a follow-up time set).</summary>
    private bool CanListenAttentively()
    {
        var s = Settings;
        return s.Activation.HandsFreeEnabled && s.Activation.FollowUpSeconds > 0 && _recognizer.IsReady && !s.Microphone.Muted;
    }

    private int FollowUpWindowSeconds()
    {
        var s = Settings.Activation;
        return s.FollowUpSeconds > 0 ? s.FollowUpSeconds : s.CommandListenTimeoutSeconds;
    }

    /// <summary>The user pressed a key or moved the mouse after <paramref name="sinceUtc"/> (busy with something else).</summary>
    private bool UserBusySince(DateTime sinceUtc)
    {
        if (_activity?.TimeSinceLastInput is not { } idle)
        {
            return false;
        }

        return DateTime.UtcNow - idle > sinceUtc + TimeSpan.FromMilliseconds(250);
    }

    private async Task ApplyListeningModeAsync(ListeningMode mode, CancellationToken ct)
    {
        try
        {
            await _listener.SetModeAsync(mode, ct).ConfigureAwait(false);
            if (mode != ListeningMode.Command)
            {
                _listener.Suppressed = false;
            }

            MicrophoneStatus = Settings.Microphone.Muted
                ? MicrophoneStatus.Muted
                : mode == ListeningMode.Off ? MicrophoneStatus.Standby : MicrophoneStatus.Active;
        }
        catch (AudioDeviceException ex)
        {
            _logger.LogWarning(ex, "Microphone unavailable.");
            MicrophoneStatus = MicrophoneStatus.NotFound;
            try
            {
                await _listener.SetModeAsync(ListeningMode.Off, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception offError)
            {
                _logger.LogDebug(offError, "Could not switch the listener off.");
            }
        }
    }

    /// <param name="followUp">
    /// Listening quietly after VEDAR has spoken: no beep, misses stay quiet, and the window is the follow-up time
    /// (<paramref name="windowSeconds"/> &gt; 0 continues an earlier window for the time that is left).
    /// </param>
    private async Task BeginCommandListeningAsync(bool explicitActivation, bool followUp, int windowSeconds = 0)
    {
        if (_recognizer.IsReady is false)
        {
            InterruptAndRun("stt-missing", async ct =>
            {
                _noFollowUpAfterTurn = true;
                await SpeakAsync(_loc.Get("speech.speechModelMissing"), ct, remember: false).ConfigureAwait(false);
            });
            return;
        }

        try
        {
            _currentListenIsFollowUp = followUp;
            _listener.Suppressed = true;
            if (Settings.Voice.Earcons && !followUp)
            {
                await PlayEarconAsync(Earcon.ListenStart, _lifetime.Token).ConfigureAwait(false);
            }

            var now = DateTime.UtcNow;
            if (followUp && windowSeconds <= 0)
            {
                windowSeconds = FollowUpWindowSeconds();
                _attentiveStartedUtc = now;
                _attentiveDeadlineUtc = now.AddSeconds(windowSeconds);
            }

            _listenStartedUtc = now;
            _listener.ListeningWindowSeconds = Math.Max(0, windowSeconds);
            await ApplyListeningModeAsync(ListeningMode.Command, _lifetime.Token).ConfigureAwait(false);
            if (MicrophoneStatus == MicrophoneStatus.NotFound)
            {
                RaiseNotification(UiNotificationKind.Error, _loc.Get("speech.micNotFound"), true);
                TryRunTurn("mic-missing", async ct =>
                {
                    _noFollowUpAfterTurn = true;
                    await SpeakAsync(_loc.Get("speech.micNotFound"), ct, remember: false).ConfigureAwait(false);
                });
                return;
            }

            _listener.Suppressed = false;
            StateMachine.TryTransition(AssistantState.Listening, explicitActivation ? "push-to-talk" : followUp ? "follow-up" : "listening");
            if (!followUp)
            {
                RaiseNotification(UiNotificationKind.Status, _loc.Get("state.Listening"), false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start command listening.");
            StateMachine.TryTransition(AssistantState.Idle);
        }
    }

    private async Task ReturnToDefaultListeningAsync(bool playCancel)
    {
        try
        {
            if (playCancel && Settings.Voice.Earcons)
            {
                _listener.Suppressed = true;
                await PlayEarconAsync(Earcon.Cancel, _lifetime.Token).ConfigureAwait(false);
            }

            await ApplyListeningModeAsync(DefaultListeningMode(), _lifetime.Token).ConfigureAwait(false);
            StateMachine.TryTransition(AssistantState.Idle);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to return to default listening.");
        }
    }

    private void OnSpeechStarted(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref _speechGeneration);
        CancelSpeculation(); // a new utterance: an earlier early recognition is outdated
        if (_listener.Mode == ListeningMode.Command)
        {
            _logger.LogDebug("Speech started.");
        }
    }

    private void OnUtteranceCaptured(object? sender, UtteranceEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.Mode == ListeningMode.Command)
        {
            var followUp = _currentListenIsFollowUp;
            var speculation = Interlocked.Exchange(ref _speculation, null);
            if (followUp && UserBusySince(_attentiveStartedUtc))
            {
                // The user went back to the keyboard (the screen reader may be talking): only "Ведар, …" counts now.
                speculation?.Cancel();
                _logger.LogInformation("Keyboard used while listening without the wake word; the utterance needs the wake word.");
                TryRunTurn("wake-scan", ct => HandleWakeUtteranceAsync(e.Samples, ct));
                return;
            }

            if (!TryRunTurn("command", ct => HandleCommandUtteranceAsync(e.Samples, followUp, speculation, ct)))
            {
                speculation?.Cancel();
                _logger.LogDebug("Command utterance dropped (busy).");
            }
        }
        else if (e.Mode == ListeningMode.WakeWord)
        {
            TryRunTurn("wake-scan", ct => HandleWakeUtteranceAsync(e.Samples, ct));
        }
    }

    private void OnCommandTimeout(object? sender, EventArgs e)
    {
        if (IsTurnActive)
        {
            return;
        }

        if (Volatile.Read(ref _setup) is { } setup)
        {
            TryRunTurn("setup-silence", ct => ApplySetupReplyAsync(setup.OnSilence(), ct));
            return;
        }

        var followUp = _currentListenIsFollowUp;
        _currentListenIsFollowUp = false;
        CancelSpeculation();
        _logger.LogInformation("No speech within the listening window (follow-up: {FollowUp}).", followUp);
        _ = ReturnToDefaultListeningAsync(playCancel: !followUp);
    }

    private void OnMicrophoneError(object? sender, AudioDeviceErrorEventArgs e)
    {
        _logger.LogWarning(e.Exception, "Microphone error: {Message}", e.Message);
        MicrophoneStatus = MicrophoneStatus.Error;
        RaiseNotification(UiNotificationKind.Error, _loc.Get("speech.micError"), _synthesizer.IsReady);
        TryRunTurn("mic-error", async ct =>
        {
            _noFollowUpAfterTurn = true;
            await SpeakAsync(_loc.Get("speech.micError"), ct, remember: false).ConfigureAwait(false);
        });
        _ = RetryMicrophoneAsync();
    }

    private async Task RetryMicrophoneAsync()
    {
        for (int attempt = 0; attempt < 6 && !_lifetime.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _lifetime.Token).ConfigureAwait(false);
                if (IsTurnActive || Settings.Microphone.Muted)
                {
                    continue;
                }

                await _listener.RestartAsync(_lifetime.Token).ConfigureAwait(false);
                await ApplyListeningModeAsync(DefaultListeningMode(), _lifetime.Token).ConfigureAwait(false);
                if (MicrophoneStatus is MicrophoneStatus.Active or MicrophoneStatus.Standby)
                {
                    _logger.LogInformation("Microphone recovered.");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Microphone retry {Attempt} failed.", attempt + 1);
            }
        }
    }

    // ------------------------------------------------------------------ turn bodies

    private async Task HandleWakeUtteranceAsync(float[] samples, CancellationToken ct)
    {
        // "Ведар, искам да говорим… за историята": a pause may have cut the sentence; keep listening meanwhile.
        StartContinuationWatch();
        try
        {
            await HandleWakeUtteranceCoreAsync(samples, ct).ConfigureAwait(false);
        }
        finally
        {
            StopContinuationWatch();
        }
    }

    private async Task HandleWakeUtteranceCoreAsync(float[] samples, CancellationToken ct)
    {
        var wakeResult = await _recognizer.RecognizeAsync(samples, new RecognitionRequest(RecognitionPurpose.WakeWord, RecognitionPrompts.WakeWord), ct).ConfigureAwait(false);
        LogRecognized("wake-scan", wakeResult);
        var match = _wakeWord.Match(wakeResult.Text);
        if (!match.IsMatch)
        {
            return;
        }

        _logger.LogInformation("Wake word detected ({Token}).", match.MatchedToken);
        _notUnderstoodSaid = false;
        var continued = Volatile.Read(ref _continuationWatch)?.Started == true;
        if (!HasMeaningfulCommand(match.Request) && !continued)
        {
            StopContinuationWatch();
            _currentListenIsFollowUp = false;
            if (Settings.Accessibility.SpeakStatusChanges)
            {
                await SpeakAsync(_loc.Pick("speech.listening"), ct, remember: false, addToTranscript: false).ConfigureAwait(false);
            }

            ExpectAnswer();
            return;
        }

        // The wake model is small and fast; re-run the main model on the full utterance for accuracy.
        StateMachine.TryTransition(AssistantState.Recognizing, "wake-word");
        Task<RecognitionResult> Recognize(float[] audio) => _recognizer.RecognizeAsync(audio, new RecognitionRequest(RecognitionPurpose.Command, CommandPrompt), ct);
        var full = await Recognize(samples).ConfigureAwait(false);
        NoteRecognitionTime(full);
        (samples, full) = await CompleteUtteranceAsync(samples, full, Recognize, ct).ConfigureAwait(false);
        LogRecognized("command", full);
        var command = match.Request;
        if (!full.IsEmpty)
        {
            var fullMatch = _wakeWord.Match(full.Text);
            command = fullMatch.IsMatch ? fullMatch.Request : full.Text;

            // The command vocabulary sometimes bends a greeting ("Добро утро" → "Добро отвори"); the fast model,
            // which has no such vocabulary, may have heard it right.
            if (_commands.FindIntent(command) is null && SmallTalk.IsSocialIntent(_commands.FindIntent(match.Request)))
            {
                _logger.LogDebug("Using the fast model's transcript for a greeting.");
                command = match.Request;
            }
        }

        if (!HasMeaningfulCommand(command))
        {
            await SpeakAsync(_loc.Pick("speech.listening"), ct, remember: false, addToTranscript: false).ConfigureAwait(false);
            ExpectAnswer();
            return;
        }

        await HandleUserTextAsync(command, ct).ConfigureAwait(false);
    }

    private async Task HandleCommandUtteranceAsync(float[] samples, bool followUp, Speculation? speculation, CancellationToken ct)
    {
        StateMachine.TryTransition(AssistantState.Recognizing);
        if (Settings.Voice.Earcons && !followUp)
        {
            await PlayEarconAsync(Earcon.ListenEnd, ct).ConfigureAwait(false);
        }

        StartContinuationWatch();
        var prompt = CommandPrompt;
        Task<RecognitionResult> Recognize(float[] audio) => _recognizer.RecognizeAsync(audio, new RecognitionRequest(RecognitionPurpose.Command, prompt), ct);
        var (result, reused) = await RecognizeOrReuseAsync(samples, prompt, speculation, Recognize, ct).ConfigureAwait(false);
        if (!reused)
        {
            NoteRecognitionTime(result);
        }

        var minWatch = reused && speculation!.CommitRequested ? EarlyCommitWatch : TimeSpan.Zero;
        (samples, result) = await CompleteUtteranceAsync(samples, result, Recognize, ct, minWatch).ConfigureAwait(false);
        LogRecognized(reused ? "command (early)" : "command", result);
        if (result.IsEmpty && Volatile.Read(ref _setup) is { } quietSetup)
        {
            await ApplySetupReplyAsync(quietSetup.OnSilence(), ct).ConfigureAwait(false);
            return;
        }

        if (result.IsEmpty)
        {
            if (followUp && _pendingConfirmation is null)
            {
                // Probably a noise while VEDAR listened quietly: stay quiet and keep listening.
                ContinueAttentiveListening();
                return;
            }

            await SpeakAsync(_loc.Pick("speech.notUnderstood"), ct, remember: false).ConfigureAwait(false);
            ExpectAnswer();
            return;
        }

        var text = result.Text;
        if (followUp && EchoDetector.IsLikelyEcho(text, _lastTurnSpeech))
        {
            // VEDAR's own words coming back (loudspeakers or a screen reader reading the answer): not the user.
            _logger.LogInformation("Ignored VEDAR's own words heard while listening.");
            ContinueAttentiveListening();
            return;
        }

        var match = _wakeWord.Match(text);
        if (match.IsMatch)
        {
            text = match.Request;
            if (!HasMeaningfulCommand(text))
            {
                await SpeakAsync(_loc.Pick("speech.listening"), ct, remember: false, addToTranscript: false).ConfigureAwait(false);
                ExpectAnswer();
                return;
            }
        }

        await HandleUserTextAsync(text, ct, attentive: followUp && !match.IsMatch).ConfigureAwait(false);
    }

    /// <summary>Uses the recognition started during the pause if the user did not go on; otherwise recognizes now.</summary>
    private async Task<(RecognitionResult Result, bool Reused)> RecognizeOrReuseAsync(
        float[] samples, string? prompt, Speculation? speculation, Func<float[], Task<RecognitionResult>> recognize, CancellationToken ct)
    {
        if (speculation is not null)
        {
            var tail = samples.Length - speculation.Samples.Length;
            var maxTail = (Settings.Activation.EndOfSpeechPauseMs + 300) * AudioFrameEventArgs.SampleRate / 1000;
            if (speculation.Generation == Interlocked.Read(ref _speechGeneration) && speculation.Prompt == prompt && tail >= 0 && tail <= maxTail)
            {
                try
                {
                    var early = await speculation.Task.WaitAsync(ct).ConfigureAwait(false);
                    _logger.LogDebug("Used the recognition started during the pause ({Ms} ms saved).", (int)(DateTime.UtcNow - speculation.StartedUtc).TotalMilliseconds);
                    return (early, true);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // It was cancelled after all; recognize the whole utterance below.
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Early recognition failed; recognizing again.");
                }
            }
            else
            {
                speculation.Cancel();
            }
        }

        return (await recognize(samples).ConfigureAwait(false), false);
    }

    /// <summary>The command vocabulary prompt improves short commands; in a free conversation only the wake word is primed.</summary>
    private string? CommandPrompt => Volatile.Read(ref _setup) is { } setup
        ? setup.RecognitionPrompt
        : InConversation ? RecognitionPrompts.WakeWord : RecognitionPrompts.Commands;

    private static bool HasMeaningfulCommand(string? text) => BulgarianText.NormalizeForMatching(text).Count(char.IsLetterOrDigit) >= 2;

    /// <param name="attentive">Heard without the wake word while VEDAR listened after speaking (may not be meant for VEDAR).</param>
    private async Task HandleUserTextAsync(string text, CancellationToken ct, bool fromUi = false, bool attentive = false)
    {
        if (!fromUi || _pendingConfirmation is null)
        {
            AddTranscript(Speaker.User, text);
        }

        if (Volatile.Read(ref _setup) is { } setup)
        {
            StateMachine.TryTransition(AssistantState.Processing, "setup");
            var reply = await setup.HandleAsync(text, ct, userBusy: UserBusySince(_listenStartedUtc)).ConfigureAwait(false);
            await ApplySetupReplyAsync(reply, ct).ConfigureAwait(false);
            return;
        }

        if (_pendingConfirmation is { } pending)
        {
            await HandleConfirmationAnswerAsync(pending, text, ct).ConfigureAwait(false);
            return;
        }

        StateMachine.TryTransition(AssistantState.Processing);
        var result = await _commands.DispatchAsync(text, ct).ConfigureAwait(false);
        _logger.LogInformation("Command result: {Outcome} {Id}", result.Outcome, result.CommandId);
        switch (result.Outcome)
        {
            case CommandOutcome.Handled when result.CommandId == SmallTalk.AckCommandId && InConversation:
                // "Добре." in a conversation answers what the AI just asked.
                await HandleUnknownTextAsync(text, ct, attentive).ConfigureAwait(false);
                break;
            case CommandOutcome.Handled:
            case CommandOutcome.Failed:
                _notUnderstoodSaid = false;
                RememberSmallTalk(text, result);
                await ApplyCommandResultAsync(result, ct).ConfigureAwait(false);
                break;
            case CommandOutcome.NeedsConfirmation when result.ConfirmAction is not null:
                _pendingConfirmation = new PendingConfirmation(result.SpokenText ?? string.Empty, result.ConfirmAction);
                ConfirmationChanged?.Invoke(this, EventArgs.Empty);
                RaiseNotification(UiNotificationKind.Important, result.SpokenText ?? string.Empty, true);
                await SpeakAsync(result.SpokenText ?? string.Empty, ct, remember: true).ConfigureAwait(false);
                ExpectAnswer();
                break;
            default:
                await HandleUnknownTextAsync(text, ct, attentive).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Keeps "Здравей" / "Как си?" exchanges as conversation context, so that the AI understands a following
    /// "да" or "зле съм" (they are sent only together with a later question to the AI).
    /// </summary>
    private void RememberSmallTalk(string text, CommandResult result)
    {
        if (result.CommandId?.StartsWith(SmallTalk.CommandPrefix, StringComparison.Ordinal) == true
            && result.Directive == AssistantDirective.None && !string.IsNullOrWhiteSpace(result.SpokenText))
        {
            Conversation.MaxMessages = Settings.Ai.MaxHistoryMessages;
            Conversation.AddUser(text);
            Conversation.AddAssistant(result.SpokenText);
        }
    }

    private async Task HandleConfirmationAnswerAsync(PendingConfirmation pending, string text, CancellationToken ct)
    {
        switch (ConfirmationParser.Parse(text))
        {
            case ConfirmationAnswer.Yes:
                _pendingConfirmation = null;
                ConfirmationChanged?.Invoke(this, EventArgs.Empty);
                StateMachine.TryTransition(AssistantState.Processing, "confirmed");
                var result = await pending.Action(ct).ConfigureAwait(false);
                await ApplyCommandResultAsync(result, ct).ConfigureAwait(false);
                break;
            case ConfirmationAnswer.No:
                _pendingConfirmation = null;
                ConfirmationChanged?.Invoke(this, EventArgs.Empty);
                await SpeakAsync(_loc.Pick("speech.confirmCancelled"), ct, remember: false).ConfigureAwait(false);
                break;
            default:
                if (pending.UnclearAnswers++ < 1)
                {
                    await SpeakAsync(_loc.Get("speech.confirmUnclear"), ct, remember: false).ConfigureAwait(false);
                    ExpectAnswer();
                }
                else
                {
                    _pendingConfirmation = null;
                    ConfirmationChanged?.Invoke(this, EventArgs.Empty);
                    await SpeakAsync(_loc.Pick("speech.confirmCancelled"), ct, remember: false).ConfigureAwait(false);
                }

                break;
        }
    }

    private async Task ApplyCommandResultAsync(CommandResult result, CancellationToken ct)
    {
        switch (result.Directive)
        {
            case AssistantDirective.Stop:
                _nextListeningMode = null;
                return;
            case AssistantDirective.Repeat:
                await SpeakAsync(LastSpokenText ?? _loc.Get("speech.repeatNothing"), ct, remember: false).ConfigureAwait(false);
                return;
            case AssistantDirective.StartConversation:
                await StartConversationCoreAsync(result.CommandId == "conversation.start.chatgpt", ct).ConfigureAwait(false);
                return;
            case AssistantDirective.EndConversation:
                var wasActive = InConversation;
                EndConversationCore();
                _noFollowUpAfterTurn = true; // "довиждане": the user is done for now
                await SpeakAsync(result.SpokenText ?? (wasActive ? _loc.Pick("speech.conversation.end") : _loc.Get("speech.conversation.notActive")), ct, remember: false).ConfigureAwait(false);
                return;
            case AssistantDirective.MuteMicrophone:
                var mutedText = _loc.Format("speech.micMutedByVoice", HotkeyText(Settings.Activation.MicrophoneToggleHotkey));
                await SpeakAsync(mutedText, ct, remember: false).ConfigureAwait(false);
                var s = Settings;
                s.Microphone.Muted = true;
                _settingsStore.Save(s);
                _nextListeningMode = ListeningMode.Off;
                return;
            case AssistantDirective.StartVoiceSetup:
                await StartVoiceSetupCoreAsync(ct).ConfigureAwait(false);
                return;
            case AssistantDirective.PasteApiKey:
                await PasteApiKeyCoreAsync(ct).ConfigureAwait(false);
                return;
            case AssistantDirective.ListenAgain:
                if (!string.IsNullOrWhiteSpace(result.SpokenText))
                {
                    await SpeakAsync(result.SpokenText, ct).ConfigureAwait(false);
                }

                ExpectAnswer();
                return;
            default:
                if (!string.IsNullOrWhiteSpace(result.SpokenText))
                {
                    if (result.Outcome == CommandOutcome.Failed)
                    {
                        RaiseNotification(UiNotificationKind.Error, result.SpokenText, true);
                    }

                    await SpeakAsync(result.SpokenText, ct).ConfigureAwait(false);
                }

                return; // after speaking, VEDAR listens without the wake word (see EndTurnAsync)
        }
    }

    // ------------------------------------------------------------------ spoken setup

    private async Task StartVoiceSetupCoreAsync(CancellationToken ct)
    {
        if (!CanRunVoiceSetup)
        {
            await SpeakAsync(_loc.Get(_recognizer.IsReady ? "speech.micNotFound" : "speech.speechModelMissing"), ct, remember: false).ConfigureAwait(false);
            return;
        }

        EndConversationCore();
        _noFollowUpAfterTurn = false;
        var setup = new VoiceSetupDialog(_settingsStore, _loc, _keys, _autoStart,
            HotkeyText(Settings.Activation.PushToTalkHotkey), HotkeyText(Settings.Activation.StopHotkey));
        Volatile.Write(ref _setup, setup);
        VoiceSetupChanged?.Invoke(this, true);
        _logger.LogInformation("Spoken setup started.");
        await ApplySetupReplyAsync(setup.Start(), ct).ConfigureAwait(false);
    }

    private async Task ApplySetupReplyAsync(SetupReply reply, CancellationToken ct)
    {
        if (reply.Finished)
        {
            EndVoiceSetup();
        }

        await SpeakAsync(reply.Text, ct).ConfigureAwait(false);
        if (!reply.Finished)
        {
            ExpectAnswer(Volatile.Read(ref _setup)?.ListenSeconds ?? 0); // the answer, without the wake word
        }
    }

    private void EndVoiceSetup()
    {
        if (Interlocked.Exchange(ref _setup, null) is not null)
        {
            _logger.LogInformation("Spoken setup ended.");
            VoiceSetupChanged?.Invoke(this, false);
            _ = RefreshAiStatusAsync(CancellationToken.None);
        }
    }

    private async Task PasteApiKeyCoreAsync(CancellationToken ct)
    {
        if (_keys is null)
        {
            await SpeakAsync(_loc.Get("key.notAvailable"), ct, remember: false).ConfigureAwait(false);
            return;
        }

        StateMachine.TryTransition(AssistantState.Processing, "api-key");
        var result = await _keys.PasteAsync(null, ct).ConfigureAwait(false);
        RaiseNotification(result.Works ? UiNotificationKind.Status : UiNotificationKind.Important, result.Spoken, true);
        await SpeakAsync(result.Spoken, ct, remember: false).ConfigureAwait(false);
        _ = RefreshAiStatusAsync(CancellationToken.None);
    }

    /// <summary>VEDAR asked something: listen for the answer without the wake word.</summary>
    private void ExpectAnswer(int windowSeconds = 0)
    {
        _nextListeningMode = ListeningMode.Command;
        _nextListenIsFollowUp = false;
        _nextListenWindowSeconds = windowSeconds;
    }

    /// <summary>Listen quietly without the wake word, as after an answer.</summary>
    private void SetFollowUp()
    {
        _nextListeningMode = ListeningMode.Command;
        _nextListenIsFollowUp = true;
        _nextListenWindowSeconds = 0;
    }

    /// <summary>
    /// Something heard while listening quietly was not for VEDAR (noise, its own echo): keep listening quietly for
    /// the rest of the same window — a cough must not end the conversation.
    /// </summary>
    private void ContinueAttentiveListening()
    {
        var left = _attentiveDeadlineUtc - DateTime.UtcNow;
        if (left >= TimeSpan.FromSeconds(2) && CanListenAttentively())
        {
            _nextListeningMode = ListeningMode.Command;
            _nextListenIsFollowUp = true;
            _nextListenWindowSeconds = (int)Math.Ceiling(left.TotalSeconds);
        }
    }

    private async Task HandleUnknownTextAsync(string text, CancellationToken ct, bool attentive = false)
    {
        if (_ai.IsConfigured)
        {
            _notUnderstoodSaid = false;
            await AskAiAsync(text, ct).ConfigureAwait(false);
            return;
        }

        if (!_aiNotConfiguredExplained)
        {
            _aiNotConfiguredExplained = true;
            await SpeakAsync(_loc.Get("speech.ai.notConfigured"), ct).ConfigureAwait(false);
            return;
        }

        if (attentive && _notUnderstoodSaid)
        {
            // Said once already: this was probably not meant for VEDAR (a television, someone else). Stay quiet.
            ContinueAttentiveListening();
            return;
        }

        _notUnderstoodSaid = attentive;
        await SpeakAsync(_loc.Pick("speech.notUnderstood"), ct, remember: false).ConfigureAwait(false);
    }

    private async Task StartConversationCoreAsync(bool mentionedChatGpt, CancellationToken ct)
    {
        if (!_ai.IsConfigured)
        {
            await SpeakAsync(_loc.Get("speech.ai.notConfigured"), ct).ConfigureAwait(false);
            return;
        }

        var provider = _ai.ActiveProviderName;
        var text = new StringBuilder();
        if (InConversation && !mentionedChatGpt)
        {
            text.Append(_loc.Get("speech.conversation.alreadyActive"));
        }
        else
        {
            text.Append(mentionedChatGpt
                ? _loc.Format("speech.conversation.startChatGpt", provider)
                : _loc.Pick("speech.conversation.start", provider));
        }

        if (_ai.ActiveProviderIsCloud && Settings.Ai.AnnounceCloudUse && !_cloudNoticeGiven)
        {
            _cloudNoticeGiven = true;
            text.Insert(0, _loc.Format("speech.conversation.cloudNotice", provider) + " ");
        }

        BeginConversation();
        await SpeakAsync(text.ToString(), ct).ConfigureAwait(false);
        SetFollowUp(); // "Какво те интересува?" — listen for the answer, quietly
    }

    private void BeginConversation()
    {
        if (!InConversation)
        {
            InConversation = true;
            ConversationChanged?.Invoke(this, true);
        }
    }

    private void EndConversationCore()
    {
        Conversation.Clear();
        if (InConversation)
        {
            InConversation = false;
            ConversationChanged?.Invoke(this, false);
        }
    }

    private async Task AskAiAsync(string text, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _aiRequestActive, 1, 0) != 0)
        {
            await SpeakAsync(_loc.Get("speech.ai.busy"), ct, remember: false).ConfigureAwait(false);
            return;
        }

        var provider = _ai.ActiveProviderName;
        var reply = new StringBuilder();
        SpeechPlaybackQueue? queue = null;
        try
        {
            if (_ai.ActiveProviderIsCloud && Settings.Ai.AnnounceCloudUse && !_cloudNoticeGiven)
            {
                _cloudNoticeGiven = true;
                await SpeakAsync(_loc.Format("speech.conversation.cloudNotice", provider), ct, remember: false).ConfigureAwait(false);
            }

            BeginConversation();
            StateMachine.TryTransition(AssistantState.Processing, "ai");
            Conversation.MaxMessages = Settings.Ai.MaxHistoryMessages;
            Conversation.AddUser(text);
            _history?.Append(Conversation.Id, ChatMessage.User(text));
            var messages = Conversation.BuildRequest(SystemPromptBuilder.Build(provider, Settings.General.UserName, _clock.Now));
            var chunker = new StreamingSentenceChunker();
            queue = new SpeechPlaybackQueue(_synthesizer, _output, new SynthesisOptions(Settings.Voice.Rate), OnSpeakingStarted, _logger, ct, OnSentencePlaying);
            Volatile.Write(ref _aiQueue, queue);
            using var thinking = StartThinkingCue(queue, ct);

            EnableBargeIn(); // "Ведар, спри" also cancels a request that is still being answered
            await foreach (var token in _ai.StreamReplyAsync(messages, ct).ConfigureAwait(false))
            {
                thinking.Cancel();
                reply.Append(token);
                foreach (var sentence in chunker.Append(token))
                {
                    queue.Enqueue(sentence);
                }
            }

            var rest = chunker.Flush();
            if (rest is not null)
            {
                queue.Enqueue(rest);
            }

            queue.Complete();
            var replyText = reply.ToString().Trim();
            if (replyText.Length == 0)
            {
                throw new AiProviderException(AiErrorKind.InvalidResponse, "Empty reply.");
            }

            Conversation.AddAssistant(replyText);
            _history?.Append(Conversation.Id, ChatMessage.Assistant(replyText));
            AddTranscript(Speaker.Assistant, replyText);
            LastSpokenText = replyText;
            RaiseNotification(UiNotificationKind.Response, replyText, true);
            _spokeInTurn = true;
            await queue.Completion.ConfigureAwait(false);
        }
        catch (AiProviderException ex)
        {
            _logger.LogWarning("AI request failed: {Kind} {Message}", ex.Kind, ex.Message);
            queue?.Complete();
            var partial = reply.ToString().Trim();
            if (partial.Length == 0)
            {
                Conversation.RemoveLastUserMessage();
            }
            else
            {
                Conversation.AddAssistant(partial);
            }

            if (queue is not null)
            {
                try
                {
                    await queue.Completion.ConfigureAwait(false);
                }
                catch (Exception playbackError) when (playbackError is not OperationCanceledException)
                {
                    _logger.LogDebug(playbackError, "Playback of partial reply failed.");
                }
            }

            // After a switch to the backup service, the error is the backup's (the switch itself was announced).
            var message = partial.Length > 0 ? _loc.Get("speech.ai.interrupted") : DescribeAiError(ex, _ai.ActiveProviderName);
            if (ex.Kind is AiErrorKind.NoConnection or AiErrorKind.Unauthorized or AiErrorKind.MissingApiKey)
            {
                _ = RefreshAiStatusAsync(CancellationToken.None);
            }

            RaiseNotification(UiNotificationKind.Error, message, true);
            await SpeakAsync(message, ct, remember: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var partial = reply.ToString().Trim();
            if (partial.Length == 0)
            {
                Conversation.RemoveLastUserMessage();
            }
            else
            {
                Conversation.AddAssistant(partial + " …");
                AddTranscript(Speaker.Assistant, partial + " …");
            }

            throw;
        }
        finally
        {
            Volatile.Write(ref _aiQueue, null);
            if (queue is not null)
            {
                await queue.DisposeAsync().ConfigureAwait(false);
            }

            Volatile.Write(ref _aiRequestActive, 0);
        }
    }

    /// <summary>The main AI service failed before answering; say that the backup answers (and, once, that it is a cloud service).</summary>
    private void OnAiFallback(object? sender, AiFallbackEventArgs e)
    {
        var text = _loc.Format("speech.ai.fallback", e.FailedProvider, e.BackupProvider);
        if (e.BackupIsCloud && Settings.Ai.AnnounceCloudUse && _backupCloudNoticeGiven.Add(e.BackupProvider))
        {
            text += " " + _loc.Format("speech.ai.fallbackCloud", e.BackupProvider);
        }

        _logger.LogInformation("AI fallback: {From} -> {To} ({Reason}).", e.FailedProvider, e.BackupProvider, e.Reason);
        RaiseNotification(UiNotificationKind.Status, text, true);
        Volatile.Read(ref _aiQueue)?.Enqueue(text);
    }

    /// <summary>
    /// If the answer takes a while: a short tone (with beeps on), otherwise a spoken "Момент." — so that the user
    /// knows VEDAR heard the question. Cancel the returned source when the first words arrive.
    /// </summary>
    private CancellationTokenSource StartThinkingCue(SpeechPlaybackQueue queue, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var earcon = Settings.Voice.Earcons;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(earcon ? 1500 : ThinkingFillerDelayMs, cts.Token).ConfigureAwait(false);
                if (earcon)
                {
                    await PlayEarconAsync(Earcon.Thinking, cts.Token).ConfigureAwait(false);
                }
                else if (!cts.IsCancellationRequested && !queue.HasStartedSpeaking)
                {
                    queue.Enqueue(_loc.Pick("speech.ai.thinking"));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Thinking cue failed.");
            }
        }, CancellationToken.None);

        return cts;
    }

    public string DescribeAiError(AiProviderException ex, string provider) => ex.Kind switch
    {
        AiErrorKind.NotConfigured => _loc.Get("speech.ai.notConfigured"),
        AiErrorKind.MissingApiKey => _loc.Get("speech.ai.missingKey"),
        AiErrorKind.Unauthorized => _loc.Format("speech.ai.unauthorized", provider),
        AiErrorKind.RateLimited => _loc.Format("speech.ai.rateLimited", provider),
        AiErrorKind.NoConnection => _loc.Format("speech.ai.offline", provider),
        AiErrorKind.Timeout => _loc.Format("speech.ai.timeout", provider),
        AiErrorKind.ModelNotFound => _loc.Format("speech.ai.modelNotFound", provider),
        AiErrorKind.InsufficientResources => _loc.Get("speech.ai.insufficientResources"),
        AiErrorKind.Busy => _loc.Get("speech.ai.busy"),
        AiErrorKind.InvalidResponse => _loc.Format("speech.ai.invalidResponse", provider),
        _ => _loc.Format("speech.ai.serverError", provider),
    };

    // ------------------------------------------------------------------ speaking

    private void OnSpeakingStarted()
    {
        _listener.Suppressed = true;
        EnableBargeIn();
        StateMachine.TryTransition(AssistantState.Speaking);
    }

    // ------------------------------------------------------------------ answering at the right moment

    /// <summary>
    /// The user paused for a moment (450 ms). Start recognizing what was said so far: if the user does not go on,
    /// the result is ready when the pause is over — and a complete short command is answered even a little earlier.
    /// </summary>
    private void OnSpeechPaused(object? sender, UtteranceEventArgs e)
    {
        if (_disposed || e.Mode != ListeningMode.Command || IsTurnActive || !_recognizer.IsReady || Settings.Microphone.Muted
            || Volatile.Read(ref _recognitionMsAverage) > MaxRecognitionMsForSpeculation)
        {
            return;
        }

        var prompt = CommandPrompt;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var speculation = new Speculation(e.Samples, prompt, Interlocked.Read(ref _speechGeneration), cts);
        Interlocked.Exchange(ref _speculation, speculation)?.Cancel();
        speculation.Task = Task.Run(() => _recognizer.RecognizeAsync(e.Samples, new RecognitionRequest(RecognitionPurpose.Command, prompt), cts.Token), CancellationToken.None);
        _ = speculation.Task.ContinueWith(t =>
        {
            try
            {
                if (!t.IsCompletedSuccessfully)
                {
                    return;
                }

                NoteRecognitionTime(t.Result);
                if (Volatile.Read(ref _speculation) != speculation)
                {
                    return;
                }

                // Answer early only if the full pause is not about to end anyway, and only a complete short command.
                var pauseMs = Settings.Activation.EndOfSpeechPauseMs;
                var silenceMs = (DateTime.UtcNow - speculation.StartedUtc).TotalMilliseconds + 450;
                if (silenceMs < pauseMs - 250 && ShouldAnswerNow(t.Result.Text))
                {
                    speculation.CommitRequested = true;
                    _listener.CommitAtSilence(EarlyCommitSilenceMs());
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Early commit check failed.");
            }
            finally
            {
                cts.Dispose();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void NoteRecognitionTime(RecognitionResult result)
    {
        var ms = result.ProcessingTime.TotalMilliseconds;
        if (ms <= 0)
        {
            return;
        }

        var average = Volatile.Read(ref _recognitionMsAverage);
        Volatile.Write(ref _recognitionMsAverage, average <= 0 ? ms : 0.7 * average + 0.3 * ms);
    }

    /// <summary>The user went on speaking: the early recognition is outdated.</summary>
    private void OnSpeechResumed(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref _speechGeneration);
        CancelSpeculation();
    }

    private void CancelSpeculation() => Interlocked.Exchange(ref _speculation, null)?.Cancel();

    /// <summary>The shorter pause after which a complete short command is answered (800 ms with the default 1.4 s).</summary>
    private int EarlyCommitSilenceMs()
    {
        var pause = Settings.Activation.EndOfSpeechPauseMs;
        return Math.Clamp(pause - 600, 450, pause);
    }

    /// <summary>
    /// A complete local command, a clear "да"/"не" to a question, or a clear answer in the setup — not a free
    /// question for the AI, a greeting or "добре" (people often go on after those), and nothing that sounds unfinished.
    /// </summary>
    private bool ShouldAnswerNow(string text)
    {
        var match = _wakeWord.Match(text);
        var request = match.IsMatch ? match.Request : text;
        if (!HasMeaningfulCommand(request) || UtteranceCompleteness.LooksUnfinished(request) || UtteranceCompleteness.LooksLikeOpener(request))
        {
            return false;
        }

        if (Volatile.Read(ref _setup) is { } setup)
        {
            return setup.IsCompleteAnswer(request);
        }

        if (_pendingConfirmation is not null)
        {
            return ConfirmationParser.Parse(request) != ConfirmationAnswer.Unknown;
        }

        return _commands.IsCompleteCommand(request);
    }

    // ------------------------------------------------------------------ "still talking": the rest of a sentence

    /// <summary>While the first part is recognized, keep listening: a pause may have cut the sentence.</summary>
    private void StartContinuationWatch()
    {
        if (Settings.Microphone.Muted || !_recognizer.IsReady)
        {
            return;
        }

        Volatile.Write(ref _continuationWatch, new ContinuationWatch());
        _listener.BargeInContinuation = true;
        _listener.BargeInEnabled = true;
    }

    private void StopContinuationWatch()
    {
        if (Interlocked.Exchange(ref _continuationWatch, null) is not null)
        {
            _listener.BargeInEnabled = false;
            _listener.BargeInContinuation = false;
        }
    }

    /// <summary>
    /// If the user went on speaking after the pause (or the first part ends like "… за"), waits for the rest and
    /// recognizes the whole sentence at once — instead of answering half of it.
    /// </summary>
    /// <param name="minWatch">After an early commit: listen at least this long (from the start of the watch) for more.</param>
    private async Task<(float[] Samples, RecognitionResult Result)> CompleteUtteranceAsync(
        float[] samples, RecognitionResult result, Func<float[], Task<RecognitionResult>> recognize, CancellationToken ct, TimeSpan minWatch = default)
    {
        try
        {
            for (int part = 0; part < 4; part++)
            {
                var watch = Volatile.Read(ref _continuationWatch);
                if (watch is null)
                {
                    break;
                }

                var deadline = UtteranceCompleteness.LooksUnfinished(result.Text)
                    ? DateTime.UtcNow + UnfinishedGrace
                    : part == 0 ? watch.CreatedUtc + minWatch : DateTime.MinValue;
                while (!watch.Started && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20, ct).ConfigureAwait(false);
                }

                if (!watch.Started)
                {
                    break;
                }

                float[] piece;
                try
                {
                    piece = await watch.Piece.Task.WaitAsync(TimeSpan.FromSeconds(Settings.Recognition.MaxConversationSeconds + 5), ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    break;
                }

                // Keep watching: the user may pause once more.
                Volatile.Write(ref _continuationWatch, new ContinuationWatch());
                if (piece.Length < AudioFrameEventArgs.SampleRate * 3 / 10)
                {
                    continue; // a click or a breath
                }

                var combined = new float[samples.Length + AudioFrameEventArgs.SampleRate / 4 + piece.Length];
                samples.CopyTo(combined, 0);
                piece.CopyTo(combined, samples.Length + AudioFrameEventArgs.SampleRate / 4);
                samples = combined;
                result = await recognize(samples).ConfigureAwait(false);
                _logger.LogInformation("The user went on speaking; recognized the whole sentence ({Seconds:F1} s).", samples.Length / (double)AudioFrameEventArgs.SampleRate);
            }
        }
        finally
        {
            StopContinuationWatch();
        }

        return (samples, result);
    }

    // ------------------------------------------------------------------ voice stop ("Ведар, спри" while speaking)

    private void EnableBargeIn()
    {
        var s = Settings;
        if (!s.Activation.VoiceStopWhileSpeaking || s.Microphone.Muted || !_recognizer.IsReady || (_listener.BargeInEnabled && !_listener.BargeInContinuation))
        {
            return;
        }

        lock (_turnGate)
        {
            if (!_turnActive || _turnCts is null)
            {
                return;
            }

            _bargeInCts?.Dispose();
            _bargeInCts = CancellationTokenSource.CreateLinkedTokenSource(_turnCts.Token);
        }

        Volatile.Write(ref _continuationWatch, null);
        _listener.BargeInContinuation = false;
        _listener.BargeInEnabled = true;
    }

    private void DisableBargeIn()
    {
        Volatile.Write(ref _continuationWatch, null);
        _listener.BargeInEnabled = false;
        _listener.BargeInContinuation = false;
        lock (_turnGate)
        {
            // A check still running must not hold the recognizer when the user speaks the next command.
            _bargeInCts?.Cancel();
        }
    }

    /// <summary>
    /// The echo reference is what VEDAR is playing right now: the current and the previous sentence (a piece of
    /// audio is at most 4 s and its check takes a moment). Not the whole turn: a help text that mentions
    /// "Ведар, спри" must not make the user's own "Ведар, спри" look like echo minutes later.
    /// </summary>
    private void OnSentencePlaying(string sentence)
    {
        lock (_echoReference)
        {
            _turnSpeech.Append(sentence).Append(' ');
            _recentSentences.Enqueue(sentence);
            while (_recentSentences.Count > 2)
            {
                _recentSentences.Dequeue();
            }

            _echoReference.Clear().AppendJoin(' ', _recentSentences);
        }
    }

    /// <summary>A short piece of audio heard while VEDAR speaks: stop at once if it is "Ведар, спри" (not our own voice).</summary>
    private void OnBargeInSpeechStarted(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _continuationWatch) is { } watch)
        {
            watch.Started = true;
        }
    }

    private void OnBargeInCaptured(object? sender, UtteranceEventArgs e)
    {
        if (Volatile.Read(ref _continuationWatch) is { } watch)
        {
            // The rest of the user's sentence, heard while VEDAR was still recognizing the first part.
            watch.Started = true;
            watch.Piece.TrySetResult(e.Samples);
            return;
        }

        if (_disposed || Interlocked.CompareExchange(ref _bargeInBusy, 1, 0) != 0)
        {
            return; // one check at a time; pieces heard meanwhile are skipped
        }

        CancellationToken token;
        long generation;
        lock (_turnGate)
        {
            if (!_turnActive || _bargeInCts is null || _bargeInCts.IsCancellationRequested)
            {
                Volatile.Write(ref _bargeInBusy, 0);
                return;
            }

            token = _bargeInCts.Token;
            generation = _turnGeneration;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _recognizer.RecognizeAsync(e.Samples, new RecognitionRequest(RecognitionPurpose.WakeWord, RecognitionPrompts.WakeWord), token).ConfigureAwait(false);
                LogRecognized("while speaking", result);
                string reference;
                lock (_echoReference)
                {
                    reference = _echoReference.ToString();
                }

                var action = StopPhraseDetector.Classify(result.Text, reference, _wakeWord.IsWakeToken, _wakeWord.Match(result.Text).IsMatch);
                if (action == BargeInAction.None)
                {
                    return;
                }

                lock (_turnGate)
                {
                    if (!_turnActive || generation != _turnGeneration)
                    {
                        return;
                    }
                }

                if (action == BargeInAction.Stop)
                {
                    // Stop, then listen for the next request without the wake word: the user is talking to VEDAR.
                    _logger.LogInformation("Stop by voice while speaking.");
                    Stop(listenNext: true);
                    return;
                }

                // "Ведар, колко е часът?" while VEDAR speaks: stop speaking and handle the new request (main model).
                _logger.LogInformation("Interrupted by the wake word while speaking.");
                InterruptAndRun("wake-while-speaking", ct => HandleWakeUtteranceAsync(e.Samples, ct));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Voice stop check failed.");
            }
            finally
            {
                Volatile.Write(ref _bargeInBusy, 0);
            }
        }, CancellationToken.None);
    }

    /// <summary>Speaks text inside the current turn. Respects cancellation immediately.</summary>
    private async Task SpeakAsync(string text, CancellationToken ct, bool remember = true, bool addToTranscript = true)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _spokeInTurn = true;
        if (remember)
        {
            LastSpokenText = text;
        }

        if (addToTranscript)
        {
            AddTranscript(Speaker.Assistant, text);
        }

        if (!_synthesizer.IsReady)
        {
            _logger.LogWarning("Speech synthesis unavailable; showing text only.");
            RaiseNotification(UiNotificationKind.Response, text, false);
            return;
        }

        await using var queue = new SpeechPlaybackQueue(_synthesizer, _output, new SynthesisOptions(Settings.Voice.Rate), OnSpeakingStarted, _logger, ct, OnSentencePlaying);
        queue.Enqueue(text);
        queue.Complete();
        await queue.Completion.ConfigureAwait(false);
    }

    private async Task PlayEarconAsync(Earcon earcon, CancellationToken ct)
    {
        try
        {
            await _output.PlayAsync(Earcons.Create(earcon), Earcons.SampleRate, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Earcon playback failed.");
        }
    }

    // ------------------------------------------------------------------ helpers

    private void AddTranscript(Speaker speaker, string text) =>
        TranscriptAdded?.Invoke(this, new TranscriptEntry(speaker, text, _clock.Now));

    private void RaiseNotification(UiNotificationKind kind, string text, bool spoken)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            Notification?.Invoke(this, new AssistantNotificationEventArgs(kind, text, spoken));
        }
    }

    private void LogRecognized(string purpose, RecognitionResult result)
    {
        if (Settings.Privacy.DiagnosticLogging)
        {
            _logger.LogInformation("Recognized ({Purpose}, {Ms} ms, conf {Conf:F2}): {Text}", purpose, (int)result.ProcessingTime.TotalMilliseconds, result.Confidence, result.Text);
        }
        else
        {
            _logger.LogDebug("Recognized ({Purpose}) {Chars} chars in {Ms} ms.", purpose, result.Text.Length, (int)result.ProcessingTime.TotalMilliseconds);
        }
    }

    /// <summary>"Ctrl+Shift+Space" → "Контрол, Шифт и Интервал" for speech.</summary>
    public static string HotkeyText(string hotkey)
    {
        var names = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(k => k switch
        {
            "Ctrl" or "Control" => "Контрол",
            "Shift" => "Шифт",
            "Alt" => "Алт",
            "Win" => "Уиндоус",
            "Space" => "Интервал",
            "Back" or "Backspace" => "Бекспейс",
            "Escape" or "Esc" => "Ескейп",
            "Enter" or "Return" => "Ентър",
            "Pause" => "Пауза",
            "Insert" => "Инсърт",
            "Delete" => "Делийт",
            _ => k,
        }).ToList();
        return names.Count <= 1 ? string.Join(string.Empty, names) : string.Join(", ", names.Take(names.Count - 1)) + " и " + names[^1];
    }

    private void ApplyAudioSettings()
    {
        _output.Volume = Settings.Voice.Volume;
        _output.DeviceId = Settings.Voice.OutputDeviceId;
    }

    private void OnSettingsChanged(object? sender, VedarSettings settings)
    {
        ApplyAudioSettings();
        Conversation.MaxMessages = settings.Ai.MaxHistoryMessages;
        if (!IsTurnActive && State is AssistantState.Idle)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _listener.RestartAsync(_lifetime.Token).ConfigureAwait(false);
                    await ApplyListeningModeAsync(DefaultListeningMode(), _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to apply new listening settings.");
                }
            });
        }

        _ = RefreshAiStatusAsync(_lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listener.UtteranceCaptured -= OnUtteranceCaptured;
        _listener.BargeInCaptured -= OnBargeInCaptured;
        _listener.BargeInSpeechStarted -= OnBargeInSpeechStarted;
        _listener.CommandTimeout -= OnCommandTimeout;
        _listener.MicrophoneError -= OnMicrophoneError;
        _listener.SpeechStarted -= OnSpeechStarted;
        _listener.SpeechPaused -= OnSpeechPaused;
        _listener.SpeechResumed -= OnSpeechResumed;
        CancelSpeculation();
        _settingsStore.SettingsChanged -= OnSettingsChanged;
        _ai.FallbackUsed -= OnAiFallback;
        _lifetime.Cancel();
        try
        {
            await CurrentTurn.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
            // Shutting down.
        }

        try
        {
            await _listener.SetModeAsync(ListeningMode.Off, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Shutting down.
        }

        StateMachine.TryTransition(AssistantState.Stopped, "disposed");
        _turnCts?.Dispose();
        _lifetime.Dispose();
    }
}

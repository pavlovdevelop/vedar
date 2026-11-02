using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vedar.Core.Abstractions;
using Vedar.Core.Settings;

namespace Vedar.Tests.Support;

public sealed class TestSettingsStore : ISettingsStore
{
    public TestSettingsStore(VedarSettings? settings = null) => Current = (settings ?? new VedarSettings()).Normalize();
    public VedarSettings Current { get; private set; }
    public int SaveCount { get; private set; }
    public event EventHandler<VedarSettings>? SettingsChanged;
    public VedarSettings Load() => Current;

    public void Save(VedarSettings settings)
    {
        Current = settings.Normalize();
        SaveCount++;
        SettingsChanged?.Invoke(this, settings);
    }
}

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;
}

/// <summary>Logger that keeps every formatted line (used to assert that secrets never reach logs).</summary>
public sealed class CapturingLogger : ILogger
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
}

public sealed class CapturingLogger<T> : ILogger<T>
{
    public CapturingLogger Inner { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Inner.Log(logLevel, eventId, state, exception, formatter);
}

public static class Log
{
    public static ILogger<T> Null<T>() => NullLogger<T>.Instance;
}

public sealed class FakeUiHost : IUserInterfaceHost
{
    public int ShowMainCount, ShowSettingsCount, ShowConversationCount, ExitRequests;
    public string? FileToPick { get; set; }
    public string? Clipboard { get; set; }
    public List<(UiNotificationKind Kind, string Text)> Notifications { get; } = [];
    public void ShowMainWindow() => ShowMainCount++;
    public void ShowSettings() => ShowSettingsCount++;
    public void ShowConversation() => ShowConversationCount++;
    public Task<string?> PickTextFileAsync(CancellationToken cancellationToken) => Task.FromResult(FileToPick);
    public Task<string?> GetClipboardTextAsync() => Task.FromResult(Clipboard);
    public Task ClearClipboardAsync() { Clipboard = null; return Task.CompletedTask; }
    public void RequestExit() => ExitRequests++;
    public void Notify(UiNotificationKind kind, string text) => Notifications.Add((kind, text));
}

public sealed class FakeSystemActions : ISystemActions
{
    public List<string> Actions { get; } = [];
    public float Volume { get; set; } = 0.5f;
    public bool Succeed { get; set; } = true;
    public bool OpenDefaultBrowser() { Actions.Add("browser"); return Succeed; }
    public bool OpenUrl(Uri url) { Actions.Add("url:" + url.AbsoluteUri); return Succeed; }
    public bool OpenNotepad() { Actions.Add("notepad"); return Succeed; }
    public bool OpenDocumentsFolder() { Actions.Add("documents"); return Succeed; }
    public float? ChangeSystemVolume(float delta) => Volume = Math.Clamp(Volume + delta, 0, 1);
    public float? GetSystemVolume() => Volume;
    public float? SetSystemVolume(float level) { Actions.Add($"volume:{level:F2}"); return Volume = Math.Clamp(level, 0, 1); }
}

public sealed class FakeHistoryStore : IConversationHistoryStore
{
    public List<(string Id, ChatMessage Message)> Appended { get; } = [];
    public int DeleteCount { get; private set; }
    public void Append(string conversationId, ChatMessage message) => Appended.Add((conversationId, message));
    public IReadOnlyList<string> ListConversations() => Appended.Select(a => a.Id).Distinct().ToList();
    public void DeleteAll() { DeleteCount++; Appended.Clear(); }
}

/// <summary>Recognizer that returns scripted transcripts in order (or by a function).</summary>
public sealed class FakeSpeechRecognizer : ISpeechRecognizer
{
    private readonly ConcurrentQueue<string> _scripted = new();
    public Func<float[], RecognitionRequest, string>? Responder { get; set; }
    public List<RecognitionRequest> Requests { get; } = [];
    public bool Ready { get; set; } = true;
    public Exception? ThrowOnce { get; set; }
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public string EngineName => "fake";
    public bool IsReady => Ready;
    public void Enqueue(params string[] transcripts) { foreach (var t in transcripts) { _scripted.Enqueue(t); } }
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<RecognitionResult> RecognizeAsync(float[] samples16k, RecognitionRequest request, CancellationToken cancellationToken)
    {
        lock (Requests) { Requests.Add(request); }
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (ThrowOnce is { } ex)
        {
            ThrowOnce = null;
            throw ex;
        }

        var text = Responder?.Invoke(samples16k, request) ?? (_scripted.TryDequeue(out var t) ? t : string.Empty);
        return new RecognitionResult(text, 0.9f, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5));
    }

    public void Dispose() { }
}

/// <summary>Synthesizer that produces 1 kHz tones whose length is proportional to the text (10 ms per character).</summary>
public sealed class FakeSpeechSynthesizer : ISpeechSynthesizer
{
    public ConcurrentQueue<string> Spoken { get; } = new();
    public bool Ready { get; set; } = true;
    public string EngineName => "fake";
    public string VoiceName => "fake-voice";
    public bool IsReady => Ready;
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public string AllSpoken => string.Join(" | ", Spoken);

    public async IAsyncEnumerable<SynthesizedAudio> SynthesizeAsync(string text, SynthesisOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var sentence in Vedar.Core.Text.SentenceSplitter.Split(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Spoken.Enqueue(sentence);
            await Task.Yield();
            yield return new SynthesizedAudio(new float[Math.Max(160, sentence.Length * 160)], 16000, sentence);
        }
    }

    public void Dispose() { }
}

/// <summary>Voice listener controlled by the test: emits utterances on demand.</summary>
public sealed class FakeVoiceListener : IVoiceListener
{
    public ListeningMode Mode { get; private set; } = ListeningMode.Off;
    public List<ListeningMode> ModeHistory { get; } = [];
    public List<bool> SuppressedHistory { get; } = [];
    private bool _suppressed;
    public bool Suppressed { get => _suppressed; set { _suppressed = value; lock (SuppressedHistory) { SuppressedHistory.Add(value); } } }
    public float Level => 0;
    public bool FailOnOpen { get; set; }
    public int FinishCount { get; private set; }

    public event EventHandler<UtteranceEventArgs>? UtteranceCaptured;
    public event EventHandler<UtteranceEventArgs>? BargeInCaptured;
    public event EventHandler? BargeInSpeechStarted;
    public event EventHandler? SpeechStarted;
    public event EventHandler? CommandTimeout;
    public event EventHandler<AudioDeviceErrorEventArgs>? MicrophoneError;
    public bool BargeInEnabled { get; set; }
    public bool BargeInContinuation { get; set; }

    /// <summary>Simulates the user going on speaking while VEDAR recognizes the first part. False if not listening for it.</summary>
    public bool EmitContinuation(float seconds = 1.5f)
    {
        if (Mode == ListeningMode.Off || !Suppressed || !BargeInEnabled || !BargeInContinuation)
        {
            return false;
        }

        BargeInSpeechStarted?.Invoke(this, EventArgs.Empty);
        BargeInCaptured?.Invoke(this, new UtteranceEventArgs(new float[(int)(16000 * seconds)], Mode, true));
        return true;
    }

    /// <summary>Simulates a short piece of audio heard while VEDAR speaks. Returns false if it would not be reported.</summary>
    public bool EmitBargeIn(float seconds = 1.0f)
    {
        if (Mode == ListeningMode.Off || !Suppressed || !BargeInEnabled)
        {
            return false;
        }

        BargeInCaptured?.Invoke(this, new UtteranceEventArgs(new float[(int)(16000 * seconds)], Mode, true));
        return true;
    }

    public Task SetModeAsync(ListeningMode mode, CancellationToken cancellationToken)
    {
        if (mode != ListeningMode.Off && FailOnOpen)
        {
            throw new AudioDeviceException("No microphone (fake).");
        }

        Mode = mode;
        lock (ModeHistory) { ModeHistory.Add(mode); }
        return Task.CompletedTask;
    }

    public void FinishUtterance() { FinishCount++; CommandTimeout?.Invoke(this, EventArgs.Empty); }
    public Task RestartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Simulates a captured utterance. Returns false if the listener would have discarded it.</summary>
    public bool Emit(float seconds = 1.5f)
    {
        if (Mode == ListeningMode.Off || Suppressed)
        {
            return false;
        }

        SpeechStarted?.Invoke(this, EventArgs.Empty);
        UtteranceCaptured?.Invoke(this, new UtteranceEventArgs(new float[(int)(16000 * seconds)], Mode, true));
        return true;
    }

    public void RaiseTimeout() => CommandTimeout?.Invoke(this, EventArgs.Empty);
    public void RaiseError() => MicrophoneError?.Invoke(this, new AudioDeviceErrorEventArgs("fake error", null));
    public void Dispose() { }
}

/// <summary>Conversational AI fake with scripted replies, errors, delays and concurrency tracking.</summary>
public sealed class FakeConversationAi : IConversationAi
{
    public event EventHandler<AiFallbackEventArgs>? FallbackUsed;

    /// <summary>Simulates the router switching to a backup service before the next reply.</summary>
    public void RaiseFallback(string from, string to, AiErrorKind reason) => FallbackUsed?.Invoke(this, new AiFallbackEventArgs(from, to, true, reason));

    private int _active;
    public Queue<Func<IAsyncEnumerable<string>>> Scripts { get; } = new();
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
    public int MaxConcurrent { get; private set; }
    public bool Configured { get; set; } = true;
    public bool Cloud { get; set; } = true;
    public string ProviderName { get; set; } = "BgGPT";
    public string ActiveProviderName => ProviderName;
    public bool ActiveProviderIsCloud => Cloud;
    public bool IsConfigured => Configured;
    public AiStatus Status { get; set; } = new(AiAvailability.Available, "BgGPT", true, "ok");

    public Task<AiStatus> CheckStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Status);

    /// <summary>Status returned per service by <see cref="CheckProviderAsync"/> (default: available).</summary>
    public Func<string, AiStatus>? ProviderStatus { get; set; }
    public List<string> ProviderChecks { get; } = [];

    public Task<AiStatus> CheckProviderAsync(string providerName, CancellationToken cancellationToken)
    {
        lock (ProviderChecks) { ProviderChecks.Add(providerName); }
        return Task.FromResult(ProviderStatus?.Invoke(providerName) ?? new AiStatus(AiAvailability.Available, providerName, true, "ok"));
    }

    public void Reply(params string[] tokens) => Scripts.Enqueue(() => Tokens(tokens, TimeSpan.Zero));
    public void ReplySlowly(TimeSpan perToken, params string[] tokens) => Scripts.Enqueue(() => Tokens(tokens, perToken));
    public void Fail(AiErrorKind kind) => Scripts.Enqueue(() => Throw(kind));

    /// <summary>The main service fails and the router announces the backup before its answer.</summary>
    public void ReplyAfterFallback(string from, string to, params string[] tokens) => Scripts.Enqueue(() => FallbackThen(from, to, tokens));

    private async IAsyncEnumerable<string> FallbackThen(string from, string to, string[] tokens)
    {
        RaiseFallback(from, to, AiErrorKind.NoConnection);
        foreach (var t in tokens)
        {
            await Task.Yield();
            yield return t;
        }
    }

    private static async IAsyncEnumerable<string> Tokens(string[] tokens, TimeSpan delay, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var t in tokens)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            yield return t;
        }
    }

#pragma warning disable CS1998
    private static async IAsyncEnumerable<string> Throw(AiErrorKind kind)
    {
        throw new AiProviderException(kind, "fake " + kind);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
#pragma warning restore CS1998

    public async IAsyncEnumerable<string> StreamReplyAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var now = Interlocked.Increment(ref _active);
        MaxConcurrent = Math.Max(MaxConcurrent, now);
        try
        {
            lock (Requests) { Requests.Add(messages); }
            var script = Scripts.Count > 0 ? Scripts.Dequeue() : () => Tokens(["Добре."], TimeSpan.Zero);
            await foreach (var t in script().WithCancellation(cancellationToken))
            {
                yield return t;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

public static class Wait
{
    /// <summary>Polls a condition until it is true or the timeout expires.</summary>
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000, string? because = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time" + (because is null ? "." : ": " + because));
            }

            await Task.Delay(10);
        }
    }
}

public sealed class FakeAutoStart : IAutoStartManager
{
    public bool Enabled { get; private set; }
    public bool IsSupported => true;
    public bool IsEnabled() => Enabled;
    public bool IsDisabledByUser() => false;
    public void Enable() => Enabled = true;
    public void Disable() => Enabled = false;
}

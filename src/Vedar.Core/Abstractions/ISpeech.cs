namespace Vedar.Core.Abstractions;

public enum RecognitionPurpose
{
    /// <summary>Short utterance checked for the wake word (fast, small model).</summary>
    WakeWord,
    /// <summary>A command or question after activation.</summary>
    Command,
}

public sealed record RecognitionRequest(RecognitionPurpose Purpose, string? Prompt = null);

public sealed record RecognitionResult(string Text, float Confidence, TimeSpan AudioDuration, TimeSpan ProcessingTime)
{
    public static RecognitionResult Empty(TimeSpan audio, TimeSpan processing) => new(string.Empty, 0f, audio, processing);
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

/// <summary>Local speech-to-text.</summary>
public interface ISpeechRecognizer : IDisposable
{
    string EngineName { get; }
    bool IsReady { get; }
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Recognizes 16 kHz mono samples. Never blocks the caller's thread.</summary>
    Task<RecognitionResult> RecognizeAsync(float[] samples16k, RecognitionRequest request, CancellationToken cancellationToken);
}

public sealed record SynthesisOptions(float Rate = 1.0f);

public sealed record SynthesizedAudio(float[] Samples, int SampleRate, string Text);

/// <summary>Offline text-to-speech. Long text is synthesized and returned sentence by sentence.</summary>
public interface ISpeechSynthesizer : IDisposable
{
    string EngineName { get; }
    string VoiceName { get; }
    bool IsReady { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<SynthesizedAudio> SynthesizeAsync(string text, SynthesisOptions options, CancellationToken cancellationToken);
}

public enum ListeningMode
{
    /// <summary>Microphone closed.</summary>
    Off,
    /// <summary>Continuous local scanning for the wake word (hands-free).</summary>
    WakeWord,
    /// <summary>Capturing one command utterance (after wake word or push-to-talk).</summary>
    Command,
}

public sealed class UtteranceEventArgs(float[] samples, ListeningMode mode, bool endedBySilence) : EventArgs
{
    public float[] Samples { get; } = samples;
    public ListeningMode Mode { get; } = mode;
    public bool EndedBySilence { get; } = endedBySilence;
    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)AudioFrameEventArgs.SampleRate);
}

/// <summary>Captures audio and cuts it into utterances using voice activity detection.</summary>
public interface IVoiceListener : IDisposable
{
    ListeningMode Mode { get; }
    /// <summary>When true, captured audio is discarded (used while VEDAR is speaking to avoid hearing itself).</summary>
    bool Suppressed { get; set; }
    /// <summary>
    /// While <see cref="Suppressed"/>, still cut the audio into short pieces (at most 4 s) and report them through
    /// <see cref="BargeInCaptured"/> — only so that "Ведар, спри" can stop VEDAR while it speaks.
    /// </summary>
    bool BargeInEnabled { get; set; }
    /// <summary>Current input level 0..1 (for the microphone meter).</summary>
    float Level { get; }

    Task SetModeAsync(ListeningMode mode, CancellationToken cancellationToken);
    /// <summary>Ends the current command utterance immediately (second push-to-talk press).</summary>
    void FinishUtterance();
    /// <summary>Re-opens the microphone, e.g. after the device changed in settings.</summary>
    Task RestartAsync(CancellationToken cancellationToken);

    event EventHandler<UtteranceEventArgs>? UtteranceCaptured;
    /// <summary>A short piece of audio heard while suppressed (see <see cref="BargeInEnabled"/>).</summary>
    event EventHandler<UtteranceEventArgs>? BargeInCaptured;
    event EventHandler? SpeechStarted;
    /// <summary>No speech started within the command timeout.</summary>
    event EventHandler? CommandTimeout;
    event EventHandler<AudioDeviceErrorEventArgs>? MicrophoneError;
}

public sealed record WakeWordMatch(bool IsMatch, string Command, string MatchedToken)
{
    public static readonly WakeWordMatch None = new(false, string.Empty, string.Empty);
}

/// <summary>Decides whether a transcript starts with the wake word and extracts the command after it.</summary>
public interface IWakeWordMatcher
{
    WakeWordMatch Match(string transcript);

    /// <summary>True for a single (normalized) word that is the wake word or one of its usual misspellings.</summary>
    bool IsWakeToken(string token);
}

namespace Vedar.Core.State;

/// <summary>
/// The externally visible state of the assistant. Exactly one state is active at a time.
/// </summary>
public enum AssistantState
{
    /// <summary>Loading settings, checking microphone, loading speech models.</summary>
    Starting,
    /// <summary>Ready. In hands-free mode the microphone is scanned locally for the wake word.</summary>
    Idle,
    /// <summary>Actively listening for a user command (after wake word or push-to-talk).</summary>
    Listening,
    /// <summary>Converting captured speech to text locally (Whisper).</summary>
    Recognizing,
    /// <summary>Executing a command or waiting for the AI response.</summary>
    Processing,
    /// <summary>Speaking a response with the synthesized voice.</summary>
    Speaking,
    /// <summary>A recoverable error happened. The assistant returns to Idle automatically.</summary>
    Error,
    /// <summary>The assistant is paused or shutting down. No audio is captured.</summary>
    Stopped,
}

public sealed class AssistantStateChangedEventArgs(AssistantState previous, AssistantState current, string? detail) : EventArgs
{
    public AssistantState Previous { get; } = previous;
    public AssistantState Current { get; } = current;
    public string? Detail { get; } = detail;
}

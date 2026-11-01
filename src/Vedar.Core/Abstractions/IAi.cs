namespace Vedar.Core.Abstractions;

public enum ChatRole { System, User, Assistant }

public sealed record ChatMessage(ChatRole Role, string Content)
{
    public static ChatMessage System(string text) => new(ChatRole.System, text);
    public static ChatMessage User(string text) => new(ChatRole.User, text);
    public static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);
}

public enum AiErrorKind
{
    NotConfigured,
    MissingApiKey,
    Unauthorized,
    RateLimited,
    NoConnection,
    Timeout,
    ServerError,
    InvalidResponse,
    ModelNotFound,
    InsufficientResources,
    Busy,
}

public sealed class AiProviderException(AiErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public AiErrorKind Kind { get; } = kind;
    public bool IsTransient => Kind is AiErrorKind.RateLimited or AiErrorKind.ServerError or AiErrorKind.NoConnection or AiErrorKind.Timeout;
}

public enum AiAvailability
{
    Available,
    NotConfigured,
    MissingApiKey,
    Offline,
    Unauthorized,
    Unavailable,
}

public sealed record AiStatus(AiAvailability Availability, string ProviderName, bool IsCloud, string Detail)
{
    public bool IsAvailable => Availability == AiAvailability.Available;
}

/// <summary>Conversational AI used for free-form dialogue. Never executes commands.</summary>
public interface IConversationAi
{
    /// <summary>Human readable name of the provider that will be used, e.g. "BgGPT".</summary>
    string ActiveProviderName { get; }
    bool ActiveProviderIsCloud { get; }
    bool IsConfigured { get; }

    Task<AiStatus> CheckStatusAsync(CancellationToken cancellationToken);

    /// <summary>Streams the reply text. Throws <see cref="AiProviderException"/> on failure.</summary>
    IAsyncEnumerable<string> StreamReplyAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);
}

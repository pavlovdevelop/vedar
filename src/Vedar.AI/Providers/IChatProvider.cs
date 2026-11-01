using Vedar.Core.Abstractions;

namespace Vedar.AI.Providers;

/// <summary>One AI backend (BgGPT, Ollama). Text only; providers never execute anything.</summary>
public interface IChatProvider
{
    string Name { get; }
    bool IsCloud { get; }
    /// <summary>True when the provider has the configuration it needs (e.g. an API key).</summary>
    bool IsConfigured { get; }
    Task<AiStatus> CheckStatusAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);
}

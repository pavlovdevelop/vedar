using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Vedar.AI.Providers;
using Vedar.Core.Abstractions;
using Vedar.Core.Settings;

namespace Vedar.AI;

/// <summary>
/// Chooses the AI provider from the settings (None / BgGPT / Ollama / Auto). In Auto mode BgGPT is used when it
/// has a key; if it cannot be reached before the first token, Ollama is tried instead (if available).
/// </summary>
public sealed class AiRouter : IConversationAi
{
    private readonly ISettingsStore _settings;
    private readonly BgGptProvider _bggpt;
    private readonly OllamaProvider _ollama;
    private readonly ILogger<AiRouter> _logger;
    private IChatProvider? _lastUsed;

    public AiRouter(ISettingsStore settings, BgGptProvider bggpt, OllamaProvider ollama, ILogger<AiRouter> logger)
    {
        _settings = settings;
        _bggpt = bggpt;
        _ollama = ollama;
        _logger = logger;
    }

    private AiProviderKind Kind => _settings.Current.Ai.Provider;

    private IChatProvider? Primary => Kind switch
    {
        AiProviderKind.BgGpt => _bggpt,
        AiProviderKind.Ollama => _ollama,
        AiProviderKind.Auto => _bggpt.IsConfigured ? _bggpt : _ollama,
        _ => null,
    };

    public string ActiveProviderName => (_lastUsed is not null && Kind == AiProviderKind.Auto ? _lastUsed : Primary)?.Name ?? BgGptProvider.ProviderName;

    public bool ActiveProviderIsCloud => Primary?.IsCloud ?? true;

    public bool IsConfigured => Kind switch
    {
        AiProviderKind.None => false,
        AiProviderKind.BgGpt => _bggpt.IsConfigured,
        AiProviderKind.Ollama => _ollama.IsConfigured,
        AiProviderKind.Auto => _bggpt.IsConfigured || _ollama.IsConfigured,
        _ => false,
    };

    public async Task<AiStatus> CheckStatusAsync(CancellationToken cancellationToken)
    {
        var primary = Primary;
        if (primary is null)
        {
            return new AiStatus(AiAvailability.NotConfigured, BgGptProvider.ProviderName, true, "AI disabled.");
        }

        var status = await primary.CheckStatusAsync(cancellationToken).ConfigureAwait(false);
        if (Kind == AiProviderKind.Auto && !status.IsAvailable && primary == _bggpt)
        {
            var local = await _ollama.CheckStatusAsync(cancellationToken).ConfigureAwait(false);
            if (local.IsAvailable)
            {
                return local;
            }
        }

        return status;
    }

    public async IAsyncEnumerable<string> StreamReplyAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var primary = Primary ?? throw new AiProviderException(AiErrorKind.NotConfigured, "AI is disabled in the settings.");
        _lastUsed = primary;
        var fallback = Kind == AiProviderKind.Auto && primary == _bggpt ? _ollama : null;

        await using var enumerator = primary.StreamAsync(messages, cancellationToken).GetAsyncEnumerator(cancellationToken);
        bool first = true;
        while (true)
        {
            string token;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    yield break;
                }

                token = enumerator.Current;
            }
            catch (AiProviderException ex) when (first && fallback is not null && ex.Kind is AiErrorKind.NoConnection or AiErrorKind.Timeout or AiErrorKind.MissingApiKey)
            {
                _logger.LogInformation("BgGPT unavailable ({Kind}); falling back to Ollama.", ex.Kind);
                _lastUsed = fallback;
                break;
            }

            first = false;
            yield return token;
        }

        await foreach (var token in fallback!.StreamAsync(messages, cancellationToken).ConfigureAwait(false))
        {
            yield return token;
        }
    }
}

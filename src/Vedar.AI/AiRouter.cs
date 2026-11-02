using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Vedar.AI.Providers;
using Vedar.Core.Abstractions;
using Vedar.Core.Settings;

namespace Vedar.AI;

/// <summary>
/// Chooses the AI service from the settings (None / BgGPT / Groq / Ollama / Auto) and switches to a backup when the
/// main service fails before the first word of the answer:
/// <list type="bullet">
/// <item>BgGPT → Groq (if a Groq key is saved and the backup is enabled).</item>
/// <item>Auto: BgGPT → Groq → local Ollama.</item>
/// </list>
/// A partly spoken answer is never repeated by another service. The listener is told through
/// <see cref="FallbackUsed"/> so it can say which service answers.
/// </summary>
public sealed class AiRouter : IConversationAi
{
    private readonly ISettingsStore _settings;
    private readonly BgGptProvider _bggpt;
    private readonly GroqProvider? _groq;
    private readonly OllamaProvider _ollama;
    private readonly ILogger<AiRouter> _logger;
    private IChatProvider? _lastUsed;

    public AiRouter(ISettingsStore settings, BgGptProvider bggpt, OllamaProvider ollama, ILogger<AiRouter> logger, GroqProvider? groq = null)
    {
        _settings = settings;
        _bggpt = bggpt;
        _ollama = ollama;
        _logger = logger;
        _groq = groq;
    }

    public event EventHandler<AiFallbackEventArgs>? FallbackUsed;

    private AiSettings Ai => _settings.Current.Ai;
    private AiProviderKind Kind => Ai.Provider;

    private bool GroqBackupReady => _groq is not null && Ai.UseGroqFallback && _groq.IsConfigured;

    /// <summary>The services to try, in order, for the current settings.</summary>
    internal IReadOnlyList<IChatProvider> Chain()
    {
        var chain = new List<IChatProvider>();
        switch (Kind)
        {
            case AiProviderKind.BgGpt:
                // Without a BgGPT key, a saved Groq key still gives the user conversations.
                if (_bggpt.IsConfigured || !GroqBackupReady)
                {
                    chain.Add(_bggpt);
                }

                if (GroqBackupReady)
                {
                    chain.Add(_groq!);
                }

                break;
            case AiProviderKind.Groq when _groq is not null:
                chain.Add(_groq);
                break;
            case AiProviderKind.Ollama:
                chain.Add(_ollama);
                break;
            case AiProviderKind.Auto:
                if (_bggpt.IsConfigured)
                {
                    chain.Add(_bggpt);
                }

                if (GroqBackupReady)
                {
                    chain.Add(_groq!);
                }

                chain.Add(_ollama);
                break;
        }

        return chain;
    }

    private IChatProvider? Primary => Chain().FirstOrDefault();

    public string ActiveProviderName => (_lastUsed is not null && Chain().Contains(_lastUsed) ? _lastUsed : Primary)?.Name ?? BgGptProvider.ProviderName;

    public bool ActiveProviderIsCloud => Primary?.IsCloud ?? true;

    public bool IsConfigured => Kind switch
    {
        AiProviderKind.None => false,
        AiProviderKind.Auto => _bggpt.IsConfigured || GroqBackupReady || _ollama.IsConfigured,
        _ => Chain().Any(p => p.IsConfigured),
    };

    /// <summary>Checks one service by name (used by the "check connection" buttons in the settings).</summary>
    public Task<AiStatus> CheckProviderAsync(string providerName, CancellationToken cancellationToken)
    {
        IChatProvider? provider = providerName switch
        {
            BgGptProvider.ProviderName => _bggpt,
            GroqProvider.ProviderName => _groq,
            _ => _ollama,
        };
        return provider is null
            ? Task.FromResult(new AiStatus(AiAvailability.NotConfigured, providerName, true, "Not available."))
            : provider.CheckStatusAsync(cancellationToken);
    }

    public async Task<AiStatus> CheckStatusAsync(CancellationToken cancellationToken)
    {
        var chain = Chain();
        if (chain.Count == 0)
        {
            return new AiStatus(AiAvailability.NotConfigured, BgGptProvider.ProviderName, true, "AI disabled.");
        }

        var first = await chain[0].CheckStatusAsync(cancellationToken).ConfigureAwait(false);
        if (first.IsAvailable)
        {
            return first;
        }

        foreach (var backup in chain.Skip(1))
        {
            var status = await backup.CheckStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.IsAvailable)
            {
                return status with { Detail = $"{chain[0].Name}: {first.Availability}. {status.Detail}" };
            }
        }

        return first;
    }

    public async IAsyncEnumerable<string> StreamReplyAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chain = Chain();
        if (chain.Count == 0)
        {
            throw new AiProviderException(AiErrorKind.NotConfigured, "AI is disabled in the settings.");
        }

        for (int i = 0; i < chain.Count; i++)
        {
            var provider = chain[i];
            var isLast = i == chain.Count - 1;
            _lastUsed = provider;
            var request = i == 0 ? messages : ForProvider(messages, chain[0].Name, provider.Name);

            await using var enumerator = provider.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            bool first = true;
            AiProviderException? failure = null;
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
                catch (AiProviderException ex) when (first && !isLast && ex.Kind != AiErrorKind.Busy && !cancellationToken.IsCancellationRequested)
                {
                    failure = ex;
                    break;
                }

                first = false;
                yield return token;
            }

            var next = chain[i + 1];
            _logger.LogInformation("{Provider} did not answer ({Kind}: {Message}); using {Backup}.", provider.Name, failure!.Kind, failure.Message, next.Name);
            FallbackUsed?.Invoke(this, new AiFallbackEventArgs(provider.Name, next.Name, next.IsCloud, failure.Kind));
        }
    }

    /// <summary>The system prompt names the service; a backup must not claim to be the main one.</summary>
    private static IReadOnlyList<ChatMessage> ForProvider(IReadOnlyList<ChatMessage> messages, string from, string to) =>
        messages.Select(m => m.Role == ChatRole.System ? m with { Content = m.Content.Replace(from, to, StringComparison.Ordinal) } : m).ToList();
}

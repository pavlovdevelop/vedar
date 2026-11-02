using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Vedar.AI.OpenAi;
using Vedar.Core.Abstractions;
using Vedar.Core.Settings;

namespace Vedar.AI.Providers;

/// <summary>
/// A cloud service with an OpenAI-compatible Chat Completions API and a personal API key (BgGPT, Groq).
/// The key is read from the protected secret store at request time; it is never cached in settings.
/// Only the conversation text is sent — no audio, files or personal data.
/// </summary>
public abstract class CloudChatProvider : IChatProvider
{
    private readonly OpenAiCompatibleChatClient _client;
    private readonly ISecretStore _secrets;

    protected CloudChatProvider(OpenAiCompatibleChatClient client, ISettingsStore settings, ISecretStore secrets, ILogger logger)
    {
        _client = client;
        Settings = settings;
        _secrets = secrets;
        Logger = logger;
    }

    protected ISettingsStore Settings { get; }
    protected ILogger Logger { get; }

    public abstract string Name { get; }
    public bool IsCloud => true;

    /// <summary>Name of the protected secret that holds the API key.</summary>
    protected abstract string SecretName { get; }
    protected abstract string BaseUrl(AiSettings ai);
    public abstract string Model(AiSettings ai);

    /// <summary>Additional request fields, e.g. reasoning settings of a reasoning model.</summary>
    protected virtual IReadOnlyDictionary<string, object>? ExtraParameters => null;

    /// <summary>Extra completion tokens for models that spend tokens on hidden reasoning.</summary>
    protected virtual int ReasoningTokenAllowance => 0;

    /// <summary>Tokens for the connection check (reasoning models need a few to answer at all).</summary>
    protected virtual int ProbeTokens => 1;

    public bool IsConfigured
    {
        get
        {
            try
            {
                return _secrets.IsSupported && _secrets.Exists(SecretName);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private string? ApiKey()
    {
        try
        {
            // Also repairs keys saved by earlier versions with a pasted "Bearer " prefix, quotes or line breaks.
            var key = ApiKeyText.Normalize(_secrets.Get(SecretName));
            return key.Length == 0 ? null : key;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not read the {Provider} key.", Name);
            return null;
        }
    }

    public async Task<AiStatus> CheckStatusAsync(CancellationToken cancellationToken)
    {
        var key = ApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return new AiStatus(AiAvailability.MissingApiKey, Name, true, "No API key.");
        }

        // A tiny request to the documented POST /chat/completions checks the address, the key and the model.
        // (GET /models is not documented by every service and may be refused for ordinary keys.)
        var ai = Settings.Current.Ai;
        try
        {
            var endpoint = Endpoint(key) with { MaxRetries = 0, FirstTokenTimeout = TimeSpan.FromSeconds(20) };
            var probe = new ChatCompletionRequest(Model(ai), [ChatMessage.User("Здравей")], ProbeTokens, 0f, ExtraParameters);
            await foreach (var _ in WithoutExtrasIfRejectedAsync(endpoint, probe, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            Logger.LogInformation("{Provider} connection check succeeded ({Model}).", Name, Model(ai));
            return new AiStatus(AiAvailability.Available, Name, true, Model(ai));
        }
        catch (AiProviderException ex)
        {
            Logger.LogWarning("{Provider} connection check failed: {Kind}. {Message}", Name, ex.Kind, ex.Message);
            return new AiStatus(ToAvailability(ex.Kind), Name, true, ex.Message);
        }
    }

    internal static AiAvailability ToAvailability(AiErrorKind kind) => kind switch
    {
        AiErrorKind.Unauthorized => AiAvailability.Unauthorized,
        AiErrorKind.NoConnection => AiAvailability.Offline,
        AiErrorKind.MissingApiKey => AiAvailability.MissingApiKey,
        AiErrorKind.NotConfigured => AiAvailability.NotConfigured,
        _ => AiAvailability.Unavailable,
    };

    private ChatEndpoint Endpoint(string key)
    {
        var ai = Settings.Current.Ai;
        return new ChatEndpoint(
            OpenAiCompatibleChatClient.Combine(BaseUrl(ai), "chat/completions"),
            key,
            Name,
            FirstTokenTimeout: TimeSpan.FromSeconds(Math.Min(45, ai.RequestTimeoutSeconds)),
            TotalTimeout: TimeSpan.FromSeconds(ai.RequestTimeoutSeconds));
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var key = ApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new AiProviderException(AiErrorKind.MissingApiKey, $"{Name} API key is not configured.");
        }

        var ai = Settings.Current.Ai;
        var request = new ChatCompletionRequest(Model(ai), messages, ai.MaxResponseTokens + ReasoningTokenAllowance, ai.Temperature, ExtraParameters);
        await foreach (var token in WithoutExtrasIfRejectedAsync(Endpoint(key), request, cancellationToken).ConfigureAwait(false))
        {
            yield return token;
        }
    }

    /// <summary>
    /// Streams the request. If the service rejects it as a bad request before answering and optional extra fields
    /// were sent (e.g. reasoning settings a model does not accept), it is sent once more without them.
    /// </summary>
    private async IAsyncEnumerable<string> WithoutExtrasIfRejectedAsync(ChatEndpoint endpoint, ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        bool any = false;
        await using (var e = _client.StreamAsync(endpoint, request, cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await e.MoveNextAsync().ConfigureAwait(false);
                }
                catch (AiProviderException ex) when (!any && request.Extra is { Count: > 0 } && ex.Kind == AiErrorKind.InvalidResponse && ex.Message.Contains("bad request", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogWarning("{Provider} rejected the optional request fields ({Message}); retrying without them.", Name, ex.Message);
                    break;
                }

                if (!moved)
                {
                    yield break;
                }

                any = true;
                yield return e.Current;
            }
        }

        await foreach (var token in _client.StreamAsync(endpoint, request with { Extra = null }, cancellationToken).ConfigureAwait(false))
        {
            yield return token;
        }
    }
}

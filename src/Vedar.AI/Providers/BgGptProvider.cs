using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Vedar.AI.OpenAi;
using Vedar.Core.Abstractions;

namespace Vedar.AI.Providers;

/// <summary>
/// BgGPT (https://models.bggpt.ai/docs/), an OpenAI-compatible cloud API.
/// The API key is read from the protected secret store at request time; it is never cached in settings.
/// Only the conversation text is sent — no audio, files or personal data.
/// </summary>
public sealed class BgGptProvider : IChatProvider
{
    public const string ProviderName = "BgGPT";
    private readonly OpenAiCompatibleChatClient _client;
    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly ILogger<BgGptProvider> _logger;

    public BgGptProvider(OpenAiCompatibleChatClient client, ISettingsStore settings, ISecretStore secrets, ILogger<BgGptProvider> logger)
    {
        _client = client;
        _settings = settings;
        _secrets = secrets;
        _logger = logger;
    }

    public string Name => ProviderName;
    public bool IsCloud => true;

    public bool IsConfigured
    {
        get
        {
            try
            {
                return _secrets.IsSupported && _secrets.Exists(SecretNames.BgGptApiKey);
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
            var key = ApiKeyText.Normalize(_secrets.Get(SecretNames.BgGptApiKey));
            return key.Length == 0 ? null : key;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the BgGPT key.");
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

        // Only POST /chat/completions is documented by BgGPT; /models may be refused for ordinary keys, which
        // would wrongly look like a rejected key. A 1-token request checks the address, the key and the model.
        var ai = _settings.Current.Ai;
        try
        {
            var endpoint = Endpoint(key) with { MaxRetries = 0, FirstTokenTimeout = TimeSpan.FromSeconds(20) };
            await foreach (var _ in _client.StreamAsync(endpoint, new ChatCompletionRequest(ai.BgGptModel, [ChatMessage.User("Здравей")], 1, 0f), cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            _logger.LogInformation("BgGPT connection check succeeded ({Model}).", ai.BgGptModel);
            return new AiStatus(AiAvailability.Available, Name, true, ai.BgGptModel);
        }
        catch (AiProviderException ex)
        {
            _logger.LogWarning("BgGPT connection check failed: {Kind}. {Message}", ex.Kind, ex.Message);
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
        var ai = _settings.Current.Ai;
        return new ChatEndpoint(
            OpenAiCompatibleChatClient.Combine(ai.BgGptEndpoint, "chat/completions"),
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
            throw new AiProviderException(AiErrorKind.MissingApiKey, "BgGPT API key is not configured.");
        }

        var ai = _settings.Current.Ai;
        var request = new ChatCompletionRequest(ai.BgGptModel, messages, ai.MaxResponseTokens, ai.Temperature);
        await foreach (var token in _client.StreamAsync(Endpoint(key), request, cancellationToken).ConfigureAwait(false))
        {
            yield return token;
        }
    }
}

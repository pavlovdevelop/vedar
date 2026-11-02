using Microsoft.Extensions.Logging;
using Vedar.AI.OpenAi;
using Vedar.Core.Abstractions;
using Vedar.Core.Settings;

namespace Vedar.AI.Providers;

/// <summary>
/// Groq (https://console.groq.com), used as a free backup when BgGPT does not answer. A free Groq account gives
/// an API key; the default model <c>openai/gpt-oss-120b</c> is on Groq's free plan (30 requests per minute,
/// 1000 per day — November 2026). It is an open-weights model run by Groq; it is not ChatGPT.
/// </summary>
public sealed class GroqProvider(OpenAiCompatibleChatClient client, ISettingsStore settings, ISecretStore secrets, ILogger<GroqProvider> logger)
    : CloudChatProvider(client, settings, secrets, logger)
{
    public const string ProviderName = "Groq";

    private static readonly IReadOnlyDictionary<string, object> ReasoningOff = new Dictionary<string, object>
    {
        // GPT-OSS always reasons first; keep it short and leave the reasoning out of the answer that is read aloud.
        ["reasoning_effort"] = "low",
        ["include_reasoning"] = false,
    };

    public override string Name => ProviderName;
    protected override string SecretName => SecretNames.GroqApiKey;
    protected override string BaseUrl(AiSettings ai) => ai.GroqEndpoint;
    public override string Model(AiSettings ai) => ai.GroqModel;

    private bool IsGptOss => Settings.Current.Ai.GroqModel.StartsWith("openai/gpt-oss", StringComparison.OrdinalIgnoreCase);

    protected override IReadOnlyDictionary<string, object>? ExtraParameters => IsGptOss ? ReasoningOff : null;
    protected override int ReasoningTokenAllowance => IsGptOss ? 512 : 0;
    protected override int ProbeTokens => IsGptOss ? 16 : 1;
}

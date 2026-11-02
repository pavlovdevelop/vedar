using Microsoft.Extensions.Logging;
using Vedar.AI.OpenAi;
using Vedar.Core.Abstractions;
using Vedar.Core.Settings;

namespace Vedar.AI.Providers;

/// <summary>BgGPT (https://models.bggpt.ai/docs/), the Bulgarian language model, through its OpenAI-compatible API.</summary>
public sealed class BgGptProvider(OpenAiCompatibleChatClient client, ISettingsStore settings, ISecretStore secrets, ILogger<BgGptProvider> logger)
    : CloudChatProvider(client, settings, secrets, logger)
{
    public const string ProviderName = "BgGPT";

    public override string Name => ProviderName;
    protected override string SecretName => SecretNames.BgGptApiKey;
    protected override string BaseUrl(AiSettings ai) => ai.BgGptEndpoint;
    public override string Model(AiSettings ai) => ai.BgGptModel;
}

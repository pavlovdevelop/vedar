using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vedar.AI.OpenAi;
using Vedar.Core.Abstractions;
using Vedar.Infrastructure.SystemInfo;

namespace Vedar.AI.Providers;

public sealed record OllamaInfo(bool Installed, bool Running, string? Version, IReadOnlyList<string> Models, ResourceReport Resources);

/// <summary>
/// Optional local AI through Ollama (https://ollama.com). Not required for any core VEDAR function.
/// Uses Ollama's OpenAI-compatible endpoint (/v1/chat/completions) and its native /api/tags and /api/version.
/// </summary>
public sealed class OllamaProvider : IChatProvider
{
    public const string ProviderName = "Ollama";
    private readonly OpenAiCompatibleChatClient _client;
    private readonly HttpClient _http;
    private readonly ISettingsStore _settings;
    private readonly ILogger<OllamaProvider> _logger;
    private readonly Func<ResourceReport> _resources;
    private readonly Func<bool> _isInstalled;

    public OllamaProvider(OpenAiCompatibleChatClient client, HttpClient http, ISettingsStore settings, ILogger<OllamaProvider> logger,
        Func<ResourceReport>? resources = null, Func<bool>? isInstalled = null)
    {
        _client = client;
        _http = http;
        _settings = settings;
        _logger = logger;
        _resources = resources ?? (() => SystemResources.Evaluate());
        _isInstalled = isInstalled ?? IsInstalledLocally;
    }

    public string Name => string.IsNullOrWhiteSpace(_settings.Current.Ai.OllamaModel) ? ProviderName : $"{ProviderName} ({_settings.Current.Ai.OllamaModel})";
    public bool IsCloud => false;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.Current.Ai.OllamaEndpoint);

    /// <summary>Looks for the Ollama executable in the standard Windows install locations and on PATH.</summary>
    public static bool IsInstalledLocally()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir, OperatingSystem.IsWindows() ? "ollama.exe" : "ollama"));
        }

        return candidates.Any(File.Exists);
    }

    public async Task<OllamaInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        var resources = _resources();
        var baseUrl = _settings.Current.Ai.OllamaEndpoint;
        string? version = null;
        var models = new List<string>();
        bool running = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using (var v = await _http.GetAsync(OpenAiCompatibleChatClient.Combine(baseUrl, "api/version"), timeout.Token).ConfigureAwait(false))
            {
                if (v.IsSuccessStatusCode)
                {
                    running = true;
                    using var doc = JsonDocument.Parse(await v.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                    version = doc.RootElement.TryGetProperty("version", out var ver) ? ver.GetString() : null;
                }
            }

            if (running)
            {
                using var tags = await _http.GetAsync(OpenAiCompatibleChatClient.Combine(baseUrl, "api/tags"), timeout.Token).ConfigureAwait(false);
                if (tags.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await tags.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                    if (doc.RootElement.TryGetProperty("models", out var arr))
                    {
                        models.AddRange(arr.EnumerateArray().Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>());
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Ollama not reachable at {Url}.", baseUrl);
        }

        return new OllamaInfo(running || _isInstalled(), running, version, models, resources);
    }

    public async Task<AiStatus> CheckStatusAsync(CancellationToken cancellationToken)
    {
        var info = await GetInfoAsync(cancellationToken).ConfigureAwait(false);
        if (!info.Resources.SufficientForLocalAi)
        {
            return new AiStatus(AiAvailability.Unavailable, Name, false, info.Resources.Explanation);
        }

        if (!info.Running)
        {
            return new AiStatus(AiAvailability.Unavailable, Name, false, info.Installed ? "Ollama is not running." : "Ollama is not installed.");
        }

        var model = ResolveModel(info.Models);
        return model is null
            ? new AiStatus(AiAvailability.NotConfigured, Name, false, "No Ollama model is installed.")
            : new AiStatus(AiAvailability.Available, Name, false, model);
    }

    private string? ResolveModel(IReadOnlyList<string> installed)
    {
        var configured = _settings.Current.Ai.OllamaModel;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return installed.FirstOrDefault();
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resources = _resources();
        if (!resources.SufficientForLocalAi)
        {
            throw new AiProviderException(AiErrorKind.InsufficientResources, resources.Explanation);
        }

        var ai = _settings.Current.Ai;
        var model = ai.OllamaModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            var info = await GetInfoAsync(cancellationToken).ConfigureAwait(false);
            model = ResolveModel(info.Models) ?? throw new AiProviderException(info.Running ? AiErrorKind.ModelNotFound : AiErrorKind.NoConnection, "No Ollama model available.");
        }

        var endpoint = new ChatEndpoint(
            OpenAiCompatibleChatClient.Combine(ai.OllamaEndpoint, "v1/chat/completions"),
            null,
            ProviderName,
            FirstTokenTimeout: TimeSpan.FromSeconds(Math.Max(60, ai.RequestTimeoutSeconds)), // local models load slowly the first time
            TotalTimeout: TimeSpan.FromSeconds(Math.Max(120, ai.RequestTimeoutSeconds)),
            MaxRetries: 1);
        await foreach (var token in _client.StreamAsync(endpoint, new ChatCompletionRequest(model, messages, ai.MaxResponseTokens, ai.Temperature), cancellationToken).ConfigureAwait(false))
        {
            yield return token;
        }
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Vedar.Core.Abstractions;

namespace Vedar.AI.OpenAi;

public sealed record ChatEndpoint(
    Uri ChatCompletionsUrl,
    string? ApiKey,
    string ProviderName,
    TimeSpan FirstTokenTimeout,
    TimeSpan TotalTimeout,
    int MaxRetries = 2);

public sealed record ChatCompletionRequest(string Model, IReadOnlyList<ChatMessage> Messages, int MaxTokens, float Temperature);

/// <summary>
/// Client for any OpenAI-compatible Chat Completions API (BgGPT, Ollama's /v1 endpoint).
/// <list type="bullet">
/// <item>Streams tokens (SSE) and falls back to a normal JSON response if the server does not stream.</item>
/// <item>Retries transient failures (network, 429, 5xx) with backoff, but only before the first token, so a
/// partly spoken answer is never repeated.</item>
/// <item>Separate timeouts for the first token and for the whole answer; user cancellation is immediate.</item>
/// <item>Maps every failure to an <see cref="AiProviderException"/> with a specific <see cref="AiErrorKind"/>.</item>
/// </list>
/// The API key is only placed in the Authorization header and is never logged.
/// </summary>
public sealed class OpenAiCompatibleChatClient
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly Func<int, TimeSpan> _backoff;

    public OpenAiCompatibleChatClient(HttpClient http, ILogger logger, Func<int, TimeSpan>? backoff = null)
    {
        _http = http;
        _logger = logger;
        _backoff = backoff ?? (attempt => TimeSpan.FromMilliseconds(500 * Math.Pow(3, attempt - 1)));
    }

    public async IAsyncEnumerable<string> StreamAsync(ChatEndpoint endpoint, ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var producer = Task.Run(() => ProduceAsync(endpoint, request, channel.Writer, cancellationToken), CancellationToken.None);
        await foreach (var token in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return token;
        }

        await producer.ConfigureAwait(false);
    }

    private async Task ProduceAsync(ChatEndpoint endpoint, ChatCompletionRequest request, ChannelWriter<string> writer, CancellationToken userToken)
    {
        Exception? failure = null;
        try
        {
            var body = ChatJson.BuildRequest(request.Model, request.Messages, request.MaxTokens, request.Temperature, stream: true);
            for (int attempt = 0; ; attempt++)
            {
                bool anyToken = false;
                try
                {
                    await RunAttemptAsync(endpoint, body, writer, () => anyToken = true, userToken).ConfigureAwait(false);
                    return;
                }
                catch (AiProviderException ex) when (ex.IsTransient && !anyToken && attempt < endpoint.MaxRetries && !userToken.IsCancellationRequested)
                {
                    var delay = ex.Kind == AiErrorKind.RateLimited && ex.Data["RetryAfter"] is TimeSpan ra && ra <= TimeSpan.FromSeconds(5) ? ra : _backoff(attempt + 1);
                    _logger.LogInformation("{Provider} request failed ({Kind}); retry {Attempt} in {Delay} ms.", endpoint.ProviderName, ex.Kind, attempt + 1, (int)delay.TotalMilliseconds);
                    await Task.Delay(delay, userToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    private async Task RunAttemptAsync(ChatEndpoint endpoint, string body, ChannelWriter<string> writer, Action onFirstToken, CancellationToken userToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(userToken);
        timeout.CancelAfter(endpoint.FirstTokenTimeout);
        var sw = Stopwatch.StartNew();
        bool first = true;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint.ChatCompletionsUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
            }

            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await SafeReadAsync(response, timeout.Token).ConfigureAwait(false);
                throw MapStatus(response, errorBody, endpoint.ProviderName);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var json = await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                var text = ParseOrThrow(() => ChatJson.ParseCompletion(json), endpoint.ProviderName);
                if (!string.IsNullOrEmpty(text))
                {
                    onFirstToken();
                    await writer.WriteAsync(text, userToken).ConfigureAwait(false);
                }

                return;
            }

            await foreach (var data in SseParser.ReadEventsAsync(stream, timeout.Token).ConfigureAwait(false))
            {
                if (data.Trim() == "[DONE]")
                {
                    break;
                }

                string? errorMessage = null;
                var delta = ParseOrThrow(() => ChatJson.ParseDelta(data, out errorMessage), endpoint.ProviderName);
                if (errorMessage is not null)
                {
                    throw new AiProviderException(AiErrorKind.ServerError, $"{endpoint.ProviderName}: {errorMessage}");
                }

                if (string.IsNullOrEmpty(delta))
                {
                    continue;
                }

                if (first)
                {
                    first = false;
                    onFirstToken();
                    var remaining = endpoint.TotalTimeout - sw.Elapsed;
                    timeout.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
                    _logger.LogDebug("{Provider} first token after {Ms} ms.", endpoint.ProviderName, sw.ElapsedMilliseconds);
                }

                await writer.WriteAsync(delta, userToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
        {
            throw new AiProviderException(AiErrorKind.Timeout, $"{endpoint.ProviderName} did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            throw MapNetworkError(ex, endpoint.ProviderName);
        }
        catch (IOException ex) when (!userToken.IsCancellationRequested)
        {
            throw new AiProviderException(first ? AiErrorKind.NoConnection : AiErrorKind.ServerError, $"{endpoint.ProviderName}: connection lost.", ex);
        }
    }

    private static string? ParseOrThrow(Func<string?> parse, string provider)
    {
        try
        {
            return parse();
        }
        catch (JsonException ex)
        {
            throw new AiProviderException(AiErrorKind.InvalidResponse, $"{provider} returned invalid JSON.", ex);
        }
    }

    private static async Task<string?> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Length > 2000 ? text[..2000] : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static AiProviderException MapStatus(HttpResponseMessage response, string? body, string provider)
    {
        var detail = ChatJson.ExtractErrorMessage(body);
        var status = (int)response.StatusCode;
        var ex = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AiProviderException(AiErrorKind.Unauthorized, $"{provider}: access denied (HTTP {status}). {detail}".TrimEnd()),
            HttpStatusCode.TooManyRequests => new AiProviderException(AiErrorKind.RateLimited, $"{provider}: rate limited (HTTP 429). {detail}".TrimEnd()),
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => new AiProviderException(AiErrorKind.Timeout, $"{provider}: timeout ({status})."),
            HttpStatusCode.NotFound when detail?.Contains("model", StringComparison.OrdinalIgnoreCase) == true =>
                new AiProviderException(AiErrorKind.ModelNotFound, $"{provider}: model not found. {detail}"),
            HttpStatusCode.NotFound => new AiProviderException(AiErrorKind.NotConfigured, $"{provider}: endpoint not found (404). Check the address."),
            HttpStatusCode.BadRequest when detail?.Contains("model", StringComparison.OrdinalIgnoreCase) == true =>
                new AiProviderException(AiErrorKind.ModelNotFound, $"{provider}: {detail}"),
            HttpStatusCode.BadRequest => new AiProviderException(AiErrorKind.InvalidResponse, $"{provider}: bad request. {detail}"),
            _ when status >= 500 => new AiProviderException(AiErrorKind.ServerError, $"{provider}: server error {status}."),
            _ => new AiProviderException(AiErrorKind.InvalidResponse, $"{provider}: unexpected status {status}. {detail}"),
        };

        if (response.Headers.RetryAfter is { } retryAfter)
        {
            var delay = retryAfter.Delta ?? (retryAfter.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            if (delay is { } d && d > TimeSpan.Zero)
            {
                ex.Data["RetryAfter"] = d;
            }
        }

        return ex;
    }

    internal static AiProviderException MapNetworkError(HttpRequestException ex, string provider)
    {
        var socket = ex.InnerException as SocketException ?? ex.InnerException?.InnerException as SocketException;
        var kind = ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError => AiErrorKind.NoConnection,
            HttpRequestError.SecureConnectionError => AiErrorKind.NoConnection,
            _ when socket is not null => AiErrorKind.NoConnection,
            _ when ex.StatusCode is { } code && (int)code >= 500 => AiErrorKind.ServerError,
            _ => AiErrorKind.NoConnection,
        };
        var reason = (socket?.Message ?? ex.InnerException?.Message ?? ex.Message).Trim();
        return new AiProviderException(kind, $"{provider}: cannot connect ({ex.HttpRequestError}: {reason}).", ex);
    }

    /// <summary>GET {base}/models. Returns the model ids, or throws a mapped exception.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(Uri modelsUrl, string? apiKey, string provider, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await _http.SendAsync(message, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw MapStatus(response, body, provider);
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                return data.EnumerateArray().Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null).OfType<string>().ToList();
            }

            return [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(AiErrorKind.Timeout, $"{provider} did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            throw MapNetworkError(ex, provider);
        }
        catch (JsonException ex)
        {
            throw new AiProviderException(AiErrorKind.InvalidResponse, $"{provider} returned invalid JSON.", ex);
        }
    }

    /// <summary>Joins a base URL ("https://api.bggpt.ai/v1") with a relative path ("chat/completions").</summary>
    public static Uri Combine(string baseUrl, string relative)
    {
        var b = baseUrl.Trim();
        if (!b.EndsWith('/'))
        {
            b += "/";
        }

        return new Uri(new Uri(b, UriKind.Absolute), relative.TrimStart('/'));
    }

    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VEDAR/0.1.0");
        return client;
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vedar.AI;
using Vedar.AI.OpenAi;
using Vedar.AI.Providers;
using Vedar.Core.Abstractions;
using Vedar.Core.Settings;
using Vedar.Infrastructure.Security;
using Vedar.Infrastructure.SystemInfo;
using Vedar.Tests.Support;

namespace Vedar.Tests.AI;

/// <summary>Stream that returns data in tiny, irregular pieces (simulates network fragmentation).</summary>
internal sealed class TrickleStream(byte[] data, int[] pattern) : Stream
{
    private int _pos;
    private int _i;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos >= data.Length)
        {
            return 0;
        }

        var n = Math.Min(Math.Min(count, pattern[_i++ % pattern.Length]), data.Length - _pos);
        Array.Copy(data, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }
}

public class SseParserTests
{
    private static async Task<List<string>> ReadAll(string raw, params int[] pattern)
    {
        var stream = new TrickleStream(Encoding.UTF8.GetBytes(raw), pattern.Length == 0 ? [3, 1, 7, 2] : pattern);
        var result = new List<string>();
        await foreach (var e in SseParser.ReadEventsAsync(stream, CancellationToken.None))
        {
            result.Add(e);
        }

        return result;
    }

    [Fact]
    public async Task Parses_events_split_across_reads_and_crlf()
    {
        var events = await ReadAll("data: {\"a\":1}\r\n\r\n: keep-alive\n\ndata: здравей\n\ndata: [DONE]\n\n");
        Assert.Equal(["{\"a\":1}", "здравей", "[DONE]"], events);
    }

    [Fact]
    public async Task Joins_multi_line_data_and_ignores_other_fields()
    {
        var events = await ReadAll("event: message\nid: 7\ndata: първи\ndata: втори\nretry: 100\n\n", 1);
        Assert.Equal(["първи\nвтори"], events);
    }

    [Fact]
    public async Task Emits_last_event_without_trailing_blank_line() =>
        Assert.Equal(["край"], await ReadAll("data: край"));
}

public class ChatJsonTests
{
    [Fact]
    public void Request_contains_model_messages_limits_and_stream_flag()
    {
        var json = ChatJson.BuildRequest("bggpt-gemma-3-27b-fp8", [ChatMessage.System("с"), ChatMessage.User("здравей")], 400, 0.4f, true);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("bggpt-gemma-3-27b-fp8", root.GetProperty("model").GetString());
        Assert.Equal(400, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("здравей", root.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("{\"choices\":[{\"delta\":{\"content\":\"Здра\"}}]}", "Здра")]
    [InlineData("{\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}", null)]
    [InlineData("{\"choices\":[{\"delta\":{\"content\":null},\"finish_reason\":\"stop\"}]}", null)]
    [InlineData("{\"choices\":[]}", null)]
    public void ParseDelta(string json, string? expected) => Assert.Equal(expected, ChatJson.ParseDelta(json, out _));

    [Fact]
    public void ParseDelta_reports_stream_errors()
    {
        ChatJson.ParseDelta("{\"error\":{\"message\":\"overloaded\"}}", out var error);
        Assert.Equal("overloaded", error);
    }
}

public class OpenAiClientTests : IAsyncLifetime
{
    private MockHttpServer _server = null!;
    private readonly CapturingLogger _log = new();

    public async ValueTask InitializeAsync() => _server = await MockHttpServer.StartAsync();

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private OpenAiCompatibleChatClient Client() => new(OpenAiCompatibleChatClient.CreateHttpClient(), _log, _ => TimeSpan.FromMilliseconds(50));

    private ChatEndpoint Endpoint(string key = "test-key-1234567890", double firstTokenSeconds = 5, int retries = 2) =>
        new(OpenAiCompatibleChatClient.Combine(_server.BaseUrl + "/v1", "chat/completions"), key, "BgGPT", TimeSpan.FromSeconds(firstTokenSeconds), TimeSpan.FromSeconds(30), retries);

    private static ChatCompletionRequest Request() => new("bggpt-gemma-3-27b-fp8", [ChatMessage.User("Здравей")], 100, 0.2f);

    private static async Task<string> Collect(IAsyncEnumerable<string> stream)
    {
        var sb = new StringBuilder();
        await foreach (var t in stream)
        {
            sb.Append(t);
        }

        return sb.ToString();
    }

    [Fact]
    public async Task Streams_tokens_and_sends_bearer_key_to_chat_completions()
    {
        _server.Handler = (ctx, _) => MockHttpServer.WriteSseAsync(ctx, ["Здравей", ", ", "аз съм ", "Ведар."]);
        var text = await Collect(Client().StreamAsync(Endpoint(), Request(), CancellationToken.None));
        Assert.Equal("Здравей, аз съм Ведар.", text);
        var req = Assert.Single(_server.Requests);
        Assert.Equal("POST", req.Method);
        Assert.Equal("/v1/chat/completions", req.Path);
        Assert.Equal("Bearer test-key-1234567890", req.Authorization);
        Assert.Contains("\"stream\":true", req.Body);
    }

    [Fact]
    public async Task Falls_back_to_normal_json_response_when_server_does_not_stream()
    {
        _server.Handler = (ctx, _) =>
        {
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Цял отговор.\"}}]}");
        };
        Assert.Equal("Цял отговор.", await Collect(Client().StreamAsync(Endpoint(), Request(), CancellationToken.None)));
    }

    [Fact]
    public async Task Unauthorized_is_not_retried()
    {
        _server.Handler = (ctx, _) => { ctx.Response.StatusCode = 401; return ctx.Response.WriteAsync("{\"error\":{\"message\":\"bad key\"}}"); };
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Collect(Client().StreamAsync(Endpoint(), Request(), CancellationToken.None)));
        Assert.Equal(AiErrorKind.Unauthorized, ex.Kind);
        Assert.Equal(1, _server.RequestCount);
    }

    [Fact]
    public async Task Server_error_is_retried_and_then_succeeds()
    {
        int calls = 0;
        _server.Handler = (ctx, _) =>
        {
            if (Interlocked.Increment(ref calls) < 3)
            {
                ctx.Response.StatusCode = 503;
                return Task.CompletedTask;
            }

            return MockHttpServer.WriteSseAsync(ctx, ["След ", "повторен опит."]);
        };
        Assert.Equal("След повторен опит.", await Collect(Client().StreamAsync(Endpoint(), Request(), CancellationToken.None)));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Gives_up_after_the_retry_limit()
    {
        _server.Handler = (ctx, _) => { ctx.Response.StatusCode = 500; return Task.CompletedTask; };
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Collect(Client().StreamAsync(Endpoint(retries: 2), Request(), CancellationToken.None)));
        Assert.Equal(AiErrorKind.ServerError, ex.Kind);
        Assert.Equal(3, _server.RequestCount);
    }

    [Fact]
    public async Task Rate_limit_honours_short_retry_after()
    {
        int calls = 0;
        _server.Handler = (ctx, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                ctx.Response.StatusCode = 429;
                ctx.Response.Headers.RetryAfter = "1";
                return Task.CompletedTask;
            }

            return MockHttpServer.WriteSseAsync(ctx, ["ОК"]);
        };
        var sw = Stopwatch.StartNew();
        Assert.Equal("ОК", await Collect(Client().StreamAsync(Endpoint(), Request(), CancellationToken.None)));
        Assert.True(sw.ElapsedMilliseconds >= 900, $"waited {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task No_first_token_in_time_is_a_timeout()
    {
        _server.Handler = async (ctx, _) => await Task.Delay(5000, ctx.RequestAborted);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Collect(Client().StreamAsync(Endpoint(firstTokenSeconds: 0.5, retries: 0), Request(), CancellationToken.None)));
        Assert.Equal(AiErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task User_cancellation_stops_the_stream_immediately()
    {
        _server.Handler = (ctx, _) => MockHttpServer.WriteSseAsync(ctx, Enumerable.Repeat("дума ", 200), TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource();
        var received = 0;
        var sw = new Stopwatch();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Client().StreamAsync(Endpoint(), Request(), cts.Token))
            {
                if (++received == 3)
                {
                    sw.Start();
                    cts.Cancel();
                }
            }
        });
        Assert.True(sw.ElapsedMilliseconds < 500, $"{sw.ElapsedMilliseconds} ms");
        Assert.Equal(3, received);
    }

    [Fact]
    public async Task Disconnect_after_first_tokens_is_reported_and_not_retried()
    {
        _server.Handler = async (ctx, _) =>
        {
            await MockHttpServer.WriteSseAsync(ctx, ["Започвам ", "да "], done: false);
            await Task.Delay(300); // let the client read the tokens before the connection drops
            ctx.Abort();
        };
        var received = new StringBuilder();
        var ex = await Assert.ThrowsAsync<AiProviderException>(async () =>
        {
            await foreach (var t in Client().StreamAsync(Endpoint(), Request(), CancellationToken.None))
            {
                received.Append(t);
            }
        });
        Assert.Equal("Започвам да ", received.ToString());
        Assert.Equal(1, _server.RequestCount);
        Assert.NotEqual(AiErrorKind.Unauthorized, ex.Kind);
    }

    [Fact]
    public async Task No_internet_connection_is_reported_as_NoConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // nothing listens on this port any more
        var endpoint = new ChatEndpoint(new Uri($"http://127.0.0.1:{port}/v1/chat/completions"), "k", "BgGPT", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), 1);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Collect(Client().StreamAsync(endpoint, Request(), CancellationToken.None)));
        Assert.Equal(AiErrorKind.NoConnection, ex.Kind);
        Assert.Contains("ConnectionError", ex.Message); // the technical reason is shown by "Провери връзката"
    }

    [Fact]
    public async Task Unknown_host_is_reported_as_NoConnection()
    {
        var endpoint = new ChatEndpoint(new Uri("http://vedar-does-not-exist.invalid/v1/chat/completions"), "k", "BgGPT", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), 0);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Collect(Client().StreamAsync(endpoint, Request(), CancellationToken.None)));
        Assert.Equal(AiErrorKind.NoConnection, ex.Kind);
    }

    [Fact]
    public async Task Api_key_never_appears_in_logs()
    {
        const string key = "sk-very-secret-bggpt-key-0123456789";
        SecretRedactor.Register(key);
        _server.Handler = (ctx, _) => { ctx.Response.StatusCode = 500; return Task.CompletedTask; };
        await Assert.ThrowsAsync<AiProviderException>(() => Collect(Client().StreamAsync(Endpoint(key, retries: 1), Request(), CancellationToken.None)));
        Assert.NotEmpty(_log.Lines);
        Assert.DoesNotContain(_log.Lines, l => l.Contains(key));
        Assert.Equal($"Authorization: Bearer {SecretRedactor.Mask}", SecretRedactor.Redact($"Authorization: Bearer {key}"));
    }

    [Theory]
    [InlineData("https://api.bggpt.ai/v1", "chat/completions", "https://api.bggpt.ai/v1/chat/completions")]
    [InlineData("https://api.bggpt.ai/v1/", "/models", "https://api.bggpt.ai/v1/models")]
    [InlineData("http://localhost:11434", "v1/chat/completions", "http://localhost:11434/v1/chat/completions")]
    public void Combine_urls(string baseUrl, string rel, string expected) => Assert.Equal(expected, OpenAiCompatibleChatClient.Combine(baseUrl, rel).AbsoluteUri);
}

public class ProviderTests : IAsyncLifetime
{
    private MockHttpServer _server = null!;

    public async ValueTask InitializeAsync() => _server = await MockHttpServer.StartAsync();

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private (TestSettingsStore Settings, InMemorySecretStore Secrets, BgGptProvider BgGpt, OllamaProvider Ollama, AiRouter Router) Create(AiProviderKind kind, bool withKey, double ramGb = 16, bool withGroqKey = false)
    {
        var settings = new TestSettingsStore(new VedarSettings
        {
            Ai = new AiSettings { Provider = kind, BgGptEndpoint = _server.BaseUrl + "/v1", OllamaEndpoint = _server.BaseUrl + "/ollama", GroqEndpoint = _server.BaseUrl + "/groq/v1" },
        });
        var secrets = new InMemorySecretStore();
        if (withKey)
        {
            secrets.Set(SecretNames.BgGptApiKey, "key-abcdef123456");
        }

        if (withGroqKey)
        {
            secrets.Set(SecretNames.GroqApiKey, "gsk_groq123456");
        }

        var http = OpenAiCompatibleChatClient.CreateHttpClient();
        var client = new OpenAiCompatibleChatClient(http, new CapturingLogger(), _ => TimeSpan.FromMilliseconds(20));
        var bggpt = new BgGptProvider(client, settings, secrets, Log.Null<BgGptProvider>());
        var ollama = new OllamaProvider(client, http, settings, Log.Null<OllamaProvider>(), () => SystemResources.Evaluate(ramGb, 8), () => true);
        var groq = new GroqProvider(client, settings, secrets, Log.Null<GroqProvider>());
        return (settings, secrets, bggpt, ollama, new AiRouter(settings, bggpt, ollama, Log.Null<AiRouter>(), groq));
    }

    /// <summary>BgGPT answers with <paramref name="bggptStatus"/> (0 = streams "BgGPT"), Groq streams "Groq".</summary>
    private void BgGptFailsGroqAnswers(int bggptStatus)
    {
        _server.Handler = (ctx, _) =>
        {
            if (ctx.Request.Path.Value!.StartsWith("/groq/"))
            {
                return MockHttpServer.WriteSseAsync(ctx, ["Отговор ", "от Groq."]);
            }

            if (bggptStatus == 0)
            {
                return MockHttpServer.WriteSseAsync(ctx, ["BgGPT."]);
            }

            ctx.Response.StatusCode = bggptStatus;
            return ctx.Response.WriteAsync("{\"message\":\"fail\"}");
        };
    }

    [Fact]
    public async Task Groq_request_uses_the_free_model_bearer_key_and_hides_reasoning()
    {
        _server.Handler = (ctx, _) => MockHttpServer.WriteSseAsync(ctx, ["Здравей."]);
        var (settings, _, _, _, router) = Create(AiProviderKind.Groq, withKey: false, withGroqKey: true);
        settings.Current.Ai.MaxResponseTokens = 300;
        Assert.True(router.IsConfigured);
        Assert.Equal("Groq", router.ActiveProviderName);
        Assert.Equal("Здравей.", await Collect(router.StreamReplyAsync([ChatMessage.User("здравей")], CancellationToken.None)));
        var r = _server.Requests.Single();
        Assert.Equal("/groq/v1/chat/completions", r.Path);
        Assert.Equal("Bearer gsk_groq123456", r.Authorization);
        Assert.Contains("\"model\":\"openai/gpt-oss-120b\"", r.Body);
        Assert.Contains("\"reasoning_effort\":\"low\"", r.Body);
        Assert.Contains("\"include_reasoning\":false", r.Body);
        Assert.Contains("\"max_tokens\":812", r.Body); // answer + allowance for hidden reasoning
    }

    [Theory]
    [InlineData(401)] // rejected key
    [InlineData(429)] // rate limit
    [InlineData(503)] // server error (after the retries)
    public async Task Groq_answers_when_BgGPT_fails_and_the_switch_is_announced(int status)
    {
        BgGptFailsGroqAnswers(status);
        var (_, _, _, _, router) = Create(AiProviderKind.BgGpt, withKey: true, withGroqKey: true);
        AiFallbackEventArgs? fallback = null;
        router.FallbackUsed += (_, e) => fallback = e;
        var system = ChatMessage.System("Ти си Ведар. Работиш чрез езиковия модел BgGPT.");

        var reply = await Collect(router.StreamReplyAsync([system, ChatMessage.User("здравей")], CancellationToken.None));

        Assert.Equal("Отговор от Groq.", reply);
        Assert.NotNull(fallback);
        Assert.Equal("BgGPT", fallback!.FailedProvider);
        Assert.Equal("Groq", fallback.BackupProvider);
        Assert.Equal("Groq", router.ActiveProviderName);
        // The backup is told its own name, so it never claims to be BgGPT.
        var groqRequest = _server.Requests.Single(r => r.Path.StartsWith("/groq/"));
        using var json = JsonDocument.Parse(groqRequest.Body);
        var systemText = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal("Ти си Ведар. Работиш чрез езиковия модел Groq.", systemText);
    }

    [Fact]
    public async Task Groq_answers_when_BgGPT_cannot_be_reached()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        BgGptFailsGroqAnswers(0);
        var (settings, _, _, _, router) = Create(AiProviderKind.BgGpt, withKey: true, withGroqKey: true);
        settings.Current.Ai.BgGptEndpoint = $"http://127.0.0.1:{port}/v1";
        Assert.Equal("Отговор от Groq.", await Collect(router.StreamReplyAsync([ChatMessage.User("здравей")], CancellationToken.None)));
    }

    [Fact]
    public async Task No_backup_without_a_Groq_key_or_when_the_backup_is_switched_off()
    {
        BgGptFailsGroqAnswers(503);
        var (_, _, _, _, noKey) = Create(AiProviderKind.BgGpt, withKey: true);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Collect(noKey.StreamReplyAsync([ChatMessage.User("здравей")], CancellationToken.None)));
        Assert.Equal(AiErrorKind.ServerError, ex.Kind);

        var (settings, _, _, _, off) = Create(AiProviderKind.BgGpt, withKey: true, withGroqKey: true);
        settings.Current.Ai.UseGroqFallback = false;
        await Assert.ThrowsAsync<AiProviderException>(() => Collect(off.StreamReplyAsync([ChatMessage.User("здравей")], CancellationToken.None)));
        Assert.DoesNotContain(_server.Requests, r => r.Path.StartsWith("/groq/"));
    }

    [Fact]
    public async Task Partly_spoken_BgGPT_answer_is_not_repeated_by_Groq()
    {
        _server.Handler = async (ctx, _) =>
        {
            if (ctx.Request.Path.Value!.StartsWith("/groq/"))
            {
                await MockHttpServer.WriteSseAsync(ctx, ["Groq"]);
                return;
            }

            await MockHttpServer.WriteSseAsync(ctx, ["Начало "], done: false);
            await Task.Delay(300); // let the client read the token before the connection drops (as in the disconnect test)
            ctx.Abort();
        };
        var (_, _, _, _, router) = Create(AiProviderKind.BgGpt, withKey: true, withGroqKey: true);
        var tokens = new List<string>();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var t in router.StreamReplyAsync([ChatMessage.User("здравей")], CancellationToken.None))
            {
                tokens.Add(t);
            }
        });
        Assert.Equal(["Начало "], tokens);
        Assert.DoesNotContain(_server.Requests, r => r.Path.StartsWith("/groq/"));
    }

    [Fact]
    public async Task Without_a_BgGPT_key_a_saved_Groq_key_still_gives_conversations()
    {
        BgGptFailsGroqAnswers(0);
        var (_, _, _, _, router) = Create(AiProviderKind.BgGpt, withKey: false, withGroqKey: true);
        Assert.True(router.IsConfigured);
        Assert.Equal("Отговор от Groq.", await Collect(router.StreamReplyAsync([ChatMessage.User("здравей")], CancellationToken.None)));
        Assert.All(_server.Requests, r => Assert.StartsWith("/groq/", r.Path));
    }

    [Fact]
    public async Task Optional_reasoning_fields_are_dropped_if_the_service_rejects_them()
    {
        _server.Handler = (ctx, _) =>
        {
            if (_server.Requests.Last().Body.Contains("reasoning_effort"))
            {
                ctx.Response.StatusCode = 400;
                return ctx.Response.WriteAsync("{\"error\":{\"message\":\"unsupported parameter reasoning_effort\"}}");
            }

            return MockHttpServer.WriteSseAsync(ctx, ["Добре."]);
        };
        var (_, _, _, _, router) = Create(AiProviderKind.Groq, withKey: false, withGroqKey: true);
        Assert.Equal("Добре.", await Collect(router.StreamReplyAsync([ChatMessage.User("здравей")], CancellationToken.None)));
        Assert.Equal(2, _server.Requests.Count);
        Assert.DoesNotContain("reasoning_effort", _server.Requests.Last().Body);
    }

    [Fact]
    public async Task Status_reports_the_backup_when_BgGPT_is_down_but_checks_each_service_separately()
    {
        BgGptFailsGroqAnswers(401);
        var (_, _, _, _, router) = Create(AiProviderKind.BgGpt, withKey: true, withGroqKey: true);
        var overall = await router.CheckStatusAsync(CancellationToken.None);
        Assert.True(overall.IsAvailable);
        Assert.Equal("Groq", overall.ProviderName);
        Assert.Contains("BgGPT: Unauthorized", overall.Detail);
        Assert.Equal(AiAvailability.Unauthorized, (await router.CheckProviderAsync("BgGPT", CancellationToken.None)).Availability);
        Assert.True((await router.CheckProviderAsync("Groq", CancellationToken.None)).IsAvailable);
    }

    private static async Task<string> Collect(IAsyncEnumerable<string> s)
    {
        var sb = new StringBuilder();
        await foreach (var t in s) { sb.Append(t); }
        return sb.ToString();
    }

    [Fact]
    public async Task BgGpt_without_key_reports_missing_key_and_sends_nothing()
    {
        var (_, _, bggpt, _, router) = Create(AiProviderKind.BgGpt, withKey: false);
        Assert.False(bggpt.IsConfigured);
        Assert.False(router.IsConfigured);
        Assert.Equal(AiAvailability.MissingApiKey, (await bggpt.CheckStatusAsync(CancellationToken.None)).Availability);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Collect(bggpt.StreamAsync([ChatMessage.User("здравей")], CancellationToken.None)));
        Assert.Equal(AiErrorKind.MissingApiKey, ex.Kind);
        Assert.Equal(0, _server.RequestCount);
    }

    [Fact]
    public async Task BgGpt_status_uses_the_documented_chat_endpoint_with_one_token()
    {
        _server.Handler = (ctx, _) => MockHttpServer.WriteSseAsync(ctx, ["З"]);
        var (_, _, bggpt, _, _) = Create(AiProviderKind.BgGpt, withKey: true);
        var status = await bggpt.CheckStatusAsync(CancellationToken.None);
        Assert.Equal(AiAvailability.Available, status.Availability);
        Assert.True(status.IsCloud);
        var request = _server.Requests.Single();
        Assert.Equal("/v1/chat/completions", request.Path);
        Assert.Contains("\"max_tokens\":1", request.Body);
        Assert.Contains("\"model\":\"bggpt-gemma-3-27b-fp8\"", request.Body);
        Assert.Equal("Bearer key-abcdef123456", request.Authorization);
    }

    [Fact]
    public async Task BgGpt_key_is_accepted_even_if_models_endpoint_is_refused()
    {
        // Regression: a gateway that refuses GET /models for ordinary keys made a valid key look rejected.
        _server.Handler = (ctx, _) =>
        {
            if (ctx.Request.Path.Value!.EndsWith("/models"))
            {
                ctx.Response.StatusCode = 403;
                return ctx.Response.WriteAsync("{\"message\":\"You cannot consume this service\"}");
            }

            return MockHttpServer.WriteSseAsync(ctx, ["З"]);
        };
        var (_, _, bggpt, _, _) = Create(AiProviderKind.BgGpt, withKey: true);
        Assert.Equal(AiAvailability.Available, (await bggpt.CheckStatusAsync(CancellationToken.None)).Availability);
        Assert.DoesNotContain(_server.Requests, r => r.Path.EndsWith("/models"));
    }

    [Fact]
    public async Task BgGpt_rejected_key_is_reported_as_unauthorized()
    {
        _server.Handler = (ctx, _) => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        var (_, _, bggpt, _, _) = Create(AiProviderKind.BgGpt, withKey: true);
        Assert.Equal(AiAvailability.Unauthorized, (await bggpt.CheckStatusAsync(CancellationToken.None)).Availability);
    }

    [Fact]
    public async Task BgGpt_gateway_error_message_is_shown_in_the_status_detail()
    {
        // The real api.bggpt.ai answers like this for a wrong key (Kong gateway format).
        _server.Handler = (ctx, _) =>
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            return ctx.Response.WriteAsync("{\n  \"message\":\"Unauthorized\",\n  \"request_id\":\"414c1455ba728012a19bc345abc07d74\"\n}");
        };
        var (_, _, bggpt, _, _) = Create(AiProviderKind.BgGpt, withKey: true);
        var status = await bggpt.CheckStatusAsync(CancellationToken.None);
        Assert.Equal(AiAvailability.Unauthorized, status.Availability);
        Assert.Contains("HTTP 401", status.Detail);
        Assert.Contains("Unauthorized", status.Detail);
        Assert.DoesNotContain("key-abcdef123456", status.Detail);
    }

    [Theory]
    [InlineData("Bearer key-abcdef123456")]
    [InlineData("Authorization: Bearer key-abcdef123456")]
    [InlineData("  \"key-abcdef123456\"\r\n")]
    [InlineData("key-abcdef\u200B123456")]
    [InlineData("key-abcdef\n123456")]
    public async Task Pasted_key_with_prefix_quotes_or_invisible_characters_is_cleaned(string pasted)
    {
        _server.Handler = (ctx, _) => MockHttpServer.WriteSseAsync(ctx, ["З"]);
        var (_, secrets, bggpt, _, _) = Create(AiProviderKind.BgGpt, withKey: false);
        secrets.Set(SecretNames.BgGptApiKey, pasted); // as saved by an older version
        Assert.Equal(AiAvailability.Available, (await bggpt.CheckStatusAsync(CancellationToken.None)).Availability);
        Assert.Equal("Bearer key-abcdef123456", _server.Requests.Single().Authorization);
    }

    [Fact]
    public async Task BgGpt_request_uses_settings_model_and_max_tokens()
    {
        _server.Handler = (ctx, _) => MockHttpServer.WriteSseAsync(ctx, ["Да."]);
        var (settings, _, bggpt, _, _) = Create(AiProviderKind.BgGpt, withKey: true);
        settings.Current.Ai.MaxResponseTokens = 123;
        Assert.Equal("Да.", await Collect(bggpt.StreamAsync([ChatMessage.User("Въпрос?")], CancellationToken.None)));
        var body = _server.Requests.Single().Body;
        Assert.Contains("\"model\":\"bggpt-gemma-3-27b-fp8\"", body);
        Assert.Contains("\"max_tokens\":123", body);
    }

    [Fact]
    public async Task Ollama_lists_models_and_streams_through_openai_compatible_endpoint()
    {
        _server.Handler = (ctx, _) =>
        {
            var path = ctx.Request.Path.Value!;
            ctx.Response.ContentType = "application/json";
            return path switch
            {
                "/ollama/api/version" => ctx.Response.WriteAsync("{\"version\":\"0.12.0\"}"),
                "/ollama/api/tags" => ctx.Response.WriteAsync("{\"models\":[{\"name\":\"gemma3:4b\"}]}"),
                "/ollama/v1/chat/completions" => MockHttpServer.WriteSseAsync(ctx, ["Локален ", "отговор."]),
                _ => Task.CompletedTask,
            };
        };
        var (_, _, _, ollama, _) = Create(AiProviderKind.Ollama, withKey: false);
        var info = await ollama.GetInfoAsync(CancellationToken.None);
        Assert.True(info.Running);
        Assert.Equal("0.12.0", info.Version);
        Assert.Equal(["gemma3:4b"], info.Models);
        var status = await ollama.CheckStatusAsync(CancellationToken.None);
        Assert.Equal(AiAvailability.Available, status.Availability);
        Assert.False(status.IsCloud);
        Assert.Equal("Локален отговор.", await Collect(ollama.StreamAsync([ChatMessage.User("Здравей")], CancellationToken.None)));
        Assert.Contains(_server.Requests, r => r.Path == "/ollama/v1/chat/completions" && r.Body.Contains("gemma3:4b") && r.Authorization is null);
    }

    [Fact]
    public async Task Ollama_is_disabled_on_computers_with_too_little_memory()
    {
        var (_, _, _, ollama, _) = Create(AiProviderKind.Ollama, withKey: false, ramGb: 4);
        var status = await ollama.CheckStatusAsync(CancellationToken.None);
        Assert.Equal(AiAvailability.Unavailable, status.Availability);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Collect(ollama.StreamAsync([ChatMessage.User("а")], CancellationToken.None)));
        Assert.Equal(AiErrorKind.InsufficientResources, ex.Kind);
    }

    [Fact]
    public async Task Ollama_not_running_is_reported()
    {
        _server.Handler = (ctx, _) => { ctx.Response.StatusCode = 404; return Task.CompletedTask; };
        var (_, _, _, ollama, _) = Create(AiProviderKind.Ollama, withKey: false);
        var info = await ollama.GetInfoAsync(CancellationToken.None);
        Assert.False(info.Running);
        Assert.Equal(AiAvailability.Unavailable, (await ollama.CheckStatusAsync(CancellationToken.None)).Availability);
    }

    [Fact]
    public async Task Auto_mode_falls_back_to_Ollama_when_BgGPT_is_unreachable()
    {
        _server.Handler = (ctx, _) =>
        {
            var path = ctx.Request.Path.Value!;
            if (path.StartsWith("/v1/"))
            {
                ctx.Abort(); // BgGPT unreachable
                return Task.CompletedTask;
            }

            ctx.Response.ContentType = "application/json";
            return path switch
            {
                "/ollama/api/version" => ctx.Response.WriteAsync("{\"version\":\"0.12.0\"}"),
                "/ollama/api/tags" => ctx.Response.WriteAsync("{\"models\":[{\"name\":\"gemma3:4b\"}]}"),
                _ => MockHttpServer.WriteSseAsync(ctx, ["Отговор ", "без интернет."]),
            };
        };
        var (_, _, _, _, router) = Create(AiProviderKind.Auto, withKey: true);
        Assert.True(router.IsConfigured);
        var text = await Collect(router.StreamReplyAsync([ChatMessage.User("Здравей")], CancellationToken.None));
        Assert.Equal("Отговор без интернет.", text);
        Assert.StartsWith("Ollama", router.ActiveProviderName);
    }

    [Fact]
    public async Task Provider_None_means_not_configured()
    {
        var (_, _, _, _, router) = Create(AiProviderKind.None, withKey: true);
        Assert.False(router.IsConfigured);
        Assert.Equal(AiAvailability.NotConfigured, (await router.CheckStatusAsync(CancellationToken.None)).Availability);
        await Assert.ThrowsAsync<AiProviderException>(() => Collect(router.StreamReplyAsync([ChatMessage.User("а")], CancellationToken.None)));
    }
}

public class ApiKeyTextTests
{
    [Theory]
    [InlineData("abc-123_XYZ", "abc-123_XYZ")]
    [InlineData("  abc-123  ", "abc-123")]
    [InlineData("Bearer abc-123", "abc-123")]
    [InlineData("bearer   abc-123", "abc-123")]
    [InlineData("Authorization: Bearer abc-123", "abc-123")]
    [InlineData("\"abc-123\"", "abc-123")]
    [InlineData("„abc-123“", "abc-123")]
    [InlineData("abc-\r\n123", "abc-123")]
    [InlineData("\uFEFFabc-123\u200B", "abc-123")]
    [InlineData("BearerToken123", "BearerToken123")] // a key that merely starts with "Bearer" is kept
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Normalize(string? pasted, string expected) => Assert.Equal(expected, ApiKeyText.Normalize(pasted));
}

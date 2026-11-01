using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vedar.Tests.Support;

public sealed record RecordedRequest(string Method, string Path, string Body, string? Authorization);

/// <summary>
/// A real HTTP server on 127.0.0.1 (Kestrel) that imitates OpenAI-compatible and Ollama endpoints.
/// Each test installs a handler; requests are recorded for assertions.
/// </summary>
public sealed class MockHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private MockHttpServer(WebApplication app) => _app = app;

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
    public Func<HttpContext, string, Task>? Handler { get; set; }
    public string BaseUrl { get; private set; } = string.Empty;
    public int RequestCount => Requests.Count;

    public static async Task<MockHttpServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new MockHttpServer(app);
        app.Run(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            server.Requests.Enqueue(new RecordedRequest(ctx.Request.Method, ctx.Request.Path.Value ?? string.Empty, body, ctx.Request.Headers.Authorization.FirstOrDefault()));
            if (server.Handler is null)
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            await server.Handler(ctx, body);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.BaseUrl = address.TrimEnd('/');
        return server;
    }

    public static async Task WriteSseAsync(HttpContext ctx, IEnumerable<string> tokens, TimeSpan delay = default, bool done = true)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        await ctx.Response.Body.FlushAsync();
        foreach (var t in tokens)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new { id = "x", @object = "chat.completion.chunk", choices = new[] { new { index = 0, delta = new { content = t } } } });
            await ctx.Response.WriteAsync($"data: {json}\n\n");
            await ctx.Response.Body.FlushAsync();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ctx.RequestAborted);
            }
        }

        if (done)
        {
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

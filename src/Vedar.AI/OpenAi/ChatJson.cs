using System.Text.Json;
using System.Text.Json.Nodes;
using Vedar.Core.Abstractions;

namespace Vedar.AI.OpenAi;

/// <summary>JSON helpers for the OpenAI Chat Completions format.</summary>
public static class ChatJson
{
    public static string BuildRequest(string model, IReadOnlyList<ChatMessage> messages, int maxTokens, float temperature, bool stream, IReadOnlyDictionary<string, object>? extra = null)
    {
        var array = new JsonArray();
        foreach (var m in messages)
        {
            array.Add(new JsonObject
            {
                ["role"] = m.Role switch { ChatRole.System => "system", ChatRole.Assistant => "assistant", _ => "user" },
                ["content"] = m.Content,
            });
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = array,
            ["max_tokens"] = maxTokens,
            ["temperature"] = Math.Round(temperature, 2),
            ["stream"] = stream,
        };
        if (extra is not null)
        {
            foreach (var (name, value) in extra)
            {
                body[name] = value switch
                {
                    bool b => JsonValue.Create(b),
                    int i => JsonValue.Create(i),
                    double d => JsonValue.Create(d),
                    float f => JsonValue.Create(f),
                    _ => JsonValue.Create(value.ToString()),
                };
            }
        }

        return body.ToJsonString();
    }

    /// <summary>Extracts choices[0].delta.content from a streaming chunk. Returns null when there is no text.</summary>
    public static string? ParseDelta(string json, out string? errorMessage)
    {
        errorMessage = null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            errorMessage = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var msg) ? msg.GetString() : error.ToString();
            return null;
        }

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var choice = choices[0];
        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
            delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        // Some servers send the full message in streaming mode.
        if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var full) && full.ValueKind == JsonValueKind.String)
        {
            return full.GetString();
        }

        return null;
    }

    /// <summary>Extracts choices[0].message.content from a non-streaming response.</summary>
    public static string? ParseCompletion(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
        {
            return content.GetString();
        }

        return null;
    }

    public static string? ExtractErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
            {
                return e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString();
            }

            if (doc.RootElement.TryGetProperty("detail", out var d))
            {
                return d.ToString();
            }

            // API gateways (e.g. Kong in front of BgGPT): {"message": "Unauthorized", "request_id": "…"}
            if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
            {
                return msg.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return body.Length > 200 ? body[..200] : body;
    }
}

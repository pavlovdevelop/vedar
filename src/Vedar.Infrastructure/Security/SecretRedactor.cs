using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Vedar.Infrastructure.Security;

/// <summary>
/// Removes secrets from any text before it is written to a log. Known secret values are registered at runtime;
/// common key formats are also masked by pattern.
/// </summary>
public static partial class SecretRedactor
{
    public const string Mask = "***";
    private static readonly ConcurrentDictionary<string, byte> Secrets = new();

    [GeneratedRegex(@"(?i)(bearer\s+)[A-Za-z0-9._~+/=\-]{6,}")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_\-]{12,}")]
    private static partial Regex SkPattern();

    [GeneratedRegex(@"(?i)(api[_-]?key[""']?\s*[:=]\s*[""']?)[^\s""',;]{6,}")]
    private static partial Regex ApiKeyPattern();

    public static void Register(string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 6)
        {
            Secrets[secret] = 0;
        }
    }

    public static void Unregister(string? secret)
    {
        if (secret is not null)
        {
            Secrets.TryRemove(secret, out _);
        }
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        foreach (var secret in Secrets.Keys)
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        text = BearerPattern().Replace(text, "$1" + Mask);
        text = SkPattern().Replace(text, "sk-" + Mask);
        text = ApiKeyPattern().Replace(text, "$1" + Mask);
        return text;
    }
}

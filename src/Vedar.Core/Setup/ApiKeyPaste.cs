using Vedar.Core.Abstractions;
using Vedar.Core.Localization;

namespace Vedar.Core.Setup;

public sealed record KeyPasteResult(bool Saved, bool Works, string Provider, string Spoken);

/// <summary>
/// Takes an API key from the clipboard — a blind user copies it from the e-mail or web page with the screen
/// reader and Ctrl+C, then says "готово" — stores it protected (DPAPI), clears the clipboard and checks the
/// connection. The key itself is never spoken, shown or logged.
/// </summary>
public sealed class ApiKeyPaste(IUserInterfaceHost ui, ISecretStore secrets, IConversationAi ai, Localizer loc)
{
    public const string BgGpt = "BgGPT";
    public const string Groq = "Groq";

    /// <summary>Groq API keys start with this prefix, so a pasted key is assigned to the right service.</summary>
    public const string GroqKeyPrefix = "gsk_";

    /// <param name="provider">"BgGPT" or "Groq"; null = decide from the key (Groq keys start with "gsk_").</param>
    public async Task<KeyPasteResult> PasteAsync(string? provider, CancellationToken cancellationToken)
    {
        string? text;
        try
        {
            text = await ui.GetClipboardTextAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            text = null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new KeyPasteResult(false, false, provider ?? BgGpt, loc.Get("key.clipboardEmpty"));
        }

        var key = ApiKeyText.Normalize(text);
        if (!LooksLikeKey(key))
        {
            return new KeyPasteResult(false, false, provider ?? BgGpt, loc.Get("key.notAKey"));
        }

        provider ??= key.StartsWith(GroqKeyPrefix, StringComparison.Ordinal) ? Groq : BgGpt;
        secrets.Set(provider == Groq ? SecretNames.GroqApiKey : SecretNames.BgGptApiKey, key);
        try
        {
            await ui.ClearClipboardAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Not critical.
        }

        var status = await ai.CheckProviderAsync(provider, cancellationToken).ConfigureAwait(false);
        var cleared = loc.Get("key.clipboardCleared");
        return status.IsAvailable
            ? new KeyPasteResult(true, true, provider, loc.Format("key.savedWorks", provider) + " " + cleared)
            : new KeyPasteResult(true, false, provider, loc.Format("key.savedFailed", provider, Reason(status)) + " " + cleared);
    }

    /// <summary>Keys are one "word" of visible ASCII characters; a copied sentence or e-mail text is not a key.</summary>
    public static bool LooksLikeKey(string key) => key.Length is >= 16 and <= 400 && key.All(c => c is > ' ' and < '\u007f');

    private string Reason(AiStatus status) => status.Availability switch
    {
        AiAvailability.Unauthorized => loc.Format("key.reason.unauthorized", status.ProviderName),
        AiAvailability.Offline => loc.Format("key.reason.offline", status.ProviderName),
        _ => loc.Format("key.reason.other", status.ProviderName),
    };
}

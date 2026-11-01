using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Vedar.Core.Localization;

/// <summary>
/// Simple JSON based localization. Values may be a string or an array of variants
/// (see <see cref="Pick"/>) so VEDAR does not start every sentence the same way.
/// Bulgarian ("bg") is the primary language and the fallback for missing keys.
/// </summary>
public sealed class Localizer
{
    public const string PrimaryLanguage = "bg";

    private readonly Dictionary<string, string[]> _values;
    private readonly Dictionary<string, string[]> _fallback;
    private readonly Random _random;

    private Localizer(string language, Dictionary<string, string[]> values, Dictionary<string, string[]> fallback, Random random)
    {
        Language = language;
        _values = values;
        _fallback = fallback;
        _random = random;
    }

    public string Language { get; }

    // Declared before Current: static initializers run in textual order.
    private static readonly string[] Languages = ["bg", "en"];

    public static IReadOnlyList<string> AvailableLanguages => Languages;

    public static Localizer Current { get; set; } = Load(PrimaryLanguage);

    public static Localizer Load(string? language, Random? random = null)
    {
        var lang = AvailableLanguages.Contains(language ?? string.Empty) ? language! : PrimaryLanguage;
        var fallback = ReadResource(PrimaryLanguage);
        var values = lang == PrimaryLanguage ? fallback : ReadResource(lang);
        return new Localizer(lang, values, fallback, random ?? Random.Shared);
    }

    private static Dictionary<string, string[]> ReadResource(string language)
    {
        var asm = typeof(Localizer).Assembly;
        using var stream = asm.GetManifestResourceStream($"Vedar.Core.Localization.{language}.json")
            ?? throw new InvalidOperationException($"Missing localization resource for '{language}'.");
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Array => prop.Value.EnumerateArray().Select(v => v.GetString() ?? string.Empty).ToArray(),
                _ => [prop.Value.GetString() ?? string.Empty],
            };
        }

        return result;
    }

    public bool Contains(string key) => _values.ContainsKey(key) || _fallback.ContainsKey(key);

    public IReadOnlyCollection<string> Keys => _fallback.Keys;

    private string[] Lookup(string key) =>
        _values.TryGetValue(key, out var v) ? v : _fallback.TryGetValue(key, out var f) ? f : [key];

    /// <summary>First variant of the key (UI text).</summary>
    public string this[string key] => Lookup(key)[0];

    public string Get(string key) => Lookup(key)[0];

    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.GetCultureInfo(Language == "bg" ? "bg-BG" : "en-US"), Get(key), args);

    /// <summary>A random variant of the key, formatted with the arguments.</summary>
    public string Pick(string key, params object?[] args)
    {
        var variants = Lookup(key);
        var template = variants.Length == 1 ? variants[0] : variants[_random.Next(variants.Length)];
        return args.Length == 0 ? template : string.Format(CultureInfo.GetCultureInfo(Language == "bg" ? "bg-BG" : "en-US"), template, args);
    }

    public IReadOnlyList<string> Variants(string key) => Lookup(key);
}

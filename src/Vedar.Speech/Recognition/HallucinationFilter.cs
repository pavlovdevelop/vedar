using System.Text.RegularExpressions;
using Vedar.Core.Text;

namespace Vedar.Speech.Recognition;

/// <summary>
/// Whisper sometimes "hears" stock phrases in silence or noise (typical for Bulgarian/Russian training data:
/// "Абонирайте се", "Субтитри…"). These must never become commands or wake-word activations.
/// </summary>
public static partial class HallucinationFilter
{
    private static readonly string[] Phrases =
    [
        "абонирайте се", "абонирай се", "субтитри", "субтитры", "редактор субтитров", "продолжение следует",
        "благодаря за гледането", "благодаря ви за гледането", "благодарим за вниманието", "благодаря за вниманието",
        "спасибо за внимание", "спасибо за просмотр", "thank you for watching", "thanks for watching", "amara org",
        "превод и субтитри", "дякую за перегляд", "до нови срещи", "до новых встреч", "please subscribe", "like and subscribe",
        "редактор на субтитрите", "субтитрите са", "коректор",
    ];

    private static readonly HashSet<string> ExactJunk = new(StringComparer.OrdinalIgnoreCase)
    {
        "you", "thank you", "thanks", "bye", "music", "музика", "смях", "аплодисменти", "тишина", "ъ", "а", "м", "мм", "хм", "ах", "ох",
    };

    [GeneratedRegex(@"[\[\(\*♪][^\]\)\*♪]{0,40}[\]\)\*♪]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\b(\w+)(?:[\s,.]+\1\b){3,}", RegexOptions.IgnoreCase)]
    private static partial Regex Repetition();

    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var s = Bracketed().Replace(text, " ");
        s = s.Replace("♪", " ", StringComparison.Ordinal);
        s = Repetition().Replace(s, "$1");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        var normalized = BulgarianText.NormalizeForMatching(s);
        if (normalized.Length == 0 || ExactJunk.Contains(normalized))
        {
            return string.Empty;
        }

        foreach (var phrase in Phrases)
        {
            if (normalized.Contains(phrase, StringComparison.Ordinal))
            {
                return string.Empty;
            }
        }

        return s.Trim(' ', '-', '—');
    }
}

using System.Globalization;
using System.Text;

namespace Vedar.Core.Text;

/// <summary>Helpers for matching spoken Bulgarian text produced by speech recognition.</summary>
public static class BulgarianText
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("bg-BG");

    /// <summary>Lower-case, unify letters, remove punctuation, collapse spaces.</summary>
    public static string NormalizeForMatching(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        bool lastSpace = true;
        foreach (var raw in text.Normalize(NormalizationForm.FormC))
        {
            var c = char.ToLower(raw, Culture);
            c = c switch
            {
                'ѝ' => 'и',
                'ё' => 'е',
                'ы' => 'и',
                'э' => 'е',
                '’' or '\'' or '`' => '\0',
                _ => c,
            };
            if (c == '\0')
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    public static string[] Tokenize(string? text) =>
        NormalizeForMatching(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static bool IsCyrillic(char c) => c is >= 'Ѐ' and <= 'ӿ';

    public static bool ContainsCyrillic(string text) => text.Any(IsCyrillic);
}

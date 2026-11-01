using System.Text;

namespace Vedar.Core.Text;

/// <summary>Splits text into sentence-sized chunks for speech synthesis.</summary>
public static class SentenceSplitter
{
    internal static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "т.е", "напр", "г", "гр", "ул", "бул", "д-р", "др", "проф", "стр", "бел", "т.нар", "вкл", "мин", "сек", "ч", "кв", "ж.к", "тел", "млн", "млрд", "хил", "лв", "ст", "в", "с", "доц", "акад", "инж", "арх", "чл", "ал", "т", "пр", "сл", "св", "№", "e.g", "i.e", "etc", "mr", "mrs", "dr",
    };

    public const int MaxChunkLength = 240;

    public static IReadOnlyList<string> Split(string? text, int maxLength = MaxChunkLength)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var current = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\r' or '\n')
            {
                Flush(current, result, maxLength);
                continue;
            }

            current.Append(c);
            if (IsSentenceEnd(text, i))
            {
                // Consume trailing quotes/brackets that belong to the sentence.
                while (i + 1 < text.Length && text[i + 1] is '"' or '”' or '“' or '»' or ')' or '\'')
                {
                    current.Append(text[++i]);
                }

                Flush(current, result, maxLength);
            }
        }

        Flush(current, result, maxLength);
        return result;
    }

    internal static bool IsSentenceEnd(string text, int i)
    {
        char c = text[i];
        if (c is not ('.' or '!' or '?' or '…' or ';'))
        {
            return false;
        }

        // Next char must be whitespace or end.
        if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && text[i + 1] is not ('"' or '”' or '»' or ')'))
        {
            return false;
        }

        if (c == '.')
        {
            // Decimal or ordinal digits "3." followed by lowercase are not sentence ends.
            var word = PreviousWord(text, i);
            if (Abbreviations.Contains(word))
            {
                // An abbreviation ends a sentence only if followed by an upper-case letter after spaces.
                int j = i + 1;
                while (j < text.Length && char.IsWhiteSpace(text[j]))
                {
                    j++;
                }

                return j >= text.Length || (char.IsUpper(text[j]) && word.Length > 2);
            }

            if (word.Length > 0 && word.All(char.IsDigit))
            {
                int j = i + 1;
                while (j < text.Length && char.IsWhiteSpace(text[j]))
                {
                    j++;
                }

                return j >= text.Length || char.IsUpper(text[j]);
            }
        }

        return true;
    }

    private static string PreviousWord(string text, int dotIndex)
    {
        int start = dotIndex - 1;
        while (start >= 0 && (char.IsLetterOrDigit(text[start]) || text[start] is '.' or '-' or '№'))
        {
            start--;
        }

        return text.Substring(start + 1, dotIndex - start - 1);
    }

    private static void Flush(StringBuilder current, List<string> result, int maxLength)
    {
        var s = current.ToString().Trim();
        current.Clear();
        if (s.Length == 0)
        {
            return;
        }

        foreach (var piece in BreakLong(s, maxLength))
        {
            if (piece.Any(char.IsLetterOrDigit))
            {
                result.Add(piece);
            }
        }
    }

    /// <summary>Breaks overly long sentences at commas, dashes or spaces.</summary>
    internal static IEnumerable<string> BreakLong(string s, int maxLength)
    {
        while (s.Length > maxLength)
        {
            int cut = LastIndexOfAny(s, [',', ';', ':', '—', '–'], maxLength);
            if (cut < maxLength / 3)
            {
                cut = s.LastIndexOf(' ', maxLength);
            }

            if (cut <= 0)
            {
                cut = maxLength;
            }

            yield return s[..(cut + 1)].Trim();
            s = s[(cut + 1)..].Trim();
        }

        if (s.Length > 0)
        {
            yield return s;
        }
    }

    private static int LastIndexOfAny(string s, char[] chars, int before)
    {
        int best = -1;
        foreach (var ch in chars)
        {
            best = Math.Max(best, s.LastIndexOf(ch, Math.Min(before, s.Length - 1)));
        }

        return best;
    }
}

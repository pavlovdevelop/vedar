namespace Vedar.Core.Text;

public enum GrammaticalGender { Masculine, Feminine, Neuter }

/// <summary>
/// Converts numbers to Bulgarian words, e.g. 2026 → "две хиляди двадесет и шест".
/// Follows the standard rule that "и" goes before the last component.
/// </summary>
public static class BulgarianNumbers
{
    private static readonly string[] UnitsMasculine = ["нула", "един", "два", "три", "четири", "пет", "шест", "седем", "осем", "девет"];
    private static readonly string[] Teens = ["десет", "единадесет", "дванадесет", "тринадесет", "четиринадесет", "петнадесет", "шестнадесет", "седемнадесет", "осемнадесет", "деветнадесет"];
    private static readonly string[] Tens = ["", "", "двадесет", "тридесет", "четиридесет", "петдесет", "шестдесет", "седемдесет", "осемдесет", "деветдесет"];
    private static readonly string[] Hundreds = ["", "сто", "двеста", "триста", "четиристотин", "петстотин", "шестстотин", "седемстотин", "осемстотин", "деветстотин"];

    private static readonly string[] OrdinalUnitStems = ["нулев", "първ", "втор", "трет", "четвърт", "пет", "шест", "седм", "осм", "девет"];

    private static string Unit(int u, GrammaticalGender gender) => (u, gender) switch
    {
        (1, GrammaticalGender.Feminine) => "една",
        (1, GrammaticalGender.Neuter) => "едно",
        (2, GrammaticalGender.Feminine) => "две",
        (2, GrammaticalGender.Neuter) => "две",
        _ => UnitsMasculine[u],
    };

    /// <summary>The components of a number 1..999, e.g. 125 → ["сто", "двадесет", "пет"].</summary>
    private static List<string> GroupComponents(int n, GrammaticalGender gender)
    {
        var parts = new List<string>(3);
        int h = n / 100, r = n % 100;
        if (h > 0)
        {
            parts.Add(Hundreds[h]);
        }

        if (r is >= 10 and < 20)
        {
            parts.Add(Teens[r - 10]);
        }
        else
        {
            int t = r / 10, u = r % 10;
            if (t > 0)
            {
                parts.Add(Tens[t]);
            }

            if (u > 0)
            {
                parts.Add(Unit(u, gender));
            }
        }

        return parts;
    }

    private static string JoinWithAnd(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => string.Empty,
        1 => parts[0],
        _ => string.Join(' ', parts.Take(parts.Count - 1)) + " и " + parts[^1],
    };

    /// <summary>Cardinal number in words. The gender affects 1 and 2 (един/една/едно, два/две).</summary>
    public static string Cardinal(long n, GrammaticalGender gender = GrammaticalGender.Masculine)
    {
        if (n == 0)
        {
            return "нула";
        }

        if (n < 0)
        {
            return "минус " + Cardinal(-n, gender);
        }

        if (n >= 1_000_000_000_000)
        {
            return Digits(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var scaleParts = new List<(string Text, bool Simple)>();
        long billions = n / 1_000_000_000;
        long millions = n / 1_000_000 % 1000;
        long thousands = n / 1000 % 1000;
        int rest = (int)(n % 1000);

        if (billions > 0)
        {
            scaleParts.Add(Scale(billions, "милиард", "милиарда"));
        }

        if (millions > 0)
        {
            scaleParts.Add(Scale(millions, "милион", "милиона"));
        }

        if (thousands > 0)
        {
            scaleParts.Add(thousands == 1
                ? ("хиляда", true)
                : (Cardinal(thousands, GrammaticalGender.Feminine) + " хиляди", GroupComponents((int)thousands, GrammaticalGender.Feminine).Count == 1));
        }

        if (rest == 0)
        {
            if (scaleParts.Count == 1)
            {
                return scaleParts[0].Text;
            }

            var head = string.Join(' ', scaleParts.Take(scaleParts.Count - 1).Select(p => p.Text));
            return scaleParts[^1].Simple ? head + " и " + scaleParts[^1].Text : head + " " + scaleParts[^1].Text;
        }

        var restParts = GroupComponents(rest, gender);
        if (scaleParts.Count == 0)
        {
            return JoinWithAnd(restParts);
        }

        var prefix = string.Join(' ', scaleParts.Select(p => p.Text));
        return restParts.Count == 1 ? prefix + " и " + restParts[0] : prefix + " " + JoinWithAnd(restParts);
    }

    private static (string Text, bool Simple) Scale(long count, string singular, string plural)
    {
        if (count == 1)
        {
            return ("един " + singular, true);
        }

        var simple = GroupComponents((int)count, GrammaticalGender.Masculine).Count == 1;
        return (Cardinal(count, GrammaticalGender.Masculine) + " " + plural, simple);
    }

    /// <summary>Ordinal number, e.g. 1 → "първи", 2026 (feminine) → "две хиляди двадесет и шеста".</summary>
    public static string Ordinal(long n, GrammaticalGender gender = GrammaticalGender.Masculine)
    {
        if (n <= 0)
        {
            return Cardinal(n, gender);
        }

        string suffixAdj = gender switch { GrammaticalGender.Feminine => "а", GrammaticalGender.Neuter => "о", _ => "и" };

        // Round thousands below 10 000 form a single compound word: двехилядна, трихиляден.
        if (n % 1000 == 0 && n < 10_000)
        {
            var prefix = (n / 1000) switch
            {
                1 => string.Empty,
                2 => "две",
                3 => "три",
                4 => "четири",
                5 => "пет",
                6 => "шест",
                7 => "седем",
                8 => "осем",
                _ => "девет",
            };
            return prefix + "хиляд" + HundredSuffix(gender);
        }

        if (n == 1_000_000)
        {
            return "милион" + HundredSuffix(gender);
        }

        var cardinal = Cardinal(n, gender);
        var words = cardinal.Split(' ');
        var last = words[^1];
        int lastValue = (int)(n % 100);
        string ordinalLast;
        if (n % 1000 == 0)
        {
            ordinalLast = "хиляд" + HundredSuffix(gender);
        }
        else if (n % 100 == 0)
        {
            ordinalLast = HundredOrdinal(last, gender);
        }
        else if (lastValue is > 0 and < 10)
        {
            ordinalLast = OrdinalUnitStems[lastValue] + suffixAdj;
        }
        else if (lastValue % 10 is > 0 and var u && lastValue > 20)
        {
            ordinalLast = OrdinalUnitStems[u] + suffixAdj;
        }
        else
        {
            // 10..19, 20, 30 ... 90: "десет" → "десети/десета/десето".
            ordinalLast = last + suffixAdj;
        }

        words[^1] = ordinalLast;
        return string.Join(' ', words);
    }

    private static string HundredSuffix(GrammaticalGender gender) => gender switch
    {
        GrammaticalGender.Feminine => "на",
        GrammaticalGender.Neuter => "но",
        _ => "ен",
    };

    private static string HundredOrdinal(string word, GrammaticalGender gender)
    {
        string stem = word switch
        {
            "сто" => "стот",
            "двеста" => "двестот",
            "триста" => "тристот",
            _ when word.EndsWith("стотин", StringComparison.Ordinal) => word[..^"стотин".Length] + "стот",
            _ => word,
        };
        return stem + HundredSuffix(gender);
    }

    private static readonly string[] DigitNames = ["нула", "едно", "две", "три", "четири", "пет", "шест", "седем", "осем", "девет"];

    /// <summary>Reads digits one by one (neuter forms, as in phone numbers), e.g. "0812" → "нула осем едно две".</summary>
    public static string Digits(string digits) =>
        string.Join(' ', digits.Where(char.IsAsciiDigit).Select(c => DigitNames[c - '0']));

    /// <summary>Correct plural for a counted noun: 1 → singular, otherwise plural (e.g. минута/минути).</summary>
    public static string Count(long n, string singular, string plural, GrammaticalGender gender, string? countedMasculinePlural = null)
    {
        if (n == 1)
        {
            return Cardinal(1, gender) + " " + singular;
        }

        // Masculine nouns use the "counted" form after numbers: два часа, пет лева.
        var noun = gender == GrammaticalGender.Masculine && countedMasculinePlural is not null ? countedMasculinePlural : plural;
        return Cardinal(n, gender) + " " + noun;
    }
}

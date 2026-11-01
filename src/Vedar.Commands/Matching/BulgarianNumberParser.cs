using System.Globalization;

namespace Vedar.Commands.Matching;

/// <summary>Parses spoken numbers 0..1000 ("двадесет и пет", "сто", "50", "50%").</summary>
public static class BulgarianNumberParser
{
    private static readonly Dictionary<string, int> Words = new(StringComparer.Ordinal)
    {
        ["нула"] = 0, ["един"] = 1, ["една"] = 1, ["едно"] = 1, ["два"] = 2, ["две"] = 2, ["три"] = 3, ["четири"] = 4, ["пет"] = 5,
        ["шест"] = 6, ["седем"] = 7, ["осем"] = 8, ["девет"] = 9, ["десет"] = 10, ["единадесет"] = 11, ["единайсет"] = 11,
        ["дванадесет"] = 12, ["дванайсет"] = 12, ["тринадесет"] = 13, ["тринайсет"] = 13, ["четиринадесет"] = 14, ["четиринайсет"] = 14,
        ["петнадесет"] = 15, ["петнайсет"] = 15, ["шестнадесет"] = 16, ["шестнайсет"] = 16, ["седемнадесет"] = 17, ["седемнайсет"] = 17,
        ["осемнадесет"] = 18, ["осемнайсет"] = 18, ["деветнадесет"] = 19, ["деветнайсет"] = 19, ["двадесет"] = 20, ["двайсет"] = 20,
        ["тридесет"] = 30, ["трийсет"] = 30, ["четиридесет"] = 40, ["четирсет"] = 40, ["петдесет"] = 50, ["шестдесет"] = 60,
        ["седемдесет"] = 70, ["осемдесет"] = 80, ["деветдесет"] = 90, ["сто"] = 100, ["двеста"] = 200, ["триста"] = 300,
        ["четиристотин"] = 400, ["петстотин"] = 500, ["хиляда"] = 1000, ["половина"] = 50, ["максимум"] = 100, ["минимум"] = 0,
    };

    /// <summary>Parses a number starting at token <paramref name="start"/>. Returns the value and the number of tokens used.</summary>
    public static (int Value, int Length)? Parse(IReadOnlyList<string> tokens, int start)
    {
        if (start >= tokens.Count)
        {
            return null;
        }

        var first = tokens[start].TrimEnd('%');
        if (int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var digits))
        {
            return (digits, 1);
        }

        int total = 0, used = 0;
        for (int i = start; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t == "и" && used > 0)
            {
                used++;
                continue;
            }

            if (!Words.TryGetValue(t, out var value))
            {
                break;
            }

            total += value;
            used++;
        }

        // Trailing "и" is not part of the number.
        while (used > 0 && tokens[start + used - 1] == "и")
        {
            used--;
        }

        return used > 0 ? (total, used) : null;
    }
}

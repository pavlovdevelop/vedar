using Vedar.Core.Abstractions;

namespace Vedar.Core.Text;

/// <summary>Understands spoken yes/no answers to confirmation questions.</summary>
public static class ConfirmationParser
{
    private static readonly HashSet<string> YesWords =
    [
        "да", "даа", "дааа", "добре", "потвърждавам", "потвърди", "сигурен", "сигурна", "разбира", "ок", "окей", "okay", "ok",
        "давай", "действай", "правилно", "точно", "yes", "ъхъ", "аха", "може", "съгласен", "съгласна",
    ];

    private static readonly HashSet<string> NoWords =
    [
        "не", "нее", "недей", "откажи", "отказ", "отмени", "отменям", "спри", "стоп", "no", "никак", "откажа", "остави",
    ];

    public static ConfirmationAnswer Parse(string? text)
    {
        var tokens = BulgarianText.Tokenize(text);
        if (tokens.Length == 0)
        {
            return ConfirmationAnswer.Unknown;
        }

        // A negation anywhere wins ("не съм сигурен", "да, не").
        if (tokens.Any(NoWords.Contains))
        {
            return ConfirmationAnswer.No;
        }

        return tokens.Any(YesWords.Contains) ? ConfirmationAnswer.Yes : ConfirmationAnswer.Unknown;
    }
}

using System.Text;

namespace Vedar.Speech.Text;

/// <summary>
/// The Bulgarian voice cannot pronounce Latin text well, so known words use a pronunciation dictionary,
/// acronyms are spelled with English letter names and other words get a rough English-style transliteration.
/// </summary>
public static class LatinTransliterator
{
    public static readonly IReadOnlyDictionary<string, string> Dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["VEDAR"] = "Ведар",
        ["BgGPT"] = "Би Джи Джи Пи Ти",
        ["ChatGPT"] = "Чат Джи Пи Ти",
        ["GPT"] = "Джи Пи Ти",
        ["OpenAI"] = "Оупън Ей Ай",
        ["Google"] = "Гугъл",
        ["YouTube"] = "Ютюб",
        ["Facebook"] = "Фейсбук",
        ["Viber"] = "Вайбър",
        ["WhatsApp"] = "Уотсап",
        ["Skype"] = "Скайп",
        ["Zoom"] = "Зуум",
        ["Windows"] = "Уиндоус",
        ["Microsoft"] = "Майкрософт",
        ["Word"] = "Уърд",
        ["Excel"] = "Ексел",
        ["Outlook"] = "Аутлук",
        ["Notepad"] = "Нотпад",
        ["Chrome"] = "Хром",
        ["Firefox"] = "Файърфокс",
        ["Edge"] = "Едж",
        ["NVDA"] = "Ен Ви Ди Ей",
        ["JAWS"] = "Джоус",
        ["Narrator"] = "Наратор",
        ["Ollama"] = "Олама",
        ["Gemma"] = "Джема",
        ["Whisper"] = "Уиспър",
        ["Piper"] = "Пайпър",
        ["Wikipedia"] = "Уикипедия",
        ["email"] = "имейл",
        ["e-mail"] = "имейл",
        ["online"] = "онлайн",
        ["offline"] = "офлайн",
        ["internet"] = "интернет",
        ["Wi-Fi"] = "уайфай",
        ["WiFi"] = "уайфай",
        ["Bluetooth"] = "блутут",
        ["OK"] = "окей",
        ["AI"] = "Ей Ай",
        ["USB"] = "Ю Ес Би",
        ["PDF"] = "Пи Ди Еф",
        ["PC"] = "Пи Си",
        ["iPhone"] = "Айфон",
        ["Android"] = "Андроид",
        ["Apple"] = "Епъл",
        ["Samsung"] = "Самсунг",
        ["www"] = "",
        ["http"] = "",
        ["https"] = "",
        ["com"] = "ком",
        ["org"] = "орг",
        ["net"] = "нет",
        ["bg"] = "бг",
        ["eu"] = "еу",
        ["the"] = "дъ",
        ["and"] = "енд",
        ["of"] = "ъв",
    };

    private static readonly Dictionary<char, string> LetterNames = new()
    {
        ['A'] = "Ей", ['B'] = "Би", ['C'] = "Си", ['D'] = "Ди", ['E'] = "И", ['F'] = "Еф", ['G'] = "Джи", ['H'] = "Ейч", ['I'] = "Ай",
        ['J'] = "Джей", ['K'] = "Кей", ['L'] = "Ел", ['M'] = "Ем", ['N'] = "Ен", ['O'] = "Оу", ['P'] = "Пи", ['Q'] = "Кю", ['R'] = "Ар",
        ['S'] = "Ес", ['T'] = "Ти", ['U'] = "Ю", ['V'] = "Ви", ['W'] = "Дабъл Ю", ['X'] = "Екс", ['Y'] = "Уай", ['Z'] = "Зед",
    };

    private static readonly (string Latin, string Cyrillic)[] Digraphs =
    [
        ("tion", "шън"), ("sch", "ш"), ("tch", "ч"), ("sh", "ш"), ("ch", "ч"), ("th", "т"), ("ph", "ф"), ("zh", "ж"), ("kh", "х"),
        ("ck", "к"), ("qu", "кв"), ("oo", "у"), ("ee", "и"), ("ea", "и"), ("ou", "ау"), ("ow", "оу"), ("ay", "ей"), ("ey", "ей"),
        ("ai", "ей"), ("oi", "ой"), ("ya", "я"), ("yu", "ю"), ("yo", "йо"), ("ts", "ц"), ("ja", "я"), ("ju", "ю"),
    ];

    private static readonly Dictionary<char, string> Letters = new()
    {
        ['a'] = "а", ['b'] = "б", ['c'] = "к", ['d'] = "д", ['e'] = "е", ['f'] = "ф", ['g'] = "г", ['h'] = "х", ['i'] = "и", ['j'] = "дж",
        ['k'] = "к", ['l'] = "л", ['m'] = "м", ['n'] = "н", ['o'] = "о", ['p'] = "п", ['q'] = "к", ['r'] = "р", ['s'] = "с", ['t'] = "т",
        ['u'] = "у", ['v'] = "в", ['w'] = "у", ['x'] = "кс", ['y'] = "и", ['z'] = "з",
    };

    public static bool IsLatinWord(string word) => word.Length > 0 && word.Any(char.IsAsciiLetter) && word.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '\'');

    public static string Transliterate(string word)
    {
        if (Dictionary.TryGetValue(word, out var known))
        {
            return known;
        }

        var lettersOnly = new string(word.Where(char.IsAsciiLetter).ToArray());
        if (lettersOnly.Length is >= 2 and <= 6 && lettersOnly.All(char.IsAsciiLetterUpper))
        {
            return Spell(lettersOnly);
        }

        if (lettersOnly.Length == 1)
        {
            return LetterNames.TryGetValue(char.ToUpperInvariant(lettersOnly[0]), out var name) ? name : lettersOnly;
        }

        var lower = word.ToLowerInvariant();
        var sb = new StringBuilder();
        for (int i = 0; i < lower.Length;)
        {
            var matched = false;
            foreach (var (latin, cyr) in Digraphs)
            {
                if (string.CompareOrdinal(lower, i, latin, 0, latin.Length) == 0)
                {
                    sb.Append(cyr);
                    i += latin.Length;
                    matched = true;
                    break;
                }
            }

            if (matched)
            {
                continue;
            }

            var c = lower[i];
            if (c == 'e' && i == lower.Length - 1 && lower.Length > 3)
            {
                i++; // silent final e: "phone" → "фон"
                continue;
            }

            sb.Append(Letters.TryGetValue(c, out var cyrLetter) ? cyrLetter : c.ToString());
            i++;
        }

        var result = sb.ToString();
        return char.IsUpper(word[0]) && result.Length > 0 ? char.ToUpper(result[0], Vedar.Core.Text.BulgarianText.Culture) + result[1..] : result;
    }

    public static string Spell(string letters) =>
        string.Join(' ', letters.Where(char.IsAsciiLetter).Select(c => LetterNames.TryGetValue(char.ToUpperInvariant(c), out var n) ? n : c.ToString()));
}

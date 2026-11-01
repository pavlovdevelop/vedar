using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Vedar.Core.Text;

namespace Vedar.Speech.Text;

/// <summary>
/// Prepares text for the Bulgarian voice: removes Markdown and emoji, expands numbers, dates, times, money,
/// percentages, units, abbreviations, URLs and e-mail addresses, and transliterates Latin words.
/// The goal is that every character that reaches the synthesizer can be pronounced naturally.
/// </summary>
public static class BulgarianTextNormalizer
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private const RegexOptions OptsCi = Opts | RegexOptions.IgnoreCase;
    private const string Months = "януари|февруари|март|април|май|юни|юли|август|септември|октомври|ноември|декември";

    private static readonly Regex CodeFence = new(@"```.*?```", Opts | RegexOptions.Singleline);
    private static readonly Regex MarkdownLink = new(@"!?\[([^\]]*)\]\(([^)]*)\)", Opts);
    private static readonly Regex HtmlTag = new(@"</?[a-zA-Z][^>]{0,200}>", Opts);
    private static readonly Regex ListMarker = new(@"^\s*(?:[-*•+]|\d{1,2}[.)])\s+", Opts | RegexOptions.Multiline);
    private static readonly Regex Heading = new(@"^\s*#{1,6}\s*", Opts | RegexOptions.Multiline);
    private static readonly Regex Emphasis = new(@"(\*\*|__|\*|_|~~|`)(?=\S)(.+?)(?<=\S)\1", Opts);
    private static readonly Regex TableRule = new(@"^\s*\|?\s*:?-{3,}.*$", Opts | RegexOptions.Multiline);
    private static readonly Regex Url = new(@"\b(?:https?://|www\.)[^\s<>""']+", Opts | RegexOptions.IgnoreCase);
    private static readonly Regex Email = new(@"\b[\w.+-]+@[\w-]+(?:\.[\w-]+)+\b", Opts);
    private static readonly Regex NumericDate = new(@"\b(\d{1,2})\.(\d{1,2})\.(\d{4})(?:\s*(?:г\.|година|год\.))?", Opts);
    private static readonly Regex IsoDate = new(@"\b(\d{4})-(\d{2})-(\d{2})\b", Opts);
    private static readonly Regex DayMonth = new($@"\b(\d{{1,2}})(?:-?(?:ви|ри|ти|ми))?\s+({Months})\b", Opts | RegexOptions.IgnoreCase);
    private static readonly Regex Year = new(@"\b(\d{3,4})\s*(?:г\.|год\.|година\b)", Opts);
    private static readonly Regex YearAfterMonth = new($@"({Months})\s+(\d{{4}})\b(?:\s*(?:г\.|год\.|година\b))?", Opts | RegexOptions.IgnoreCase);
    private static readonly Regex Time = new(@"\b([01]?\d|2[0-3]):([0-5]\d)(?::[0-5]\d)?(?:\s*(?:ч\.|часа))?", Opts);
    private static readonly Regex Century = new(@"\b([IVXLC]{1,6})\s*(?:-?(?:ви|ри|ти|ми))?\s+(в\.|век\b|века\b)", Opts);
    private static readonly Regex Percent = new(@"(\d+(?:[.,]\d+)?)\s*%", Opts);
    private static readonly Regex Leva = new(@"(\d+)(?:[.,](\d{1,2}))?\s*(?:лв\.?|лева\b)", Opts);
    private static readonly Regex EuroBefore = new(@"€\s*(\d+)(?:[.,](\d{1,2}))?", Opts);
    private static readonly Regex EuroAfter = new(@"(\d+)(?:[.,](\d{1,2}))?\s*(?:€|EUR\b|евро\b)", Opts);
    private static readonly Regex Dollar = new(@"\$\s*(\d+)(?:[.,](\d{1,2}))?|(\d+)(?:[.,](\d{1,2}))?\s*(?:\$|USD\b|долара\b)", Opts);
    private static readonly Regex Degrees = new(@"(-?\d+(?:[.,]\d+)?)\s*°\s*[CС]?", Opts);
    private static readonly Regex Unit = new(@"(\d+(?:[.,]\d+)?)\s*(км/ч|км|кг|см|мм|мл|кв\.\s*м|м²|GB|MB|KB|ГБ|МБ)(?![\p{L}])", Opts);
    private static readonly Regex OrdinalSuffix = new(@"\b(\d+)-(ви|ри|ти|ми|ия|ва|ра|та|ма|во|ро|то|мо)\b", Opts);
    private static readonly Regex Range = new(@"\b(\d+)\s*[-–—]\s*(\d+)\b", Opts);
    private static readonly Regex Phone = new(@"(?<![\d])(\+\d{1,3}[\s-]?)?0?\d{2,4}(?:[\s-]\d{2,4}){2,4}(?![\d])", Opts);
    private static readonly Regex Thousands = new(@"\b[1-9]\d{0,2}(?:[  ]\d{3})+\b", Opts);
    private static readonly Regex Decimal = new(@"(\d+)[.,](\d+)", Opts);
    private static readonly Regex Fraction = new(@"\b(\d+)/(\d+)\b", Opts);
    private static readonly Regex Integer = new(@"\d+", Opts);
    private static readonly Regex LatinWord = new(@"[A-Za-z][A-Za-z0-9'-]*", Opts);
    private static readonly Regex Spaces = new(@"[ \t ]+", Opts);
    private static readonly Regex SpaceBeforePunct = new(@"\s+([,.!?;:])", Opts);
    private static readonly Regex RepeatedPunct = new(@"([,.!?;:])(?:\s*[,.;:])+", Opts);

    private static readonly (Regex Pattern, string Replacement)[] Abbreviations =
    [
        (new(@"\bи\s+т\.\s*н\.", OptsCi), "и така нататък"),
        (new(@"\bи\s+т\.\s*нат\.", OptsCi), "и така нататък"),
        (new(@"\bи\s+др\.", OptsCi), "и други"),
        (new(@"\bт\.\s*е\.", OptsCi), "тоест"),
        (new(@"\bт\.\s*нар\.", OptsCi), "така наречен"),
        (new(@"\bнапр\.", OptsCi), "например"),
        (new(@"\bг-н\b", OptsCi), "господин"),
        (new(@"\bг-жа\b", OptsCi), "госпожа"),
        (new(@"\bг-ца\b", OptsCi), "госпожица"),
        (new(@"\bд-р\b", OptsCi), "доктор"),
        (new(@"\bпроф\.", OptsCi), "професор"),
        (new(@"\bдоц\.", OptsCi), "доцент"),
        (new(@"\bакад\.", OptsCi), "академик"),
        (new(@"\bинж\.", OptsCi), "инженер"),
        (new(@"\bул\.", OptsCi), "улица"),
        (new(@"\bбул\.", OptsCi), "булевард"),
        (new(@"\bж\.\s*к\.", OptsCi), "жилищен комплекс"),
        (new(@"\bкв\.(?!\s*м)", OptsCi), "квартал"),
        (new(@"\bгр\.", OptsCi), "град"),
        (new(@"\bобл\.", OptsCi), "област"),
        (new(@"\bтел\.", OptsCi), "телефон"),
        (new(@"\bстр\.", OptsCi), "страница"),
        (new(@"\bчл\.", OptsCi), "член"),
        (new(@"\bал\.", OptsCi), "алинея"),
        (new(@"\bмлн\.?(?=\s|$)", OptsCi), "милиона"),
        (new(@"\bмлрд\.?(?=\s|$)", OptsCi), "милиарда"),
        (new(@"\bхил\.", OptsCi), "хиляди"),
        (new(@"\bмин\.", OptsCi), "минути"),
        (new(@"\bсек\.", OptsCi), "секунди"),
        (new(@"\bпр\.\s*Хр\.", OptsCi), "преди Христа"),
        (new(@"\bсл\.\s*Хр\.", OptsCi), "след Христа"),
        (new(@"\bсв\.", OptsCi), "свети"),
        (new(@"\bвкл\.", OptsCi), "включително"),
        (new(@"\bбел\.\s*ред\.", OptsCi), "бележка на редактора"),
    ];

    private static readonly (Regex Pattern, string Replacement)[] Acronyms =
    [
        (new(@"\bЕС\b", Opts), "Е Се"),
        (new(@"\bСАЩ\b", Opts), "Сащ"),
        (new(@"\bООН\b", Opts), "О О Н"),
        (new(@"\bБАН\b", Opts), "Бан"),
        (new(@"\bНАП\b", Opts), "Нап"),
        (new(@"\bНОИ\b", Opts), "Нои"),
        (new(@"\bТЕЛК\b", Opts), "Телк"),
    ];

    private static readonly HashSet<string> FeminineNouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "минута", "минути", "секунда", "секунди", "година", "години", "седмица", "седмици", "страница", "страници", "жена", "жени",
        "книга", "книги", "стотинка", "стотинки", "хиляда", "хиляди", "улица", "улици", "стая", "стаи", "чаша", "чаши", "точка", "точки",
        "крачка", "крачки", "песен", "песни", "буква", "букви", "дума", "думи", "задача", "задачи", "част", "части", "нощ", "нощи", "вечер", "вечери",
        "таблетка", "таблетки", "държава", "държави", "област", "области", "статия", "статии", "глава", "глави", "снимка", "снимки",
    };

    private static readonly HashSet<string> NeuterNouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "евро", "дете", "деца", "място", "места", "писмо", "писма", "яйце", "яйца", "кило", "кила", "село", "села", "име", "имена",
        "число", "числа", "съобщение", "съобщения", "упражнение", "упражнения", "изречение", "изречения", "лице", "лица", "парче", "парчета",
    };

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var s = text.Normalize(NormalizationForm.FormC);
        s = StripMarkup(s);
        s = Url.Replace(s, m => " " + SpeakUrl(m.Value) + " ");
        s = Email.Replace(s, m => " " + SpeakEmail(m.Value) + " ");
        foreach (var (pattern, replacement) in Abbreviations)
        {
            var current = s;
            s = pattern.Replace(current, m => KeepSentenceEnd(current, m, MatchCase(m.Value, replacement)));
        }

        foreach (var (pattern, replacement) in Acronyms)
        {
            s = pattern.Replace(s, replacement);
        }

        var t0 = s;
        s = NumericDate.Replace(t0, m => KeepSentenceEnd(t0, m, onlyAtEnd: true, replacement: SpeakDate(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)) ?? m.Value));
        s = IsoDate.Replace(s, m => SpeakDate(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) ?? m.Value);
        s = DayMonth.Replace(s, m => BulgarianNumbers.Ordinal(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), GrammaticalGender.Masculine) + " " + m.Groups[2].Value.ToLower(BulgarianText.Culture));
        var t1 = s;
        s = YearAfterMonth.Replace(t1, m => KeepSentenceEnd(t1, m, onlyAtEnd: true, replacement: m.Groups[1].Value + " " + BulgarianDateTime.SpokenYear(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))));
        var t2 = s;
        s = Year.Replace(t2, m => KeepSentenceEnd(t2, m, onlyAtEnd: true, replacement: BulgarianDateTime.SpokenYear(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))));
        var t3 = s;
        s = Time.Replace(t3, m => KeepSentenceEnd(t3, m, onlyAtEnd: true, replacement: SpeakTime(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))));
        var beforeCentury = s;
        s = Century.Replace(beforeCentury, m => RomanToInt(m.Groups[1].Value) is int c and > 0
            ? KeepSentenceEnd(beforeCentury, m, BulgarianNumbers.Ordinal(c, GrammaticalGender.Masculine) + " век")
            : m.Value);
        s = Percent.Replace(s, m => SpeakQuantity(m.Groups[1].Value, "процент", "процента", GrammaticalGender.Masculine));
        var t4 = s;
        s = Leva.Replace(t4, m => KeepSentenceEnd(t4, m, onlyAtEnd: true, replacement: SpeakMoney(m.Groups[1].Value, m.Groups[2].Value, "лев", "лева", GrammaticalGender.Masculine)));
        s = EuroBefore.Replace(s, m => SpeakMoney(m.Groups[1].Value, m.Groups[2].Value, "евро", "евро", GrammaticalGender.Neuter, "евроцент", "евроцента"));
        s = EuroAfter.Replace(s, m => SpeakMoney(m.Groups[1].Value, m.Groups[2].Value, "евро", "евро", GrammaticalGender.Neuter, "евроцент", "евроцента"));
        s = Dollar.Replace(s, m => m.Groups[1].Success
            ? SpeakMoney(m.Groups[1].Value, m.Groups[2].Value, "долар", "долара", GrammaticalGender.Masculine, "цент", "цента")
            : SpeakMoney(m.Groups[3].Value, m.Groups[4].Value, "долар", "долара", GrammaticalGender.Masculine, "цент", "цента"));
        s = Degrees.Replace(s, m => SpeakQuantity(m.Groups[1].Value, "градус", "градуса", GrammaticalGender.Masculine));
        s = Unit.Replace(s, m => SpeakUnit(m.Groups[1].Value, m.Groups[2].Value));
        s = OrdinalSuffix.Replace(s, m => SpeakOrdinalSuffix(m.Groups[1].Value, m.Groups[2].Value));
        s = Phone.Replace(s, m => IsPhoneLike(m.Value) ? " " + SpeakDigits(m.Value) + " " : m.Value);
        s = Range.Replace(s, m => $"{m.Groups[1].Value} до {m.Groups[2].Value}");
        s = Thousands.Replace(s, m => m.Value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal));
        s = Fraction.Replace(s, m => $"{m.Groups[1].Value} на {m.Groups[2].Value}");
        s = Decimal.Replace(s, m => SpeakDecimal(m.Groups[1].Value, m.Groups[2].Value, GenderOfNextWord(s, m.Index + m.Length)));
        s = Integer.Replace(s, m => SpeakInteger(m.Value, GenderOfNextWord(s, m.Index + m.Length), IsStandalone(s, m.Index + m.Length)));
        s = ReplaceSymbols(s);
        s = LatinWord.Replace(s, m => LatinTransliterator.Transliterate(m.Value));
        s = Spaces.Replace(s, " ");
        s = SpaceBeforePunct.Replace(s, "$1");
        s = RepeatedPunct.Replace(s, "$1");
        return s.Trim().Trim(',', ';', ':').Trim();
    }

    /// <summary>Removes Markdown, HTML, emoji and control characters; list items become sentences.</summary>
    public static string StripMarkup(string s)
    {
        s = CodeFence.Replace(s, " ");
        s = MarkdownLink.Replace(s, "$1");
        s = HtmlTag.Replace(s, " ");
        s = TableRule.Replace(s, string.Empty);
        s = Heading.Replace(s, string.Empty);
        s = Emphasis.Replace(s, "$2");
        s = Emphasis.Replace(s, "$2");
        s = ListMarker.Replace(s, string.Empty);

        var lines = s.Split('\n');
        var multiLine = lines.Count(l => l.Trim().Length > 0) > 1;
        var sb = new StringBuilder(s.Length);
        foreach (var raw in lines)
        {
            var line = raw.Replace('|', ',').Trim().Trim(',').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            sb.Append(line);
            if (multiLine && !".!?:;…".Contains(line[^1]))
            {
                sb.Append('.');
            }

            sb.Append(' ');
        }

        var result = new StringBuilder(sb.Length);
        var text = sb.ToString();
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsSurrogate(c))
            {
                continue; // emoji and other astral symbols
            }

            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format)
            {
                result.Append(' ');
                continue;
            }

            if (cat == UnicodeCategory.OtherSymbol && c is not ('°' or '№' or '©' or '®'))
            {
                continue;
            }

            result.Append(c switch
            {
                '“' or '”' or '„' or '«' or '»' or '"' => ' ',
                '‘' or '’' or '`' or '´' => '\'',
                '…' => '.',
                ' ' => ' ',
                _ => c,
            });
        }

        return result.ToString();
    }

    /// <summary>
    /// An abbreviation such as "т.н." or "в." also ends the sentence when it is the last word, or when an
    /// upper-case word follows. Its dot is consumed by the replacement, so the sentence-ending dot is restored.
    /// </summary>
    private static string KeepSentenceEnd(string input, Match m, string replacement, bool onlyAtEnd = false)
    {
        if (!m.Value.EndsWith('.'))
        {
            return replacement;
        }

        int i = m.Index + m.Length;
        while (i < input.Length && input[i] == ' ')
        {
            i++;
        }

        if (i >= input.Length || input[i] == '\n')
        {
            return replacement + ".";
        }

        if (onlyAtEnd)
        {
            return replacement;
        }

        // Titles and address words ("проф. Иванов", "ул. Витоша") are followed by a capitalized name.
        return char.IsUpper(input[i]) && !TitleWords.Contains(replacement.ToLower(BulgarianText.Culture)) ? replacement + "." : replacement;
    }

    private static readonly HashSet<string> TitleWords = new(StringComparer.Ordinal)
    {
        "господин", "госпожа", "госпожица", "доктор", "професор", "доцент", "академик", "инженер", "улица", "булевард",
        "жилищен комплекс", "квартал", "град", "област", "свети", "член", "алинея", "страница", "телефон",
    };

    private static string MatchCase(string original, string replacement) =>
        original.Length > 0 && char.IsUpper(original[0]) && replacement.Length > 0
            ? char.ToUpper(replacement[0], BulgarianText.Culture) + replacement[1..]
            : replacement;

    private static string ReplaceSymbols(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '&': sb.Append(" и "); break;
                case '+': sb.Append(" плюс "); break;
                case '=': sb.Append(" равно "); break;
                case '№': sb.Append(" номер "); break;
                case '@': sb.Append(" кльомба "); break;
                case '§': sb.Append(" параграф "); break;
                case '©': sb.Append(" авторско право "); break;
                case '®': break;
                case '×': sb.Append(" по "); break;
                case '÷': sb.Append(" делено на "); break;
                case '<': sb.Append(" по-малко от "); break;
                case '>': sb.Append(" по-голямо от "); break;
                case '—' or '–':
                    sb.Append(", ");
                    break;
                case '-':
                    // Keep hyphens inside words (по-добре), drop dashes between spaces.
                    var inWord = i > 0 && i < s.Length - 1 && char.IsLetter(s[i - 1]) && char.IsLetter(s[i + 1]);
                    sb.Append(inWord ? '-' : ' ');
                    break;
                case '/' or '\\' or '*' or '#' or '_' or '~' or '^' or '|' or '[' or ']' or '{' or '}' or '(' or ')':
                    sb.Append(c is '(' or ')' ? ", " : " ");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    private static string SpeakUrl(string url)
    {
        var u = url.TrimEnd('.', ',', ')', ';', ':', '!', '?');
        if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            u = "https://" + u;
        }

        var host = Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Host : url;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        return "уеб адрес " + string.Join(" точка ", host.Split('.', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string SpeakEmail(string email)
    {
        var parts = email.Split('@');
        var domain = string.Join(" точка ", parts[1].Split('.'));
        var user = parts[0].Replace(".", " точка ", StringComparison.Ordinal).Replace("_", " долна черта ", StringComparison.Ordinal).Replace("-", " тире ", StringComparison.Ordinal);
        return $"{user} кльомба {domain}";
    }

    private static string? SpeakDate(int day, int month, int year)
    {
        if (month is < 1 or > 12 || day is < 1 or > 31)
        {
            return null;
        }

        return $"{BulgarianNumbers.Ordinal(day, GrammaticalGender.Masculine)} {BulgarianDateTime.Months[month - 1]} {BulgarianDateTime.SpokenYear(year)}";
    }

    private static string SpeakTime(int h, int m) =>
        m == 0 ? $"{BulgarianNumbers.Cardinal(h)} часа" : $"{BulgarianNumbers.Cardinal(h)} и {BulgarianNumbers.Cardinal(m, GrammaticalGender.Feminine)}";

    private static string SpeakQuantity(string number, string singular, string plural, GrammaticalGender gender)
    {
        if (number.Contains(',') || number.Contains('.'))
        {
            var parts = number.Split(',', '.');
            return SpeakDecimal(parts[0], parts[1], gender) + " " + plural;
        }

        var negative = number.StartsWith('-');
        var n = long.Parse(number.TrimStart('-'), CultureInfo.InvariantCulture);
        var words = n == 1 ? BulgarianNumbers.Cardinal(1, gender) + " " + singular : BulgarianNumbers.Cardinal(n, gender) + " " + plural;
        return negative ? "минус " + words : words;
    }

    private static string SpeakMoney(string whole, string cents, string singular, string plural, GrammaticalGender gender, string centSingular = "стотинка", string centPlural = "стотинки")
    {
        var main = SpeakQuantity(whole, singular, plural, gender);
        if (string.IsNullOrEmpty(cents))
        {
            return main;
        }

        var c = int.Parse(cents.Length == 1 ? cents + "0" : cents, CultureInfo.InvariantCulture);
        if (c == 0)
        {
            return main;
        }

        var centGender = centSingular == "стотинка" ? GrammaticalGender.Feminine : GrammaticalGender.Masculine;
        return $"{main} и {(c == 1 ? BulgarianNumbers.Cardinal(1, centGender) + " " + centSingular : BulgarianNumbers.Cardinal(c, centGender) + " " + centPlural)}";
    }

    private static string SpeakUnit(string number, string unit)
    {
        var (singular, plural, gender) = unit.Replace(" ", string.Empty, StringComparison.Ordinal) switch
        {
            "км/ч" => ("километър в час", "километра в час", GrammaticalGender.Masculine),
            "км" => ("километър", "километра", GrammaticalGender.Masculine),
            "кг" => ("килограм", "килограма", GrammaticalGender.Masculine),
            "см" => ("сантиметър", "сантиметра", GrammaticalGender.Masculine),
            "мм" => ("милиметър", "милиметра", GrammaticalGender.Masculine),
            "мл" => ("милилитър", "милилитра", GrammaticalGender.Masculine),
            "кв.м" or "м²" => ("квадратен метър", "квадратни метра", GrammaticalGender.Masculine),
            "GB" or "ГБ" => ("гигабайт", "гигабайта", GrammaticalGender.Masculine),
            "MB" or "МБ" => ("мегабайт", "мегабайта", GrammaticalGender.Masculine),
            "KB" => ("килобайт", "килобайта", GrammaticalGender.Masculine),
            _ => (unit, unit, GrammaticalGender.Masculine),
        };
        return SpeakQuantity(number, singular, plural, gender);
    }

    private static string SpeakOrdinalSuffix(string number, string suffix)
    {
        var n = long.Parse(number, CultureInfo.InvariantCulture);
        var gender = suffix[^1] switch { 'а' => GrammaticalGender.Feminine, 'о' => GrammaticalGender.Neuter, _ => GrammaticalGender.Masculine };
        return BulgarianNumbers.Ordinal(n, gender);
    }

    private static bool IsPhoneLike(string value)
    {
        var digits = value.Count(char.IsAsciiDigit);
        var trimmed = value.TrimStart();
        return digits >= 7 && (trimmed.StartsWith('+') || trimmed.StartsWith('0'));
    }

    private static string SpeakDigits(string value)
    {
        var sb = new StringBuilder();
        if (value.TrimStart().StartsWith('+'))
        {
            sb.Append("плюс ");
        }

        var groups = Regex.Split(value.Trim().TrimStart('+'), @"[\s-]+").Where(g => g.Length > 0);
        sb.Append(string.Join(", ", groups.Select(BulgarianNumbers.Digits)));
        return sb.ToString();
    }

    private static string SpeakDecimal(string whole, string fraction, GrammaticalGender gender)
    {
        var w = long.Parse(whole, CultureInfo.InvariantCulture);
        var wholeWords = BulgarianNumbers.Cardinal(w, GrammaticalGender.Feminine);
        var fracWords = fraction.StartsWith('0') && fraction.Length > 1
            ? BulgarianNumbers.Digits(fraction)
            : BulgarianNumbers.Cardinal(long.Parse(fraction.Length > 9 ? fraction[..9] : fraction, CultureInfo.InvariantCulture), GrammaticalGender.Feminine);
        _ = gender;
        return $"{wholeWords} цяло и {fracWords}";
    }

    private static string SpeakInteger(string digits, GrammaticalGender gender, bool standalone)
    {
        if (digits.Length > 1 && digits[0] == '0')
        {
            return BulgarianNumbers.Digits(digits);
        }

        if (digits.Length > 12)
        {
            return BulgarianNumbers.Digits(digits);
        }

        var n = long.Parse(digits, CultureInfo.InvariantCulture);
        // Standalone counting ("1, 2, 3") uses neuter forms: едно, две.
        return BulgarianNumbers.Cardinal(n, standalone ? GrammaticalGender.Neuter : gender);
    }

    private static GrammaticalGender GenderOfNextWord(string s, int index)
    {
        var word = NextWord(s, index);
        if (word is null)
        {
            return GrammaticalGender.Masculine;
        }

        if (FeminineNouns.Contains(word))
        {
            return GrammaticalGender.Feminine;
        }

        return NeuterNouns.Contains(word) ? GrammaticalGender.Neuter : GrammaticalGender.Masculine;
    }

    private static bool IsStandalone(string s, int index)
    {
        var word = NextWord(s, index);
        return word is null || !word.Any(BulgarianText.IsCyrillic);
    }

    private static string? NextWord(string s, int index)
    {
        int i = index;
        while (i < s.Length && s[i] == ' ')
        {
            i++;
        }

        int start = i;
        while (i < s.Length && char.IsLetter(s[i]))
        {
            i++;
        }

        return i > start ? s[start..i] : null;
    }

    private static int RomanToInt(string roman)
    {
        var map = new Dictionary<char, int> { ['I'] = 1, ['V'] = 5, ['X'] = 10, ['L'] = 50, ['C'] = 100 };
        int total = 0;
        for (int i = 0; i < roman.Length; i++)
        {
            if (!map.TryGetValue(roman[i], out var v))
            {
                return 0;
            }

            var next = i + 1 < roman.Length && map.TryGetValue(roman[i + 1], out var n) ? n : 0;
            total += v < next ? -v : v;
        }

        return total;
    }
}

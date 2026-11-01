using Vedar.Speech.Text;

namespace Vedar.Tests.Speech;

public class BulgarianTextNormalizerTests
{
    [Theory]
    [InlineData("Имам 3 въпроса.", "Имам три въпроса.")]
    [InlineData("Чакай 2 минути.", "Чакай две минути.")]
    [InlineData("Купих 1 книга.", "Купих една книга.")]
    [InlineData("Броим: 1, 2, 3.", "Броим: едно, две, три.")]
    [InlineData("Населението е 6 500 000 души.", "Населението е шест милиона и петстотин хиляди души.")]
    [InlineData("Срещата е в 14:30.", "Срещата е в четиринадесет и тридесет.")]
    [InlineData("Ела в 9:00 ч.", "Ела в девет часа.")]
    [InlineData("Роден на 01.11.2026 г.", "Роден на първи ноември две хиляди двадесет и шеста година.")]
    [InlineData("На 3 март 1878 г. България е освободена.", "На трети март хиляда осемстотин седемдесет и осма година България е освободена.")]
    [InlineData("През 2026 г. е топло.", "През две хиляди двадесет и шеста година е топло.")]
    [InlineData("Отстъпка 25%.", "Отстъпка двадесет и пет процента.")]
    [InlineData("Струва 1%.", "Струва един процент.")]
    [InlineData("Цена 12,50 лв.", "Цена дванадесет лева и петдесет стотинки.")]
    [InlineData("Струва 5 €.", "Струва пет евро.")]
    [InlineData("Температурата е -3 °C.", "Температурата е минус три градуса.")]
    [InlineData("Разстоянието е 120 км.", "Разстоянието е сто и двадесет километра.")]
    [InlineData("Числото е 3,14.", "Числото е три цяло и четиринадесет.")]
    [InlineData("Това е XX в.", "Това е двадесети век.")]
    [InlineData("Той завърши 2-ри.", "Той завърши втори.")]
    [InlineData("Отворено 9-18 ч.", "Отворено девет до осемнадесет ч.")]
    public void Expands_numbers_dates_times_money_and_units(string input, string expected) =>
        Assert.Equal(expected, BulgarianTextNormalizer.Normalize(input));

    [Theory]
    [InlineData("Т.е. това е напр. пример.", "тоест това е например пример.")]
    [InlineData("Живее на ул. Витоша, гр. София.", "Живее на улица Витоша, град София.")]
    [InlineData("Д-р Петров и проф. Иванов.", "доктор Петров и професор Иванов.")]
    [InlineData("Ябълки, круши и т.н.", "Ябълки, круши и така нататък.")]
    public void Expands_abbreviations(string input, string expected) =>
        Assert.Equal(expected, BulgarianTextNormalizer.Normalize(input), ignoreCase: true);

    [Fact]
    public void Reads_urls_as_domain_names()
    {
        var text = BulgarianTextNormalizer.Normalize("Виж https://www.bnr.bg/sofia/post/123 за повече.");
        Assert.Equal("Виж уеб адрес бнр точка бг за повече.", text);
    }

    [Fact]
    public void Reads_email_addresses()
    {
        var text = BulgarianTextNormalizer.Normalize("Пиши на ivan.petrov@abv.bg днес.");
        Assert.Contains("кльомба", text);
        Assert.Contains("точка бг", text);
        Assert.DoesNotContain("@", text);
    }

    [Fact]
    public void Reads_phone_numbers_digit_by_digit()
    {
        var text = BulgarianTextNormalizer.Normalize("Телефонът е 0888 123 456.");
        Assert.Equal("Телефонът е нула осем осем осем, едно две три, четири пет шест.", text);
    }

    [Fact]
    public void Removes_markdown_and_emoji_and_turns_list_items_into_sentences()
    {
        var md = "## Заглавие\n**Важно:** това е *курсив* 😀\n- първа точка\n- втора точка\n```\ncode\n```\n[връзка](https://x.y)";
        var text = BulgarianTextNormalizer.Normalize(md);
        Assert.DoesNotContain("*", text);
        Assert.DoesNotContain("#", text);
        Assert.DoesNotContain("`", text);
        Assert.DoesNotContain("😀", text);
        Assert.DoesNotContain("code", text);
        Assert.Contains("Заглавие.", text);
        Assert.Contains("първа точка.", text);
        Assert.Contains("втора точка.", text);
        Assert.Contains("връзка", text);
    }

    [Theory]
    [InlineData("Използвам BgGPT, а не ChatGPT.", "Използвам Би Джи Джи Пи Ти, а не Чат Джи Пи Ти.")]
    [InlineData("Отвори Google.", "Отвори Гугъл.")]
    [InlineData("Работи с NVDA и Narrator.", "Работи с Ен Ви Ди Ей и Наратор.")]
    [InlineData("Свържи USB кабела.", "Свържи Ю Ес Би кабела.")]
    public void Transliterates_latin_words(string input, string expected) =>
        Assert.Equal(expected, BulgarianTextNormalizer.Normalize(input));

    [Fact]
    public void Output_contains_no_digits_or_latin_letters()
    {
        var text = BulgarianTextNormalizer.Normalize("В 2026 г. VEDAR ще има 12 нови функции на цена 0 лв. и 99,9% точност, вж. www.example.com.");
        Assert.DoesNotContain(text, char.IsAsciiDigit);
        Assert.DoesNotContain(text, char.IsAsciiLetter);
    }

    [Fact]
    public void Symbols_are_spoken()
    {
        Assert.Equal("Том и Джери.", BulgarianTextNormalizer.Normalize("Том & Джери."));
        Assert.Equal("две плюс две равно четири", BulgarianTextNormalizer.Normalize("2 + 2 = 4"));
    }

    [Fact]
    public void Empty_input_gives_empty_output()
    {
        Assert.Equal(string.Empty, BulgarianTextNormalizer.Normalize(null));
        Assert.Equal(string.Empty, BulgarianTextNormalizer.Normalize("   "));
    }
}

public class LatinTransliteratorTests
{
    [Theory]
    [InlineData("VEDAR", "Ведар")]
    [InlineData("YouTube", "Ютюб")]
    [InlineData("BBC", "Би Би Си")]
    [InlineData("Peter", "Петер")]
    [InlineData("phone", "фон")]
    [InlineData("Sharp", "Шарп")]
    public void Transliterate(string latin, string expected) => Assert.Equal(expected, LatinTransliterator.Transliterate(latin));
}

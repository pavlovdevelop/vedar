using Vedar.Core.Abstractions;
using Vedar.Core.Text;

namespace Vedar.Tests.Core;

public class BulgarianNumbersTests
{
    [Theory]
    [InlineData(0, "нула")]
    [InlineData(1, "един")]
    [InlineData(2, "два")]
    [InlineData(11, "единадесет")]
    [InlineData(19, "деветнадесет")]
    [InlineData(20, "двадесет")]
    [InlineData(21, "двадесет и един")]
    [InlineData(100, "сто")]
    [InlineData(101, "сто и един")]
    [InlineData(115, "сто и петнадесет")]
    [InlineData(120, "сто и двадесет")]
    [InlineData(125, "сто двадесет и пет")]
    [InlineData(200, "двеста")]
    [InlineData(999, "деветстотин деветдесет и девет")]
    [InlineData(1000, "хиляда")]
    [InlineData(1005, "хиляда и пет")]
    [InlineData(1100, "хиляда и сто")]
    [InlineData(1125, "хиляда сто двадесет и пет")]
    [InlineData(2000, "две хиляди")]
    [InlineData(2026, "две хиляди двадесет и шест")]
    [InlineData(21000, "двадесет и една хиляди")]
    [InlineData(1_000_000, "един милион")]
    [InlineData(1_200_000, "един милион и двеста хиляди")]
    [InlineData(2_500_000, "два милиона и петстотин хиляди")]
    [InlineData(-5, "минус пет")]
    public void Cardinal_masculine(long n, string expected) => Assert.Equal(expected, BulgarianNumbers.Cardinal(n));

    [Theory]
    [InlineData(1, GrammaticalGender.Feminine, "една")]
    [InlineData(2, GrammaticalGender.Feminine, "две")]
    [InlineData(1, GrammaticalGender.Neuter, "едно")]
    [InlineData(22, GrammaticalGender.Feminine, "двадесет и две")]
    public void Cardinal_gender(long n, GrammaticalGender g, string expected) => Assert.Equal(expected, BulgarianNumbers.Cardinal(n, g));

    [Theory]
    [InlineData(1, GrammaticalGender.Masculine, "първи")]
    [InlineData(2, GrammaticalGender.Masculine, "втори")]
    [InlineData(3, GrammaticalGender.Feminine, "трета")]
    [InlineData(4, GrammaticalGender.Masculine, "четвърти")]
    [InlineData(7, GrammaticalGender.Masculine, "седми")]
    [InlineData(8, GrammaticalGender.Neuter, "осмо")]
    [InlineData(11, GrammaticalGender.Masculine, "единадесети")]
    [InlineData(20, GrammaticalGender.Masculine, "двадесети")]
    [InlineData(21, GrammaticalGender.Masculine, "двадесет и първи")]
    [InlineData(31, GrammaticalGender.Masculine, "тридесет и първи")]
    [InlineData(100, GrammaticalGender.Masculine, "стотен")]
    [InlineData(2000, GrammaticalGender.Feminine, "двехилядна")]
    [InlineData(2001, GrammaticalGender.Feminine, "две хиляди и първа")]
    [InlineData(2010, GrammaticalGender.Feminine, "две хиляди и десета")]
    [InlineData(2026, GrammaticalGender.Feminine, "две хиляди двадесет и шеста")]
    [InlineData(1989, GrammaticalGender.Feminine, "хиляда деветстотин осемдесет и девета")]
    [InlineData(1900, GrammaticalGender.Feminine, "хиляда и деветстотна")]
    public void Ordinal(long n, GrammaticalGender g, string expected) => Assert.Equal(expected, BulgarianNumbers.Ordinal(n, g));

    [Fact]
    public void Digits_are_read_one_by_one() => Assert.Equal("нула осем осем осем", BulgarianNumbers.Digits("0888"));

    [Theory]
    [InlineData(1, "една минута")]
    [InlineData(2, "две минути")]
    [InlineData(35, "тридесет и пет минути")]
    public void Count_feminine(long n, string expected) => Assert.Equal(expected, BulgarianNumbers.Count(n, "минута", "минути", GrammaticalGender.Feminine));
}

public class BulgarianDateTimeTests
{
    [Theory]
    [InlineData(14, 35, "четиринадесет и тридесет и пет минути")]
    [InlineData(9, 0, "точно девет")]
    [InlineData(13, 1, "тринадесет и една минута")]
    [InlineData(0, 5, "нула и пет минути")]
    public void SpokenTime(int h, int m, string expected) => Assert.Equal(expected, BulgarianDateTime.SpokenTime(h, m));

    [Fact]
    public void SpokenDate_includes_weekday_ordinal_day_month_and_year()
    {
        var text = BulgarianDateTime.SpokenDate(new DateTime(2026, 11, 1));
        Assert.Equal("неделя, първи ноември две хиляди двадесет и шеста година", text);
    }
}

public class FuzzyAndConfirmationTests
{
    [Theory]
    [InlineData("часът", "часът", true)]
    [InlineData("чесат", "часът", true)]
    [InlineData("браузера", "браузъра", true)]
    [InlineData("часовете", "часът", false)]
    [InlineData("спирай", "спри", false)]
    [InlineData("да", "не", false)]
    public void TokenMatches(string spoken, string expected, bool match) => Assert.Equal(match, FuzzyMatcher.TokenMatches(spoken, expected));

    [Theory]
    [InlineData("Да", ConfirmationAnswer.Yes)]
    [InlineData("да, сигурен съм", ConfirmationAnswer.Yes)]
    [InlineData("добре", ConfirmationAnswer.Yes)]
    [InlineData("Не", ConfirmationAnswer.No)]
    [InlineData("не съм сигурен", ConfirmationAnswer.No)]
    [InlineData("откажи", ConfirmationAnswer.No)]
    [InlineData("какво?", ConfirmationAnswer.Unknown)]
    [InlineData("", ConfirmationAnswer.Unknown)]
    public void ConfirmationParser_understands_yes_no(string text, ConfirmationAnswer expected) => Assert.Equal(expected, ConfirmationParser.Parse(text));

    [Fact]
    public void NormalizeForMatching_lowercases_and_strips_punctuation() =>
        Assert.Equal("ведар колко е часът", BulgarianText.NormalizeForMatching("Ведар, колко е ЧАСЪТ?!"));
}

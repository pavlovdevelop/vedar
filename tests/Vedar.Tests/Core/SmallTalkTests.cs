using Vedar.Core.Conversation;
using Vedar.Core.Localization;
using Vedar.Core.Text;
using Vedar.Tests.Support;

namespace Vedar.Tests.Core;

public class SmallTalkTests
{
    private static readonly Localizer Bg = Localizer.Load("bg");

    private static (SmallTalk Talk, FixedClock Clock) Create(int hour)
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 2, hour, 15, 0, TimeSpan.FromHours(2)));
        return (new SmallTalk(Localizer.Load("bg", new Random(3)), clock), clock);
    }

    private static IEnumerable<string> Variants(string key, params object[] args) =>
        Bg.Variants(key).Select(v => args.Length == 0 ? v : string.Format(v, args));

    [Theory]
    [InlineData(6, "Добро утро")]
    [InlineData(10, "Добро утро")]
    [InlineData(12, "Добър ден")]
    [InlineData(19, "Добър вечер")]
    [InlineData(23, "Добър вечер")]
    [InlineData(2, "Здравей")]
    public void Greets_by_the_time_of_day(int hour, string expected) => Assert.Equal(expected, Create(hour).Talk.TimeGreeting());

    [Fact]
    public void After_its_own_hello_VEDAR_does_not_say_hello_again_but_asks_how_the_user_is()
    {
        var (talk, _) = Create(20);
        Assert.StartsWith("Добър вечер, Павел!", talk.StartupGreeting("Павел"));
        Assert.False(talk.AwaitsFeeling);

        var reply = talk.ReplyToGreeting("Здравей, Ведар!", "Павел");
        Assert.Contains(reply, Variants("smalltalk.greetedBack", ", Павел"));
        Assert.True(talk.AwaitsFeeling);
    }

    [Fact]
    public void Answers_the_greeting_the_user_used()
    {
        var (talk, _) = Create(13);
        Assert.StartsWith("Добро утро!", talk.ReplyToGreeting("Добро утро.", null));
        var (other, _) = Create(13);
        Assert.StartsWith("Здравей, Мария!", other.ReplyToGreeting("Здрасти!", "Мария"));
    }

    [Fact]
    public void How_are_you_with_a_greeting_greets_first_and_asks_back()
    {
        var (talk, _) = Create(9);
        var reply = talk.ReplyToHowAreYou("Здравей, как си?", "Иван");
        Assert.StartsWith("Здравей, Иван! ", reply);
        Assert.Contains(Variants("speech.howAreYou"), v => reply.EndsWith(v, StringComparison.Ordinal));
        Assert.True(talk.AwaitsFeeling);
    }

    [Theory]
    [InlineData("Добре съм, благодаря.", Feeling.Good)]
    [InlineData("Чудесно!", Feeling.Good)]
    [InlineData("Не съм добре.", Feeling.Bad)]
    [InlineData("Уморен съм.", Feeling.Bad)]
    [InlineData("Зле.", Feeling.Bad)]
    [InlineData("Горе-долу.", Feeling.SoSo)]
    [InlineData("Нормално.", Feeling.SoSo)]
    [InlineData("Не се оплаквам.", Feeling.SoSo)]
    [InlineData("Колко е часът?", Feeling.Unknown)]
    public void Understands_how_the_user_is(string heard, Feeling expected) => Assert.Equal(expected, SmallTalk.ClassifyFeeling(heard));

    [Fact]
    public void The_answer_to_how_are_you_is_understood_only_right_after_the_question()
    {
        var (talk, clock) = Create(11);
        Assert.Null(talk.ReplyToFeeling("Добре съм.", aiAvailable: true)); // nobody asked

        talk.ReplyToHowAreYou("Как си?", null);
        var reply = talk.ReplyToFeeling("Добре съм, а ти?", aiAvailable: true);
        Assert.NotNull(reply);
        Assert.StartsWith(Bg.Get("smalltalk.meToo"), reply);
        Assert.False(talk.AwaitsFeeling);

        talk.ReplyToHowAreYou("Как си?", null);
        clock.Now = clock.Now.AddMinutes(5);
        Assert.Null(talk.ReplyToFeeling("Добре съм.", aiAvailable: true)); // too long ago
    }

    [Fact]
    public void Feeling_bad_is_left_to_the_AI_when_it_is_available()
    {
        var (talk, _) = Create(11);
        talk.ReplyToHowAreYou("Как си?", null);
        Assert.Null(talk.ReplyToFeeling("Зле съм.", aiAvailable: true));

        talk.ReplyToHowAreYou("Как си?", null);
        Assert.Contains(talk.ReplyToFeeling("Зле съм.", aiAvailable: false), Variants("smalltalk.feeling.bad"));
    }

    [Theory]
    [InlineData(10, "smalltalk.farewell.day")]
    [InlineData(19, "smalltalk.farewell.evening")]
    [InlineData(22, "smalltalk.farewell.night")]
    public void Goodbye_fits_the_time_of_day(int hour, string key) =>
        Assert.Contains(Create(hour).Talk.Farewell("Довиждане.", "Павел"), Variants(key, ", Павел"));

    [Fact]
    public void Good_night_is_answered_with_good_night_at_any_hour() =>
        Assert.StartsWith("Лека нощ", Create(15).Talk.Farewell("Лека нощ!", null));

    [Theory]
    // Measured with the real models: the main model's and the fast model's transcripts of the same audio.
    [InlineData("колко е часът", "кокочасот", true)]
    [InlineData("отвори настройките", "путворена стреките", true)]
    [InlineData("на", "да", true)]
    [InlineData("добре сен", "добрасен", true)]
    [InlineData("слъпи", "пърж", false)]  // white noise
    [InlineData("ви", "побей", false)]     // the end of a word
    [InlineData("към пършата", "", false)] // keyboard clicks
    [InlineData("слъпи", "сържен в тези", false)] // near silence (the voice check also rejects it)
    [InlineData("отвори", "оте", false)]  // "Утре" said; the main model heard a word from its examples
    [InlineData("добре съм", "добре съм благодаря", true)] // one model heard a word more
    public void Two_transcripts_of_real_speech_agree_and_of_a_noise_do_not(string main, string fast, bool agree) =>
        Assert.Equal(agree, SpeechAgreement.Similarity(main, fast) >= SpeechAgreement.Threshold);

    [Theory]
    [InlineData("Софийският университет е основан през хиляда осемстотин осемдесет и осма година.", true)]
    [InlineData("университет е основан през осемдесет и осма", true)]
    [InlineData("Добре съм.", false)]                                 // short replies often repeat VEDAR's words
    [InlineData("А кой е основал Софийския университет?", false)]
    public void Recognizes_VEDARs_own_words_coming_back(string heard, bool echo) =>
        Assert.Equal(echo, EchoDetector.IsLikelyEcho(heard, "Софийският университет е основан през хиляда осемстотин осемдесет и осма година."));

}

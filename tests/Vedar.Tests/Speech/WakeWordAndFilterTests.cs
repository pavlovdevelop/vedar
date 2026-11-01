using Vedar.Speech.Recognition;
using Vedar.Speech.WakeWord;

namespace Vedar.Tests.Speech;

public class WakeWordMatcherTests
{
    private readonly WakeWordMatcher _matcher = new();

    [Theory]
    [InlineData("Ведар, колко е часът?", "колко е часът?")]
    [InlineData("ведар отвори браузъра", "отвори браузъра")]
    [InlineData("Видар, коя дата сме?", "коя дата сме?")]
    [InlineData("Ведър, пусни музика.", "пусни музика.")]
    [InlineData("Ведаре, спри.", "спри.")]
    [InlineData("Хей, Ведар, повтори.", "повтори.")]
    [InlineData("Ей Ведар искам да поговорим", "искам да поговорим")]
    [InlineData("Vedar, open the browser", "open the browser")]
    [InlineData("Въдар, помощ", "помощ")]
    [InlineData("Ведар.", "")]
    [InlineData("Ведар", "")]
    public void Recognizes_wake_word_variants_and_extracts_command(string transcript, string command)
    {
        var m = _matcher.Match(transcript);
        Assert.True(m.IsMatch, transcript);
        Assert.Equal(command, m.Command);
    }

    [Theory]
    [InlineData("Ведро е днес.")]
    [InlineData("Вътре е топло.")]
    [InlineData("Вятърът духа силно.")]
    [InlineData("Бодър съм тази сутрин.")]
    [InlineData("Петър отвори вратата.")]
    [InlineData("Днес ще ходя при Ведар.")]
    [InlineData("Видра плува в реката.")]
    [InlineData("Колко е часът?")]
    [InlineData("")]
    [InlineData("Абонирайте се за канала.")]
    public void Rejects_similar_words_and_wake_word_in_the_middle(string transcript) =>
        Assert.False(_matcher.Match(transcript).IsMatch, transcript);
}

public class HallucinationFilterTests
{
    [Theory]
    [InlineData("Абонирайте се за канала!")]
    [InlineData("Субтитри от Иван Петров")]
    [InlineData("Субтитры сделал DimaTorzok")]
    [InlineData("Thank you for watching.")]
    [InlineData("[Музика]")]
    [InlineData("♪ ♪ ♪")]
    [InlineData("you")]
    [InlineData("...")]
    public void Removes_typical_whisper_hallucinations(string text) => Assert.Equal(string.Empty, HallucinationFilter.Clean(text));

    [Theory]
    [InlineData("Ведар, колко е часът?", "Ведар, колко е часът?")]
    [InlineData("Отвори браузъра [смях]", "Отвори браузъра")]
    [InlineData("да да да да да да", "да")]
    public void Keeps_real_speech(string text, string expected) => Assert.Equal(expected, HallucinationFilter.Clean(text));
}

public class PromptEchoTests
{
    [Theory]
    [InlineData("Ведар, колко е часът? Коя дата сме? Отвори браузъра.", true)]
    [InlineData("Отвори браузъра. Отвори бележника.", true)]
    [InlineData("Ведар, колко е часът?", false)]
    [InlineData("Отвори браузъра.", false)]
    [InlineData("Разкажи ми за Пирин.", false)]
    public void Detects_whisper_repeating_the_command_prompt(string text, bool echo) =>
        Assert.Equal(echo, WhisperSpeechRecognizer.IsPromptEcho(text, Vedar.Core.Abstractions.RecognitionPrompts.Commands));
}

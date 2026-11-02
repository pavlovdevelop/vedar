using Vedar.Core.Text;
using Vedar.Speech.WakeWord;

namespace Vedar.Tests.Core;

public class StopPhraseDetectorTests
{
    private static readonly WakeWordMatcher Wake = new();

    private const string Answer = "Софийският университет е основан през хиляда осемстотин осемдесет и осма година. Той е най-старото висше училище в България.";

    [Theory]
    [InlineData("Ведар, спри!")]
    [InlineData("Спри.")]
    [InlineData("Стоп.")]
    [InlineData("Стига, стига.")]
    [InlineData("Ведар, млъкни.")]
    [InlineData("Ведар спри")]
    [InlineData("Веда, спри.")] // the usual Whisper misspellings of the wake word
    [InlineData("Достатъчно, благодаря.")]
    public void Short_stop_phrases_stop(string heard) => Assert.True(StopPhraseDetector.IsStopRequest(heard, Answer, Wake.IsWakeToken));

    [Fact]
    public void Stop_mixed_into_our_own_voice_needs_the_wake_word()
    {
        // VEDAR's voice through the speakers and the user's "Ведар, спри" in the same piece of audio.
        Assert.True(StopPhraseDetector.IsStopRequest("Той е най-старото висше Ведар спри училище в България", Answer, Wake.IsWakeToken));
        // A stray stop word inside a long piece is not enough.
        Assert.False(StopPhraseDetector.IsStopRequest("Той е най-старото висше стига училище в България", Answer, Wake.IsWakeToken));
    }

    [Theory]
    [InlineData("Софийският университет е основан през хиляда осемстотин")]
    [InlineData("Той е най-старото висше училище.")]
    [InlineData("")]
    [InlineData("Ведар")]
    [InlineData("Колко е часът?")]
    public void Our_own_words_and_other_speech_do_not_stop(string heard) =>
        Assert.False(StopPhraseDetector.IsStopRequest(heard, Answer, Wake.IsWakeToken));

    [Fact]
    public void A_stop_word_that_VEDAR_itself_says_is_echo()
    {
        const string help = "Докато говоря, можеш да кажеш „Ведар, спри“ или да натиснеш клавиша за спиране.";
        Assert.False(StopPhraseDetector.IsStopRequest("Ведар, спри", help, Wake.IsWakeToken));
        // Whisper may spell an echo slightly differently: "спря" in our speech heard as "спри".
        Assert.False(StopPhraseDetector.IsStopRequest("Дъждът спри.", "Дъждът спря.", Wake.IsWakeToken));
    }

    [Theory]
    [InlineData("Сприк")]                     // "Спри!" alone, as the fast model heard it
    [InlineData("Ведар из тига.")]            // "Ведар, стига!"
    [InlineData("Ведар е сприк.")]            // over VEDAR's own voice
    [InlineData("ведар изприт.")]
    [InlineData("ведар инсприт и осмогуди.")]
    [InlineData("Ето университеля дар, сприве.")]
    public void Garbled_stop_words_measured_with_the_real_models_still_stop(string heard) =>
        Assert.True(StopPhraseDetector.IsStopRequest(heard, Answer, Wake.IsWakeToken), heard);

    [Fact]
    public void Vedar_with_another_request_interrupts()
    {
        Assert.Equal(BargeInAction.Interrupt, StopPhraseDetector.Classify("Ведар, колко е часът?", Answer, Wake.IsWakeToken, startsWithWakeWord: true));
        // Our own name in our own speech is not the user calling.
        Assert.Equal(BargeInAction.None, StopPhraseDetector.Classify("Ведар продължава да работи", "Ведар продължава да работи във фонов режим.", Wake.IsWakeToken, startsWithWakeWord: true));
    }

    [Fact]
    public void Vedar_stop_works_even_while_VEDAR_mentions_the_word_stop()
    {
        // The spoken help says: ... „спри“, за да млъкна. The user's own "Ведар" before "спри" still counts.
        const string help = "Кажи „искам да поговорим“ за разговор с изкуствен интелект, а „спри“, за да млъкна.";
        Assert.True(StopPhraseDetector.IsStopRequest("Ведар, спри!", help, Wake.IsWakeToken));
        Assert.False(StopPhraseDetector.IsStopRequest("Спри.", help, Wake.IsWakeToken)); // could be our own voice
    }
}

public class UtteranceCompletenessTests
{
    [Theory]
    [InlineData("Искам да говорим за", true)]
    [InlineData("Разкажи ми нещо и", true)]
    [InlineData("Ами…", true)]
    [InlineData("Кога е основан", false)]
    [InlineData("Искам да говорим за историята на България.", false)]
    [InlineData("Колко е часът?", false)]
    [InlineData("", false)]
    public void Recognises_cut_off_sentences(string text, bool unfinished) =>
        Assert.Equal(unfinished, Vedar.Core.Text.UtteranceCompleteness.LooksUnfinished(text));
}

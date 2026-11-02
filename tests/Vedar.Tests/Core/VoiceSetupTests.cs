using Vedar.Core.Abstractions;
using Vedar.Core.Setup;
using Vedar.Core.State;
using Vedar.Tests.Support;

namespace Vedar.Tests.Core;

/// <summary>The first-run setup by voice: a blind user answers questions and never has to type.</summary>
public class VoiceSetupTests
{
    private const string BgGptKey = "bgk-5f2a9c71e0d34b8aa6c1";

    private static OrchestratorHarness FirstRun() => new(s => { s.General.FirstRunCompleted = false; s.General.GreetOnStartup = true; });

    /// <summary>Answers the next question as if the user had spoken (after the listening signal).</summary>
    private static async Task AnswerAsync(OrchestratorHarness h, string spoken)
    {
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && !h.Listener.Suppressed && !h.Orchestrator.IsTurnActive && h.Orchestrator.State == AssistantState.Listening, 5000, "listening for the answer");
        h.Recognizer.Enqueue(spoken);
        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => !h.Orchestrator.IsTurnActive, 5000, "answer handled");
    }

    [Fact]
    public async Task Whole_setup_by_voice_name_speed_autostart_and_key_from_the_clipboard()
    {
        await using var h = FirstRun();
        await h.StartAsync();
        Assert.True(h.Orchestrator.CanRunVoiceSetup);
        Assert.DoesNotContain("Слушам те", h.Spoken); // no separate greeting on the first run: the setup greets

        await h.Orchestrator.StartVoiceSetupAsync();
        Assert.True(h.Orchestrator.InVoiceSetup);
        Assert.Contains("само с гласа", h.Spoken);
        Assert.Contains("Как се казваш?", h.Spoken);

        await AnswerAsync(h, "Казвам се Стевко.");
        Assert.Contains("Да те наричам ли Стевко?", h.Spoken);
        await AnswerAsync(h, "Да.");
        Assert.Equal("Стевко", h.Settings.Current.General.UserName);
        Assert.Contains("Приятно ми е, Стевко!", h.Spoken);

        var rate = h.Settings.Current.Voice.Rate;
        await AnswerAsync(h, "По-бързо.");
        Assert.True(h.Settings.Current.Voice.Rate > rate);
        await AnswerAsync(h, "Добре.");
        Assert.Contains("Ведар, спри", h.Spoken); // how to stop VEDAR was explained

        await AnswerAsync(h, "Да.");
        Assert.True(h.AutoStart.Enabled);

        h.Ui.Clipboard = "Bearer " + BgGptKey + "\r\n"; // copied from the e-mail with the screen reader
        await AnswerAsync(h, "Готово.");
        Assert.Equal(BgGptKey, h.Secrets.Get(SecretNames.BgGptApiKey));
        Assert.Null(h.Ui.Clipboard); // cleared for safety
        Assert.Contains("BgGPT", h.Ai.ProviderChecks);
        Assert.Contains("Работи!", h.Spoken);

        await AnswerAsync(h, "Пропусни."); // no Groq key
        await h.IdleAsync();
        Assert.False(h.Orchestrator.InVoiceSetup);
        Assert.True(h.Settings.Current.General.FirstRunCompleted);
        Assert.Contains("Настройката приключи", h.Spoken);
        Assert.DoesNotContain(BgGptKey, h.Spoken); // the key is never read aloud
        Assert.DoesNotContain(h.Transcript, t => t.Text.Contains(BgGptKey));
        Assert.Equal(AssistantState.Idle, h.Orchestrator.State);
    }

    [Fact]
    public async Task Wrong_name_can_be_corrected_and_a_rejected_key_is_explained()
    {
        await using var h = FirstRun();
        await h.StartAsync();
        h.Ai.ProviderStatus = p => new AiStatus(AiAvailability.Unauthorized, p, true, "HTTP 401");
        await h.Orchestrator.StartVoiceSetupAsync();

        await AnswerAsync(h, "Стефко");
        await AnswerAsync(h, "Не.");
        Assert.Contains("Кажи ми само името си", h.Spoken);
        await AnswerAsync(h, "Аз съм Стевко");
        await AnswerAsync(h, "да");
        Assert.Equal("Стевко", h.Settings.Current.General.UserName);
        await AnswerAsync(h, "добре");      // speed
        await AnswerAsync(h, "не");         // no autostart
        Assert.False(h.AutoStart.Enabled);

        await AnswerAsync(h, "готово");     // nothing copied yet
        Assert.Contains("В клипборда няма текст", h.Spoken);
        h.Ui.Clipboard = "Здравейте, ето вашия ключ";
        await AnswerAsync(h, "готово");
        Assert.Contains("не прилича на ключ", h.Spoken);
        h.Ui.Clipboard = BgGptKey;
        await AnswerAsync(h, "готово");
        Assert.Contains("BgGPT отказа ключа", h.Spoken);
        Assert.Contains("опиташ с друг ключ", h.Spoken);
        Assert.True(h.Orchestrator.InVoiceSetup);
        await AnswerAsync(h, "край на настройката");
        Assert.False(h.Orchestrator.InVoiceSetup);
        Assert.True(h.Settings.Current.General.FirstRunCompleted);
    }

    [Fact]
    public async Task Silence_repeats_the_question_once_then_pauses_the_setup()
    {
        await using var h = FirstRun();
        await h.StartAsync();
        await h.Orchestrator.StartVoiceSetupAsync();

        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && !h.Orchestrator.IsTurnActive);
        h.Listener.RaiseTimeout();
        await Wait.UntilAsync(() => h.Spoken.Contains("Не те чух."), 5000);
        Assert.True(h.Orchestrator.InVoiceSetup);
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && !h.Orchestrator.IsTurnActive);
        h.Listener.RaiseTimeout();
        await Wait.UntilAsync(() => h.Spoken.Contains("Ще продължим по-късно"), 5000);
        await h.IdleAsync();
        Assert.False(h.Orchestrator.InVoiceSetup);
        Assert.False(h.Settings.Current.General.FirstRunCompleted); // it starts again next time
    }

    [Fact]
    public async Task Setup_and_keys_are_also_available_by_voice_later()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();

        h.Ui.Clipboard = "gsk_" + new string('a', 40); // a Groq key is recognised by its prefix
        await h.Orchestrator.SubmitTextAsync("Ведар, постави ключа");
        await h.IdleAsync();
        Assert.Equal("gsk_" + new string('a', 40), h.Secrets.Get(SecretNames.GroqApiKey));
        Assert.False(h.Secrets.Exists(SecretNames.BgGptApiKey));
        Assert.Contains("Записах ключа за Groq", h.Spoken);

        await h.Orchestrator.SubmitTextAsync("настройка");
        await Wait.UntilAsync(() => h.Spoken.Contains("Как се казваш?"), 5000);
        Assert.True(h.Orchestrator.InVoiceSetup);
        h.Orchestrator.Stop(); // the stop key ends the setup
        await h.IdleAsync();
        Assert.False(h.Orchestrator.InVoiceSetup);
    }

    [Fact]
    public async Task Without_a_microphone_the_spoken_setup_is_not_offered()
    {
        await using var h = FirstRun();
        h.Listener.FailOnOpen = true;
        await h.StartAsync();
        Assert.False(h.Orchestrator.CanRunVoiceSetup);
    }

    [Theory]
    [InlineData("Казвам се Стевко.", "Стевко")]
    [InlineData("Аз съм Мария Иванова", "Мария Иванова")]
    [InlineData("иван", "Иван")]
    [InlineData("Името ми е Петър.", "Петър")]
    [InlineData("Наричай ме Ники", "Ники")]
    [InlineData("да", null)]
    [InlineData("", null)]
    public void Names_are_taken_from_natural_answers(string heard, string? expected) =>
        Assert.Equal(expected, VoiceSetupDialog.ExtractName(heard));
}

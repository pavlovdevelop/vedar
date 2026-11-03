using System.Runtime.CompilerServices;
using Vedar.Core.Abstractions;
using Vedar.Core.Orchestration;
using Vedar.Core.Settings;
using Vedar.Core.State;
using Vedar.Tests.Support;

namespace Vedar.Tests.Core;

/// <summary>
/// The user starts to speak while VEDAR speaks or is still working on the answer: VEDAR gives way and listens to
/// the user until they are quiet — and never runs a command that nobody said.
/// </summary>
public class InterruptionTests
{
    private static string LastReply(OrchestratorHarness h)
    {
        lock (h.Transcript)
        {
            return h.Transcript.Last(t => t.Speaker == Speaker.Assistant).Text;
        }
    }

    [Fact]
    public async Task The_user_speaking_over_VEDAR_makes_it_stop_and_answer_what_the_user_said()
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        h.Output.Instant = false;
        await h.StartAsync();
        // The quick check hears the first words; the main model hears the whole request.
        h.Recognizer.Responder = (_, _) => "Разкажи ми виц за котки.";
        h.Ai.Reply("Котката влиза ", "в бар.");

        _ = h.Orchestrator.SubmitTextAsync("помощ"); // a long answer
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Speaking && h.Listener.InterruptOnSpeech, 3000);
        Assert.True(h.Listener.EmitUserSpeechStart(1.2f));
        await Wait.UntilAsync(() => h.Output.Ducked, 1000, "VEDAR lowers its voice at once");
        await Wait.UntilAsync(() => h.Output.CancelledCount >= 1, 3000, "VEDAR stops speaking");
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Listening, 3000);
        Assert.True(h.Listener.EmitUserSpeechEnd(2.5f)); // the user goes quiet

        await Wait.UntilAsync(() => h.Spoken.Contains("Котката влиза"), 5000);
        var asked = Assert.Single(h.Ai.Requests);
        Assert.Contains(asked, m => m.Content.Contains("виц за котки"));
        Assert.False(h.Output.Ducked);
    }

    [Fact]
    public async Task A_cough_over_VEDARs_voice_only_lowers_it_for_a_moment()
    {
        await using var h = new OrchestratorHarness();
        h.Output.Instant = false;
        h.Output.SpeedFactor = 4;
        await h.StartAsync();
        h.Recognizer.Responder = (_, _) => string.Empty; // the quick check hears no words

        var turn = h.Orchestrator.SubmitTextAsync("помощ");
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Speaking && h.Listener.InterruptOnSpeech, 3000);
        Assert.True(h.Listener.EmitUserSpeechStart(0.6f));
        await Wait.UntilAsync(() => h.Listener.Drops == 1, 3000, "the noise is forgotten");
        Assert.False(h.Output.Ducked);
        Assert.Equal([true, false], h.Output.DuckHistory);
        await turn;
        Assert.Equal(0, h.Output.CancelledCount); // VEDAR finished its answer
        Assert.Contains("Ведар, постави ключа", h.Spoken);
    }

    [Fact]
    public async Task VEDARs_own_words_over_its_voice_do_not_stop_it()
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        h.Output.Instant = false;
        await h.StartAsync();
        const string Answer = "Пловдив е един от най-старите градове в Европа, построен върху седем хълма край река Марица, и днес в него живеят около триста и петдесет хиляди души, които се гордеят с античния театър и Стария град";
        h.Ai.Reply(Answer);
        // What the microphone hears from the loudspeakers: VEDAR's own words.
        h.Recognizer.Responder = (_, _) => "един от най-старите градове в Европа построен върху седем хълма";

        var turn = h.Orchestrator.SubmitTextAsync("Разкажи ми за Пловдив.");
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Speaking && h.Listener.InterruptOnSpeech, 3000);
        Assert.True(h.Listener.EmitUserSpeechStart());
        await Wait.UntilAsync(() => h.Listener.Drops == 1, 3000);
        await turn;
        Assert.Equal(0, h.Output.CancelledCount);
    }

    [Fact]
    public async Task Stop_over_VEDARs_voice_stops_it_and_it_listens()
    {
        await using var h = new OrchestratorHarness();
        h.Output.Instant = false;
        await h.StartAsync();
        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.WakeWord ? "Ведар, спри." : string.Empty;

        _ = h.Orchestrator.SubmitTextAsync("помощ");
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Speaking && h.Listener.InterruptOnSpeech, 3000);
        Assert.True(h.Listener.EmitUserSpeechStart());
        await Wait.UntilAsync(() => h.Output.CancelledCount >= 1, 3000);
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Listening && h.Listener.Mode == ListeningMode.Command && !h.Listener.Suppressed, 3000);
        Assert.False(h.Output.Ducked);
    }

    [Fact]
    public async Task Going_on_before_VEDAR_answers_is_heard_as_one_sentence()
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        await h.StartAsync();
        h.Recognizer.Responder = (samples, r) => r.Purpose == RecognitionPurpose.WakeWord
            ? "за Васил Левски"
            : samples.Length > 16000 * 2 ? "Разкажи ми нещо интересно за Васил Левски." : "Разкажи ми нещо интересно.";
        h.Ai.Scripts.Enqueue(() => SlowAnswer()); // the first request: still thinking when the user goes on
        h.Ai.Reply("Васил Левски е ", "Апостола на свободата.");

        h.Orchestrator.PushToTalk();
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && !h.Listener.Suppressed);
        Assert.True(h.Listener.Emit(1.5f)); // "Разкажи ми нещо интересно." … pause …
        await Wait.UntilAsync(() => h.Ai.Requests.Count == 1 && h.Listener.InterruptOnSpeech, 3000);
        Assert.True(h.Listener.EmitUserSpeechStart(0.8f)); // … "за Васил Левски"
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Listening, 3000);
        Assert.True(h.Listener.EmitUserSpeechEnd(1.5f));

        await Wait.UntilAsync(() => h.Spoken.Contains("Апостола"), 5000);
        Assert.Equal(2, h.Ai.Requests.Count);
        Assert.Contains(h.Ai.Requests[1], m => m.Content == "Разкажи ми нещо интересно за Васил Левски.");
        Assert.DoesNotContain(h.Orchestrator.Conversation.Messages, m => m.Content == "Разкажи ми нещо интересно."); // the half question is gone
        Assert.Equal(ActivationSettings.DefaultEndOfSpeechPauseMs + 150, h.Settings.Current.Activation.EndOfSpeechPauseMs); // learned: wait a bit longer

        static async IAsyncEnumerable<string> SlowAnswer([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(10000, ct);
            yield return "Твърде късно.";
        }
    }

    [Fact]
    public async Task Giving_way_can_be_switched_off()
    {
        await using var h = new OrchestratorHarness(s => s.Activation.GiveWayWhenUserSpeaks = false);
        h.Output.Instant = false;
        await h.StartAsync();
        _ = h.Orchestrator.SubmitTextAsync("помощ");
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Speaking && h.Listener.BargeInEnabled, 3000);
        Assert.False(h.Listener.InterruptOnSpeech);
        Assert.False(h.Listener.EmitUserSpeechStart());
        h.Orchestrator.Stop();
        await h.IdleAsync();
    }

    [Fact]
    public async Task A_command_phrase_heard_in_a_noise_is_not_run()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("колко е часът");
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && !h.Listener.Suppressed && h.Orchestrator.State == AssistantState.Listening);
        var spoken = h.Synth.Spoken.Count;

        // Whisper, primed with the example commands, "hears" one of them in a noise; the fast model hears nothing.
        h.Recognizer.FastModelHearsTheSame = false;
        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.Command ? "Отвори браузъра." : string.Empty;
        Assert.True(h.Listener.Emit(0.6f));
        await Wait.UntilAsync(() => h.Recognizer.Requests.Count(r => r.Purpose == RecognitionPurpose.WakeWord) == 1, 3000);
        await h.IdleAsync();

        Assert.Empty(h.System.Actions);
        Assert.Equal(spoken, h.Synth.Spoken.Count);
    }

    [Fact]
    public async Task A_command_phrase_that_was_really_said_is_run()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        h.Recognizer.FastModelHearsTheSame = false;
        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.Command ? "Отвори браузъра." : "отвори браузъра";
        h.Orchestrator.PushToTalk();
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && !h.Listener.Suppressed);
        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => h.System.Actions.Contains("browser"), 3000);
    }

    [Fact]
    public async Task A_short_garbled_word_in_a_noise_is_not_sent_to_the_AI()
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        await h.StartAsync();
        h.Ai.Reply("Да, разбира се.");
        await h.Orchestrator.SubmitTextAsync("Може ли да ти задам въпрос?");
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && h.Orchestrator.State == AssistantState.Listening);

        h.Recognizer.FastModelHearsTheSame = false; // measured with the real models on a noise
        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.Command ? "Слъпи." : "Пърж!";
        Assert.True(h.Listener.Emit(0.6f));
        await Wait.UntilAsync(() => h.Recognizer.Requests.Count(r => r.Purpose == RecognitionPurpose.WakeWord) == 1, 3000);
        await h.IdleAsync();
        Assert.Single(h.Ai.Requests); // only the real question
    }

    [Fact]
    public async Task A_short_real_reply_goes_to_the_AI()
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        await h.StartAsync();
        h.Ai.Reply("Какво е времето при теб?");
        await h.Orchestrator.SubmitTextAsync("Разкажи ми нещо за времето днес.");
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && h.Orchestrator.State == AssistantState.Listening);

        h.Recognizer.FastModelHearsTheSame = false;
        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.Command ? "Вали." : "Воли!";
        h.Ai.Reply("Тогава вземи чадър.");
        Assert.True(h.Listener.Emit(0.8f));
        await Wait.UntilAsync(() => h.Spoken.Contains("чадър"), 3000);
    }

    [Fact]
    public void Old_settings_listen_two_minutes_after_an_answer()
    {
        var old = new VedarSettings { SchemaVersion = 3 };
        old.Activation.FollowUpSeconds = 30;
        old.Normalize();
        Assert.Equal(120, old.Activation.FollowUpSeconds);
        var mine = new VedarSettings { SchemaVersion = 3 };
        mine.Activation.FollowUpSeconds = 45;
        Assert.Equal(45, mine.Normalize().Activation.FollowUpSeconds);
        Assert.True(new VedarSettings().Activation.GiveWayWhenUserSpeaks);
    }

    [Fact]
    public async Task Listening_after_an_answer_lasts_two_minutes_by_default()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("колко е часът");
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && h.Orchestrator.State == AssistantState.Listening);
        Assert.Equal(120, h.Listener.ListeningWindowSeconds);
    }
}

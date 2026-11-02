using Vedar.Core.Abstractions;
using Vedar.Core.Localization;
using Vedar.Core.Orchestration;
using Vedar.Core.Settings;
using Vedar.Core.State;
using Vedar.Tests.Support;

namespace Vedar.Tests.Core;

/// <summary>
/// Talking to VEDAR like to a person: it answers greetings, waits quietly for the reply after it has spoken (no
/// wake word, no beep), stays quiet about noise and its own echo, and answers a complete command a little earlier.
/// </summary>
public class NaturalConversationTests
{
    private static readonly Localizer Bg = Localizer.Load("bg");

    private static IEnumerable<string> Variants(string key, params object[] args) =>
        Bg.Variants(key).Select(v => args.Length == 0 ? v : string.Format(v, args));

    private static string LastReply(OrchestratorHarness h)
    {
        lock (h.Transcript)
        {
            return h.Transcript.Last(t => t.Speaker == Speaker.Assistant).Text;
        }
    }

    private static int Replies(OrchestratorHarness h)
    {
        lock (h.Transcript)
        {
            return h.Transcript.Count(t => t.Speaker == Speaker.Assistant);
        }
    }

    private static Task ListeningQuietlyAsync(OrchestratorHarness h) =>
        Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && !h.Listener.Suppressed && !h.Orchestrator.IsTurnActive
                              && h.Orchestrator.State == AssistantState.Listening, 5000, "listening without the wake word");

    [Fact]
    public async Task Answering_the_greeting_without_the_wake_word_gets_a_warm_reply_without_a_second_hello()
    {
        await using var h = new OrchestratorHarness(s => { s.General.GreetOnStartup = true; s.General.UserName = "Павел"; });
        await h.StartAsync();
        Assert.StartsWith("Добър ден, Павел!", LastReply(h));
        await ListeningQuietlyAsync(h);

        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.Command ? "Здравей, Ведар!" : string.Empty;
        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => Replies(h) == 2);
        var reply = LastReply(h);
        Assert.Contains(reply, Variants("smalltalk.greetedBack", ", Павел"));
        Assert.DoesNotContain("Добър ден", reply); // VEDAR has just said hello

        await ListeningQuietlyAsync(h);
        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.Command ? "Добре съм, благодаря." : string.Empty;
        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => Replies(h) == 3);
        Assert.Contains(LastReply(h), Variants("smalltalk.feeling.good"));
        Assert.Empty(h.Ai.Requests); // answered at once, locally
    }

    [Theory]
    [InlineData("Здравей, Ведар!", "Здравей!")]
    [InlineData("Добро утро, Ведар.", "Добро утро!")]
    public async Task A_greeting_with_the_name_is_answered_in_hands_free_mode(string heard, string expectedStart)
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        Assert.Equal(ListeningMode.WakeWord, h.Listener.Mode);
        h.Recognizer.Responder = (_, _) => heard;

        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => Replies(h) == 1);
        Assert.StartsWith(expectedStart, LastReply(h));
        await ListeningQuietlyAsync(h); // and waits for the answer to its "Как си?" or "С какво да помогна?"
    }

    [Fact]
    public async Task A_greeting_bent_by_the_command_vocabulary_is_taken_from_the_fast_model()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        // Measured with the real models: the main model, primed with the commands, heard "утро" as "отвори".
        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.WakeWord ? "Добро утро, Ведар!" : "Добро отвори. Ведар.";

        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => Replies(h) == 1);
        Assert.StartsWith("Добро утро!", LastReply(h));
        Assert.Empty(h.Ai.Requests);
    }

    [Fact]
    public async Task The_name_at_the_end_of_a_short_question_wakes_VEDAR()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        h.Recognizer.Responder = (_, _) => "Как си, Ведар?";

        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => Replies(h) == 1);
        Assert.Contains(LastReply(h), Variants("speech.howAreYou"));
    }

    [Fact]
    public async Task Small_talk_is_kept_as_context_so_the_AI_understands_the_answer()
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("Как си?");
        var question = LastReply(h);

        h.Ai.Reply("Съжалявам да го чуя. ", "Какво се случи?");
        await h.Orchestrator.SubmitTextAsync("Зле съм.");
        var request = Assert.Single(h.Ai.Requests);
        Assert.Contains(request, m => m.Role == ChatRole.User && m.Content == "Как си?");
        Assert.Contains(request, m => m.Role == ChatRole.Assistant && m.Content == question);
        Assert.Equal("Зле съм.", request[^1].Content);
    }

    [Fact]
    public async Task Okay_after_an_answer_needs_no_reply_and_VEDAR_goes_quiet()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("колко е часът");
        await ListeningQuietlyAsync(h);
        var replies = Replies(h);

        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.Command ? "Добре." : string.Empty;
        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.WakeWord && !h.Orchestrator.IsTurnActive);
        Assert.Equal(replies, Replies(h));
    }

    [Fact]
    public async Task Goodbye_gets_a_warm_reply_and_ends_listening_without_the_wake_word()
    {
        await using var h = new OrchestratorHarness(s => s.General.UserName = "Павел");
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("Довиждане.");
        await h.IdleAsync();

        Assert.Contains(LastReply(h), Variants("smalltalk.farewell.day", ", Павел")); // 14:35
        Assert.Equal(ListeningMode.WakeWord, h.Listener.Mode);
        Assert.Equal(AssistantState.Idle, h.Orchestrator.State);
    }

    [Fact]
    public async Task Late_in_the_evening_goodbye_is_good_night()
    {
        await using var h = new OrchestratorHarness();
        h.Clock.Now = new DateTimeOffset(2026, 11, 1, 22, 10, 0, TimeSpan.FromHours(2));
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("Чао.");
        Assert.StartsWith("Лека нощ!", LastReply(h));
    }

    [Fact]
    public async Task A_noise_while_listening_quietly_keeps_the_window_open_and_VEDAR_silent()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("колко е часът");
        await ListeningQuietlyAsync(h);
        var spoken = h.Synth.Spoken.Count;

        h.Recognizer.Responder = (_, _) => string.Empty; // a cough
        Assert.True(h.Listener.Emit(0.6f));
        await Wait.UntilAsync(() => h.Recognizer.Requests.Count > 0);
        await ListeningQuietlyAsync(h);
        await Task.Delay(450);

        Assert.Equal(spoken, h.Synth.Spoken.Count);
        Assert.Equal(ListeningMode.Command, h.Listener.Mode);
        var window = h.Listener.ListeningWindowSeconds;
        Assert.InRange(window, 2, ActivationSettings.DefaultFollowUpSeconds); // the rest of the same window
    }

    [Fact]
    public async Task VEDARs_own_words_coming_back_are_not_taken_as_a_question()
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        await h.StartAsync();
        const string Answer = "Софийският университет е основан през хиляда осемстотин осемдесет и осма година.";
        h.Ai.Reply(Answer);
        await h.Orchestrator.SubmitTextAsync("Кога е основан Софийският университет?");
        await ListeningQuietlyAsync(h);

        // The screen reader reads the answer aloud and the microphone hears it.
        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.Command ? Answer : string.Empty;
        Assert.True(h.Listener.Emit(3));
        await Wait.UntilAsync(() => h.Recognizer.Requests.Count > 0);
        await ListeningQuietlyAsync(h);

        Assert.Single(h.Ai.Requests);
    }

    [Fact]
    public async Task After_typing_only_the_wake_word_counts_so_the_screen_reader_is_not_answered()
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("колко е часът");
        await ListeningQuietlyAsync(h);
        await Task.Delay(400);
        var replies = Replies(h);

        h.Activity.TypeNow(); // the user works with the screen reader
        h.Recognizer.Responder = (_, _) => "Входяща поща, пет непрочетени писма.";
        Assert.True(h.Listener.Emit(2));
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.WakeWord && !h.Orchestrator.IsTurnActive);
        Assert.Equal(replies, Replies(h));
        Assert.Empty(h.Ai.Requests);

        h.Recognizer.Responder = (_, _) => "Ведар, коя дата сме?";
        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => h.Spoken.Contains("първи ноември"));
    }

    [Fact]
    public async Task Without_AI_not_understood_is_said_once_then_VEDAR_stays_quiet()
    {
        await using var h = new OrchestratorHarness();
        h.Ai.Configured = false;
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("Разкажи ми приказка.");
        Assert.Contains("не са настроени", h.Spoken);
        await ListeningQuietlyAsync(h);

        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.Command ? "Утре ще вали сняг в планината." : string.Empty;
        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => Variants("speech.notUnderstood").Contains(LastReply(h)));
        await ListeningQuietlyAsync(h);
        var replies = Replies(h);

        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.Command ? "А после отиваме на пазар с децата." : string.Empty;
        Assert.True(h.Listener.Emit(2));
        await Wait.UntilAsync(() => h.Recognizer.Requests.Count(r => r.Purpose == RecognitionPurpose.Command) >= 2);
        await ListeningQuietlyAsync(h);
        Assert.Equal(replies, Replies(h)); // probably someone else talking: no second "не те разбрах"
    }

    [Fact]
    public async Task Listening_without_the_wake_word_can_be_switched_off()
    {
        await using var h = new OrchestratorHarness(s => s.Activation.FollowUpSeconds = 0);
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("колко е часът");
        await h.IdleAsync();
        Assert.Equal(ListeningMode.WakeWord, h.Listener.Mode);
        Assert.Equal(AssistantState.Idle, h.Orchestrator.State);
    }

    [Fact]
    public async Task A_slow_AI_answer_is_bridged_with_a_short_spoken_moment()
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        await h.StartAsync();
        h.Ai.Scripts.Enqueue(SlowStart);

        await h.Orchestrator.SubmitTextAsync("Колко звезди има във Вселената?");
        var spoken = h.Synth.Spoken.ToList();
        var filler = spoken.FindIndex(s => Variants("speech.ai.thinking").Contains(s));
        Assert.True(filler >= 0, string.Join(" | ", spoken));
        Assert.True(filler < spoken.FindIndex(s => s.StartsWith("Никой не знае", StringComparison.Ordinal)));
        Assert.DoesNotContain(h.Transcript, t => Variants("speech.ai.thinking").Contains(t.Text)); // not part of the answer

        static async IAsyncEnumerable<string> SlowStart()
        {
            await Task.Delay(3200);
            yield return "Никой не знае точно. ";
            yield return "Те са безброй.";
        }
    }

    [Fact]
    public async Task A_complete_short_command_is_answered_after_a_shorter_pause_with_the_early_recognition()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.Command ? "Колко е часът?" : string.Empty;
        h.Orchestrator.PushToTalk();
        await ListeningQuietlyAsync(h);

        Assert.True(h.Listener.EmitSpeechStart());
        Assert.True(h.Listener.EmitPause(1.0f)); // 450 ms of silence after "Колко е часът?"
        await Wait.UntilAsync(() => h.Listener.CommitRequests.Count > 0, 2000);
        Assert.Equal(800, h.Listener.CommitRequests[0]); // instead of 1400 ms
        Assert.True(h.Listener.EmitEnd(1.35f));
        await Wait.UntilAsync(() => h.Spoken.Contains("четиринадесет"));

        Assert.Single(h.Recognizer.Requests, r => r.Purpose == RecognitionPurpose.Command); // recognized once, during the pause
    }

    [Fact]
    public async Task On_a_slow_computer_VEDAR_does_not_recognize_during_pauses()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        h.Recognizer.ReportedTime = TimeSpan.FromSeconds(4); // measured: a cancelled recognition would hold the CPU
        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.Command ? "Колко е часът?" : string.Empty;
        h.Orchestrator.PushToTalk();
        await ListeningQuietlyAsync(h);
        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => h.Spoken.Contains("четиринадесет"));
        await ListeningQuietlyAsync(h);
        var requests = h.Recognizer.Requests.Count;

        Assert.True(h.Listener.EmitSpeechStart());
        Assert.True(h.Listener.EmitPause(1.0f));
        await Task.Delay(300);
        Assert.Equal(requests, h.Recognizer.Requests.Count); // no recognition during the pause
        Assert.Empty(h.Listener.CommitRequests);             // and the full pause is waited for
        Assert.True(h.Listener.EmitEnd(1.5f));
        await Wait.UntilAsync(() => h.Recognizer.Requests.Count == requests + 1);
    }

    [Theory]
    [InlineData("Искам да те питам.")]
    [InlineData("Разкажи ми за историята на България.")]
    [InlineData("Здравей.")]
    [InlineData("Добре.")]
    [InlineData("Разкажи ми за")]
    public async Task No_early_answer_for_openers_free_questions_greetings_or_unfinished_sentences(string soFar)
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.Command ? soFar : string.Empty;
        h.Orchestrator.PushToTalk();
        await ListeningQuietlyAsync(h);

        Assert.True(h.Listener.EmitSpeechStart());
        Assert.True(h.Listener.EmitPause(1.0f));
        await Wait.UntilAsync(() => h.Recognizer.Requests.Count > 0);
        await Task.Delay(300);
        Assert.Empty(h.Listener.CommitRequests); // the full pause is waited for
    }

    [Fact]
    public async Task Going_on_after_a_pause_discards_the_early_recognition()
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        await h.StartAsync();
        h.Recognizer.Responder = (samples, r) => samples.Length > 16000 * 2 ? "Разкажи ми за Васил Левски." : "Разкажи ми за";
        h.Ai.Reply("Васил Левски е ", "Апостола на свободата.");
        h.Orchestrator.PushToTalk();
        await ListeningQuietlyAsync(h);

        Assert.True(h.Listener.EmitSpeechStart());
        Assert.True(h.Listener.EmitPause(1.0f));
        await Wait.UntilAsync(() => h.Recognizer.Requests.Count == 1);
        h.Listener.EmitResume(); // "… Васил Левски."
        Assert.True(h.Listener.EmitEnd(2.6f));
        await h.IdleAsync();

        var asked = Assert.Single(h.Ai.Requests);
        Assert.Contains(asked, m => m.Content.Contains("Васил Левски"));
        Assert.Equal(2, h.Recognizer.Requests.Count(r => r.Purpose == RecognitionPurpose.Command));
    }

    [Fact]
    public async Task Push_to_talk_while_listening_quietly_switches_to_listening_for_a_request()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("колко е часът");
        await ListeningQuietlyAsync(h);
        Assert.Equal(ActivationSettings.DefaultFollowUpSeconds, h.Listener.ListeningWindowSeconds);

        h.Orchestrator.PushToTalk();
        await Wait.UntilAsync(() => h.Listener.ListeningWindowSeconds == 0 && h.Orchestrator.State == AssistantState.Listening);
        h.Recognizer.Responder = (_, _) => string.Empty;
        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => Variants("speech.notUnderstood").Contains(LastReply(h))); // a request: an empty one is reported
    }

    [Fact]
    public void Old_settings_lose_the_beeps_and_get_the_longer_listening()
    {
        var old = new VedarSettings { SchemaVersion = 2 };
        old.Voice.Earcons = true;
        old.Activation.FollowUpSeconds = 15;
        old.Normalize();
        Assert.False(old.Voice.Earcons);
        Assert.Equal(ActivationSettings.DefaultFollowUpSeconds, old.Activation.FollowUpSeconds);

        var mine = new VedarSettings { SchemaVersion = 2 };
        mine.Activation.FollowUpSeconds = 45; // chosen by the user: kept
        mine.Normalize();
        Assert.Equal(45, mine.Activation.FollowUpSeconds);
        Assert.False(new VedarSettings().Voice.Earcons);
    }
}

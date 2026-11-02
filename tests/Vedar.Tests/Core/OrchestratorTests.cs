using System.Diagnostics;
using Vedar.Audio.Testing;
using Vedar.Commands;
using Vedar.Core.Abstractions;
using Vedar.Core.Localization;
using Vedar.Core.Orchestration;
using Vedar.Core.Settings;
using Vedar.Core.State;
using Vedar.Speech.WakeWord;
using Vedar.Tests.Support;

namespace Vedar.Tests.Core;

/// <summary>
/// End-to-end behaviour of the assistant with simulated audio. The command dispatcher and wake word matcher are
/// the real implementations; microphone, recognizer, synthesizer, audio output and AI are controllable fakes.
/// </summary>
public sealed class OrchestratorHarness : IAsyncDisposable
{
    public TestSettingsStore Settings { get; }
    public FakeVoiceListener Listener { get; } = new();
    public FakeSpeechRecognizer Recognizer { get; } = new();
    public FakeSpeechSynthesizer Synth { get; } = new();
    public RecordingAudioOutput Output { get; } = new();
    public FakeConversationAi Ai { get; } = new();
    public FakeUiHost Ui { get; } = new();
    public FakeSystemActions System { get; } = new();
    public FakeHistoryStore History { get; } = new();
    public Vedar.Infrastructure.Security.InMemorySecretStore Secrets { get; } = new();
    public FakeAutoStart AutoStart { get; } = new();
    public FakeUserActivity Activity { get; } = new();
    public FixedClock Clock { get; }
    public AssistantOrchestrator Orchestrator { get; }
    public List<TranscriptEntry> Transcript { get; } = [];

    public OrchestratorHarness(Action<VedarSettings>? configure = null)
    {
        var settings = new VedarSettings();
        settings.General.GreetOnStartup = false;
        settings.General.FirstRunCompleted = true; // an already set-up VEDAR (the first run is tested in VoiceSetupTests)
        settings.Voice.Earcons = false;
        configure?.Invoke(settings);
        Settings = new TestSettingsStore(settings);
        var loc = Localizer.Load("bg", new Random(7));
        var clock = Clock = new FixedClock(new DateTimeOffset(2026, 11, 1, 14, 35, 0, TimeSpan.FromHours(2)));
        var wake = new WakeWordMatcher();
        var smallTalk = new Vedar.Core.Conversation.SmallTalk(loc, clock);
        var dispatcher = new CommandDispatcher(Settings, System, Ui, Ai, History, loc, Log.Null<CommandDispatcher>(), clock, wake.IsWakeToken, smallTalk);
        Orchestrator = new AssistantOrchestrator(Log.Null<AssistantOrchestrator>(), Settings, Listener, Recognizer, wake, Synth, Output, dispatcher, Ai, loc, clock, History,
            ui: Ui, secrets: Secrets, autoStart: AutoStart, activity: Activity, smallTalk: smallTalk);
        Orchestrator.TranscriptAdded += (_, e) => { lock (Transcript) { Transcript.Add(e); } };
    }

    public async Task StartAsync()
    {
        await Orchestrator.StartAsync();
        await IdleAsync();
    }

    public async Task IdleAsync(int timeoutMs = 5000)
    {
        await Wait.UntilAsync(() => !Orchestrator.IsTurnActive && Orchestrator.State is AssistantState.Idle or AssistantState.Listening, timeoutMs, "assistant idle");
        await Task.Delay(450); // post-speech tail restores listening
    }

    public string Spoken => Synth.AllSpoken;

    public ValueTask DisposeAsync() => Orchestrator.DisposeAsync();
}

public class OrchestratorTests
{
    [Fact]
    public async Task Greets_user_by_name_on_startup_then_waits_for_an_answer_quietly_and_returns_to_hands_free()
    {
        await using var h = new OrchestratorHarness(s => { s.General.GreetOnStartup = true; s.General.UserName = "Стевко"; });
        await h.StartAsync();
        Assert.StartsWith("Добър ден, Стевко!", h.Spoken); // 14:35
        // Like a person who has just said hello: it listens for the answer, without "Ведар" and without a beep.
        Assert.Equal(AssistantState.Listening, h.Orchestrator.State);
        Assert.Equal(ListeningMode.Command, h.Listener.Mode);
        Assert.False(h.Listener.Suppressed);
        Assert.Equal(ActivationSettings.DefaultFollowUpSeconds, h.Listener.ListeningWindowSeconds);
        Assert.Equal(MicrophoneStatus.Active, h.Orchestrator.MicrophoneStatus);

        var spoken = h.Synth.Spoken.Count;
        h.Listener.RaiseTimeout(); // nobody answered
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.WakeWord && h.Orchestrator.State == AssistantState.Idle);
        Assert.Equal(spoken, h.Synth.Spoken.Count);
        Assert.DoesNotContain(h.Output.Played.ToList(), p => p.SampleRate == Earcons.SampleRate); // no beeps
    }

    [Fact]
    public async Task Push_to_talk_listens_recognizes_executes_and_answers_with_voice()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        h.Recognizer.Enqueue("Колко е часът?");

        h.Orchestrator.PushToTalk();
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && h.Orchestrator.State == AssistantState.Listening);
        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => h.Spoken.Contains("четиринадесет и тридесет и пет минути"));
        await h.IdleAsync();

        // After the answer VEDAR keeps listening for a follow-up without the wake word.
        Assert.Equal(AssistantState.Listening, h.Orchestrator.State);
        Assert.Equal(ListeningMode.Command, h.Listener.Mode);
        Assert.Contains(h.Transcript, t => t.Speaker == Speaker.User && t.Text == "Колко е часът?");
    }

    [Fact]
    public async Task Wake_word_with_command_is_executed_in_hands_free_mode()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        h.Recognizer.Responder = (_, _) => "Ведар, коя дата сме?";

        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => h.Spoken.Contains("първи ноември"));
        Assert.Contains(h.Recognizer.Requests, r => r.Purpose == RecognitionPurpose.WakeWord);
        Assert.Contains(h.Recognizer.Requests, r => r.Purpose == RecognitionPurpose.Command);
    }

    [Fact]
    public async Task Speech_without_wake_word_is_ignored_silently()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        h.Recognizer.Responder = (_, _) => "Днес времето е хубаво.";

        Assert.True(h.Listener.Emit());
        await h.IdleAsync();
        Assert.Empty(h.Synth.Spoken);
        Assert.Empty(h.Transcript);
        Assert.Equal(AssistantState.Idle, h.Orchestrator.State);
    }

    [Fact]
    public async Task Wake_word_alone_answers_listening_then_takes_the_command()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        h.Recognizer.Enqueue("Ведар.");
        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && h.Orchestrator.State == AssistantState.Listening);
        Assert.Contains(Localizer.Load("bg").Variants("speech.listening"), v => h.Spoken.Contains(v.TrimEnd('.')));

        h.Recognizer.Enqueue("отвори браузъра");
        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => h.System.Actions.Contains("browser"));
    }

    [Fact]
    public async Task Microphone_is_suppressed_while_speaking_so_VEDAR_cannot_hear_itself()
    {
        await using var h = new OrchestratorHarness();
        h.Output.Instant = false;
        h.Output.SpeedFactor = 3;
        await h.StartAsync();

        var task = h.Orchestrator.SubmitTextAsync("помощ");
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Speaking);
        Assert.True(h.Listener.Suppressed);
        Assert.False(h.Listener.Emit(), "utterances must be discarded while speaking");
        await task;
        await h.IdleAsync();
        Assert.False(h.Listener.Suppressed);
    }

    [Fact]
    public async Task Stop_interrupts_speech_immediately_and_returns_to_idle()
    {
        await using var h = new OrchestratorHarness();
        h.Output.Instant = false;
        await h.StartAsync();

        _ = h.Orchestrator.SubmitTextAsync("помощ");
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Speaking);
        var sw = Stopwatch.StartNew();
        h.Orchestrator.Stop();
        await Wait.UntilAsync(() => !h.Orchestrator.IsTurnActive, 2000);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"stop took {sw.ElapsedMilliseconds} ms");
        Assert.True(h.Output.CancelledCount >= 1);
        Assert.True(h.Output.TotalSecondsPlayed < 20);
        await h.IdleAsync();
        Assert.Equal(AssistantState.Idle, h.Orchestrator.State);
    }

    [Fact]
    public async Task Saying_Vedar_stop_while_it_speaks_stops_immediately()
    {
        await using var h = new OrchestratorHarness();
        h.Output.Instant = false;
        await h.StartAsync();
        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.WakeWord ? "Ведар, спри!" : string.Empty;

        _ = h.Orchestrator.SubmitTextAsync("помощ");
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Speaking && h.Listener.BargeInEnabled);
        var sw = Stopwatch.StartNew();
        Assert.True(h.Listener.EmitBargeIn());
        await Wait.UntilAsync(() => !h.Orchestrator.IsTurnActive, 2000);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"voice stop took {sw.ElapsedMilliseconds} ms");
        Assert.True(h.Output.CancelledCount >= 1);
        Assert.Contains(h.Recognizer.Requests, r => r.Purpose == RecognitionPurpose.WakeWord); // the fast model
        // After "Ведар, спри" VEDAR listens for the next request without the wake word.
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Listening && h.Listener.Mode == ListeningMode.Command && !h.Listener.Suppressed, 3000);
        Assert.False(h.Listener.BargeInEnabled);

        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.Command ? "Искам да ми разкажеш нещо за световната история." : string.Empty;
        h.Ai.Reply("Световната история започва ", "с първите цивилизации.");
        Assert.True(h.Listener.Emit());
        await h.IdleAsync();
        Assert.Contains(h.Ai.Requests, r => r.Any(m => m.Content.Contains("световната история")));
        Assert.Contains("първите цивилизации", h.Spoken);
    }

    [Fact]
    public async Task Pause_in_the_middle_of_a_sentence_does_not_cut_it_the_whole_sentence_is_understood()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        h.Recognizer.Delay = TimeSpan.FromMilliseconds(300); // recognizing the first part takes a moment
        h.Recognizer.Responder = (samples, r) => samples.Length > 16000 * 2
            ? "Искам да говорим за историята на България."
            : "Искам да говорим.";
        h.Ai.Reply("Историята на България ", "започва през шестстотин осемдесет и първа година.");

        h.Orchestrator.PushToTalk();
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && !h.Listener.Suppressed);
        Assert.True(h.Listener.Emit(1.0f));          // "Искам да говорим" … pause …
        await Wait.UntilAsync(() => h.Listener.BargeInContinuation && h.Listener.BargeInEnabled, 2000);
        Assert.True(h.Listener.EmitContinuation(1.5f)); // … "за историята на България"
        await h.IdleAsync();

        Assert.DoesNotContain("Какво искаш да обсъдим", h.Spoken); // it did not answer half a sentence
        var asked = Assert.Single(h.Ai.Requests);
        Assert.Contains(asked, m => m.Content.Contains("историята на България"));
        Assert.Contains("шестстотин осемдесет и първа", h.Spoken);
        Assert.False(h.Listener.BargeInContinuation);
    }

    [Fact]
    public async Task Sentence_ending_like_unfinished_waits_for_the_rest()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        h.Recognizer.Responder = (samples, r) => samples.Length > 16000 * 2 ? "Разкажи ми за Васил Левски." : "Разкажи ми за";
        h.Ai.Reply("Васил Левски е ", "Апостола на свободата.");

        h.Orchestrator.PushToTalk();
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && !h.Listener.Suppressed);
        Assert.True(h.Listener.Emit(1.0f));
        await Wait.UntilAsync(() => h.Listener.BargeInContinuation, 2000);
        await Task.Delay(1000); // the user thinks for a second, then goes on
        Assert.True(h.Listener.EmitContinuation(1.5f));
        await h.IdleAsync();

        var asked = Assert.Single(h.Ai.Requests);
        Assert.Contains(asked, m => m.Content.Contains("Васил Левски"));
    }

    [Fact]
    public async Task Complete_sentence_without_more_speech_is_answered_without_waiting()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.Command ? "Колко е часът?" : string.Empty;
        h.Orchestrator.PushToTalk();
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && !h.Listener.Suppressed);
        var sw = Stopwatch.StartNew();
        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => h.Spoken.Contains("четиринадесет"), 3000);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"answered after {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Hearing_its_own_voice_while_speaking_does_not_stop_it()
    {
        await using var h = new OrchestratorHarness();
        h.Output.Instant = false;
        await h.StartAsync();
        string? firstSentence = null;
        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.WakeWord ? firstSentence ?? string.Empty : string.Empty;

        _ = h.Orchestrator.SubmitTextAsync("помощ");
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Speaking && h.Listener.BargeInEnabled);
        firstSentence = h.Synth.Spoken.First(); // the echo of what VEDAR is saying
        Assert.True(h.Listener.EmitBargeIn(3));
        await Wait.UntilAsync(() => h.Recognizer.Requests.Any(r => r.Purpose == RecognitionPurpose.WakeWord), 2000);
        await Task.Delay(300);

        Assert.True(h.Orchestrator.IsTurnActive, "still speaking");
        Assert.Equal(0, h.Output.CancelledCount);
        h.Orchestrator.Stop();
        await h.IdleAsync();
    }

    [Fact]
    public async Task Voice_stop_also_cancels_an_AI_answer_that_is_still_coming()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        h.Recognizer.Responder = (_, r) => r.Purpose == RecognitionPurpose.WakeWord ? "Стоп." : string.Empty;
        h.Ai.ReplySlowly(TimeSpan.FromSeconds(5), "Това ", "е ", "дълъг ", "отговор.");

        _ = h.Orchestrator.SubmitTextAsync("Разкажи ми дълга история.");
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Processing && h.Listener.BargeInEnabled, 3000);
        Assert.True(h.Listener.EmitBargeIn());
        await Wait.UntilAsync(() => !h.Orchestrator.IsTurnActive, 2000);
        await h.IdleAsync();
        Assert.DoesNotContain("дълъг", h.Spoken);
    }

    [Fact]
    public async Task Voice_stop_can_be_switched_off()
    {
        await using var h = new OrchestratorHarness(s => s.Activation.VoiceStopWhileSpeaking = false);
        h.Output.Instant = false;
        await h.StartAsync();
        _ = h.Orchestrator.SubmitTextAsync("помощ");
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Speaking);
        Assert.False(h.Listener.BargeInEnabled);
        Assert.False(h.Listener.EmitBargeIn());
        h.Orchestrator.Stop();
        await h.IdleAsync();
    }

    [Fact]
    public async Task Push_to_talk_while_speaking_interrupts_and_starts_listening()
    {
        await using var h = new OrchestratorHarness();
        h.Output.Instant = false;
        await h.StartAsync();
        _ = h.Orchestrator.SubmitTextAsync("помощ");
        await Wait.UntilAsync(() => h.Orchestrator.State == AssistantState.Speaking);

        h.Orchestrator.PushToTalk();
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && h.Orchestrator.State == AssistantState.Listening);
        Assert.False(h.Listener.Suppressed);
    }

    [Fact]
    public async Task Switch_to_the_backup_AI_is_announced_before_its_answer()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        h.Ai.ReplyAfterFallback("BgGPT", "Groq", "Софийският университет е основан ", "през хиляда осемстотин осемдесет и осма година.");

        await h.Orchestrator.SubmitTextAsync("Кога е основан Софийският университет?");
        await h.IdleAsync();

        var spoken = h.Spoken;
        var notice = spoken.IndexOf("BgGPT не отговаря, затова ще отговори резервният модел Groq.", StringComparison.Ordinal);
        Assert.True(notice >= 0, spoken);
        Assert.Contains("Въпросът се изпраща към Groq през интернет.", spoken);
        Assert.True(notice < spoken.IndexOf("Софийският университет е основан", StringComparison.Ordinal), "the notice comes first");
    }

    [Fact]
    public async Task Unknown_request_is_answered_by_AI_with_streaming_speech_and_cloud_notice()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        h.Ai.Reply("България е ", "красива страна. ", "Има ", "много планини.");

        await h.Orchestrator.SubmitTextAsync("Разкажи ми нещо за България.");
        await h.IdleAsync();

        Assert.Contains("ще се изпраща като текст към BgGPT", h.Spoken);
        Assert.Contains("България е красива страна.", h.Spoken);
        Assert.Contains("Има много планини.", h.Spoken);
        Assert.True(h.Orchestrator.InConversation);
        var messages = h.Orchestrator.Conversation.Messages;
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.Assistant, messages[1].Role);
        Assert.Equal(ChatRole.System, h.Ai.Requests[0][0].Role);

        // The cloud notice is given once per session.
        h.Ai.Reply("Да.");
        h.Synth.Spoken.Clear();
        await h.Orchestrator.SubmitTextAsync("А кой е най-високият връх?");
        await h.IdleAsync();
        Assert.DoesNotContain("през интернет", h.Spoken);
        Assert.Equal(4, h.Orchestrator.Conversation.Count);
    }

    [Fact]
    public async Task Offline_AI_gives_clear_message_and_next_request_recovers()
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        await h.StartAsync();
        h.Ai.Fail(AiErrorKind.NoConnection);

        await h.Orchestrator.SubmitTextAsync("Разкажи ми виц.");
        await h.IdleAsync();
        Assert.Contains("Няма връзка с интернет", h.Spoken);
        Assert.Equal(0, h.Orchestrator.Conversation.Count);
        Assert.Equal(AssistantState.Listening, h.Orchestrator.State); // waits quietly in case the user answers

        h.Ai.Reply("Ето един виц.");
        await h.Orchestrator.SubmitTextAsync("Разкажи ми виц.");
        await h.IdleAsync();
        Assert.Contains("Ето един виц.", h.Spoken);
        Assert.Equal(2, h.Orchestrator.Conversation.Count);
    }

    [Theory]
    [InlineData(AiErrorKind.Unauthorized, "отказа достъп")]
    [InlineData(AiErrorKind.RateLimited, "претоварен")]
    [InlineData(AiErrorKind.Timeout, "не отговори навреме")]
    [InlineData(AiErrorKind.ServerError, "проблем при BgGPT")]
    [InlineData(AiErrorKind.MissingApiKey, "Няма въведен ключ")]
    public async Task AI_errors_are_explained_in_bulgarian(AiErrorKind kind, string expected)
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        await h.StartAsync();
        h.Ai.Fail(kind);
        await h.Orchestrator.SubmitTextAsync("Какво е квантова физика?");
        await h.IdleAsync();
        Assert.Contains(expected, h.Spoken);
    }

    [Fact]
    public async Task Never_runs_two_AI_requests_for_the_same_conversation_at_once()
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        await h.StartAsync();
        h.Ai.ReplySlowly(TimeSpan.FromMilliseconds(80), "Първи ", "отговор ", "е ", "дълъг.");
        h.Ai.ReplySlowly(TimeSpan.FromMilliseconds(80), "Втори ", "отговор.");

        await Task.WhenAll(h.Orchestrator.SubmitTextAsync("Първи въпрос за историята?"), h.Orchestrator.SubmitTextAsync("Втори въпрос за природата?"));
        await h.IdleAsync();
        Assert.Equal(1, h.Ai.MaxConcurrent);
    }

    [Fact]
    public async Task Voice_utterance_arriving_during_a_turn_is_not_processed_twice()
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        await h.StartAsync();
        h.Ai.ReplySlowly(TimeSpan.FromMilliseconds(100), "Мисля ", "по ", "въпроса.");
        h.Recognizer.Responder = (_, _) => "Ведар, разкажи ми за Пирин.";
        Assert.True(h.Listener.Emit());
        await Wait.UntilAsync(() => h.Orchestrator.IsTurnActive);
        h.Listener.Suppressed = false; // even if audio got through, the second request must be dropped
        h.Listener.Emit();
        await h.IdleAsync();
        Assert.Single(h.Ai.Requests);
    }

    [Fact]
    public async Task Sensitive_action_requires_spoken_confirmation_yes()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("Изключи се.");
        Assert.NotNull(h.Orchestrator.PendingConfirmationQuestion);
        Assert.Contains("Сигурен ли си", h.Spoken);
        Assert.Equal(0, h.Ui.ExitRequests);

        await h.Orchestrator.SubmitTextAsync("Да.");
        Assert.Equal(1, h.Ui.ExitRequests);
        Assert.Contains("Довиждане", h.Spoken);
        Assert.Null(h.Orchestrator.PendingConfirmationQuestion);
    }

    [Fact]
    public async Task Sensitive_action_is_cancelled_on_no()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("Изтрий историята.");
        await h.Orchestrator.SubmitTextAsync("Не.");
        Assert.Equal(0, h.History.DeleteCount);
        Assert.True(h.Spoken.Contains("отказах") || h.Spoken.Contains("няма да го правя"), h.Spoken);
        Assert.Null(h.Orchestrator.PendingConfirmationQuestion);
    }

    [Fact]
    public async Task Asking_for_ChatGPT_opens_a_BgGPT_conversation_and_says_so()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("Ведар, искам да си говоря с ChatGPT.");
        await h.IdleAsync();
        Assert.Contains("Отварям разговор с BgGPT", h.Spoken);
        Assert.Contains("вместо ChatGPT", h.Spoken);
        Assert.Contains("през интернет", h.Spoken);
        Assert.True(h.Orchestrator.InConversation);
        Assert.Equal(ListeningMode.Command, h.Listener.Mode);
    }

    [Fact]
    public async Task Muted_microphone_is_never_opened_and_push_to_talk_explains_how_to_unmute()
    {
        await using var h = new OrchestratorHarness(s => s.Microphone.Muted = true);
        await h.StartAsync();
        Assert.Equal(ListeningMode.Off, h.Listener.Mode);
        Assert.Equal(MicrophoneStatus.Muted, h.Orchestrator.MicrophoneStatus);

        h.Orchestrator.PushToTalk();
        await Wait.UntilAsync(() => h.Spoken.Contains("Микрофонът е изключен"));
        Assert.Contains("Контрол, Шифт и F8", h.Spoken);
        await h.IdleAsync();
        Assert.DoesNotContain(ListeningMode.Command, h.Listener.ModeHistory);
    }

    [Fact]
    public async Task Recognition_failure_reports_error_and_recovers()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        var states = new List<AssistantState>();
        h.Orchestrator.StateMachine.StateChanged += (_, e) => { lock (states) { states.Add(e.Current); } };
        h.Recognizer.ThrowOnce = new InvalidOperationException("boom");

        h.Orchestrator.PushToTalk();
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command);
        h.Listener.Emit();
        await Wait.UntilAsync(() => h.Spoken.Contains("Възникна грешка"));
        await h.IdleAsync();
        Assert.Contains(AssistantState.Error, states);
        Assert.Equal(AssistantState.Listening, h.Orchestrator.State); // ready again, listening quietly

        h.Recognizer.Enqueue("колко е часът");
        h.Orchestrator.PushToTalk();
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command);
        h.Listener.Emit();
        await Wait.UntilAsync(() => h.Spoken.Contains("четиринадесет"));
    }

    [Fact]
    public async Task Without_AI_unknown_requests_explain_once_then_ask_to_repeat()
    {
        await using var h = new OrchestratorHarness();
        h.Ai.Configured = false;
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("Разкажи ми приказка за дракони.");
        Assert.Contains("не са настроени", h.Spoken);
        h.Synth.Spoken.Clear();
        await h.Orchestrator.SubmitTextAsync("Разкажи ми друга приказка за принцеси.");
        Assert.DoesNotContain("не са настроени", h.Spoken);
        Assert.NotEmpty(h.Synth.Spoken);
    }

    [Fact]
    public async Task Repeat_says_the_last_answer_again()
    {
        await using var h = new OrchestratorHarness();
        await h.StartAsync();
        await h.Orchestrator.SubmitTextAsync("колко е часът");
        var first = h.Orchestrator.LastSpokenText;
        await h.Orchestrator.SubmitTextAsync("повтори");
        Assert.Equal(first, h.Orchestrator.LastSpokenText);
        Assert.Equal(2, h.Synth.Spoken.Count(s => s.Contains("четиринадесет")));
    }

    [Fact]
    public async Task Missing_microphone_is_reported_at_startup_but_typing_still_works()
    {
        await using var h = new OrchestratorHarness(s => s.General.GreetOnStartup = true);
        h.Listener.FailOnOpen = true;
        await h.StartAsync();
        Assert.Equal(MicrophoneStatus.NotFound, h.Orchestrator.MicrophoneStatus);
        Assert.Contains("Не намирам микрофон", h.Spoken);

        await h.Orchestrator.SubmitTextAsync("коя дата сме");
        Assert.Contains("ноември", h.Spoken);
    }

    [Fact]
    public async Task After_an_AI_answer_VEDAR_listens_for_a_follow_up_then_returns_to_wake_word_quietly()
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        await h.StartAsync();
        h.Ai.Reply("Разбира се.");
        await h.Orchestrator.SubmitTextAsync("Може ли да ми помогнеш с една рецепта?");
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.Command && h.Orchestrator.State == AssistantState.Listening);

        var spokenBefore = h.Synth.Spoken.Count;
        h.Listener.RaiseTimeout();
        await Wait.UntilAsync(() => h.Listener.Mode == ListeningMode.WakeWord);
        Assert.Equal(spokenBefore, h.Synth.Spoken.Count);
        Assert.True(h.Orchestrator.InConversation, "the conversation context is kept until it is closed");
    }

    [Fact]
    public async Task Ending_the_conversation_clears_context()
    {
        await using var h = new OrchestratorHarness(s => s.Ai.AnnounceCloudUse = false);
        await h.StartAsync();
        h.Ai.Reply("Здравей.");
        await h.Orchestrator.SubmitTextAsync("Да си поговорим за музика.");
        await h.Orchestrator.SubmitTextAsync("Затвори разговора.");
        Assert.False(h.Orchestrator.InConversation);
        Assert.Equal(0, h.Orchestrator.Conversation.Count);
    }

    [Fact]
    public async Task Hotkey_names_are_spoken_in_bulgarian() =>
        Assert.Equal("Контрол, Шифт и Интервал", await Task.FromResult(AssistantOrchestrator.HotkeyText("Ctrl+Shift+Space")));
}

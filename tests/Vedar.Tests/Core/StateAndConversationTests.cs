using Vedar.Core.Abstractions;
using Vedar.Core.Conversation;
using Vedar.Core.Localization;
using Vedar.Core.State;
using Vedar.Core.Text;

namespace Vedar.Tests.Core;

public class AssistantStateMachineTests
{
    [Fact]
    public void Starts_in_Starting_and_allows_normal_cycle()
    {
        var sm = new AssistantStateMachine();
        Assert.Equal(AssistantState.Starting, sm.Current);
        Assert.True(sm.TryTransition(AssistantState.Idle));
        Assert.True(sm.TryTransition(AssistantState.Listening));
        Assert.True(sm.TryTransition(AssistantState.Recognizing));
        Assert.True(sm.TryTransition(AssistantState.Processing));
        Assert.True(sm.TryTransition(AssistantState.Speaking));
        Assert.True(sm.TryTransition(AssistantState.Idle));
    }

    [Fact]
    public void Rejects_invalid_transition_and_keeps_state()
    {
        var sm = new AssistantStateMachine();
        sm.Transition(AssistantState.Idle);
        sm.Transition(AssistantState.Speaking);
        Assert.False(sm.TryTransition(AssistantState.Recognizing));
        Assert.Equal(AssistantState.Speaking, sm.Current);
        Assert.Throws<InvalidOperationException>(() => sm.Transition(AssistantState.Recognizing));
    }

    [Theory]
    [InlineData(AssistantState.Listening)]
    [InlineData(AssistantState.Recognizing)]
    [InlineData(AssistantState.Processing)]
    [InlineData(AssistantState.Speaking)]
    [InlineData(AssistantState.Error)]
    public void Any_state_can_return_to_Idle_Error_and_Stopped(AssistantState from)
    {
        Assert.True(AssistantStateMachine.IsAllowed(from, AssistantState.Idle));
        Assert.True(AssistantStateMachine.IsAllowed(from, AssistantState.Error));
        Assert.True(AssistantStateMachine.IsAllowed(from, AssistantState.Stopped));
    }

    [Fact]
    public void Raises_StateChanged_with_previous_and_current()
    {
        var sm = new AssistantStateMachine();
        AssistantStateChangedEventArgs? seen = null;
        sm.StateChanged += (_, e) => seen = e;
        sm.Transition(AssistantState.Idle, "ready");
        Assert.NotNull(seen);
        Assert.Equal(AssistantState.Starting, seen!.Previous);
        Assert.Equal(AssistantState.Idle, seen.Current);
        Assert.Equal("ready", seen.Detail);
    }
}

public class SentenceTests
{
    [Fact]
    public void Splitter_handles_abbreviations_and_decimals()
    {
        var parts = SentenceSplitter.Split("Здравей! Това е т.е. пример с 3.5 числа. Ул. Витоша е дълга? Да.");
        Assert.Equal(["Здравей!", "Това е т.е. пример с 3.5 числа.", "Ул. Витоша е дълга?", "Да."], parts);
    }

    [Fact]
    public void Splitter_breaks_very_long_sentences_at_commas()
    {
        var longText = string.Join(", ", Enumerable.Repeat("това е част от много дълго изречение", 15)) + ".";
        var parts = SentenceSplitter.Split(longText);
        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.True(p.Length <= SentenceSplitter.MaxChunkLength + 1));
        Assert.Equal(BulgarianText.NormalizeForMatching(longText), BulgarianText.NormalizeForMatching(string.Join(" ", parts)));
    }

    [Fact]
    public void Streaming_chunker_releases_sentences_as_tokens_arrive()
    {
        var c = new StreamingSentenceChunker();
        var ready = new List<string>();
        foreach (var token in new[] { "Здра", "вей", "! Как", " си", " днес", "? Аз", " съм", " добре" })
        {
            ready.AddRange(c.Append(token));
        }

        Assert.Equal(["Здравей!", "Как си днес?"], ready);
        Assert.Equal("Аз съм добре", c.Flush());
    }

    [Fact]
    public void Streaming_chunker_does_not_split_decimals_or_abbreviations()
    {
        var c = new StreamingSentenceChunker();
        var ready = new List<string>();
        foreach (var ch in "Цената е 3.50 лева, т.е. евтино. Край.")
        {
            ready.AddRange(c.Append(ch.ToString()));
        }

        var rest = c.Flush();
        if (rest is not null)
        {
            ready.Add(rest);
        }

        Assert.Equal(["Цената е 3.50 лева, т.е. евтино.", "Край."], ready);
    }
}

public class ConversationSessionTests
{
    [Fact]
    public void BuildRequest_starts_with_system_prompt_and_keeps_recent_messages()
    {
        var s = new ConversationSession(maxMessages: 4);
        for (int i = 0; i < 5; i++)
        {
            s.AddUser("въпрос " + i);
            s.AddAssistant("отговор " + i);
        }

        var request = s.BuildRequest("системен");
        Assert.Equal(ChatRole.System, request[0].Role);
        Assert.Equal("системен", request[0].Content);
        Assert.Equal(ChatRole.User, request[1].Role);
        Assert.Equal("въпрос 3", request[1].Content);
        Assert.Equal(5, request.Count);
    }

    [Fact]
    public void RemoveLastUserMessage_only_removes_trailing_user_message()
    {
        var s = new ConversationSession();
        s.AddUser("а");
        s.AddAssistant("б");
        s.RemoveLastUserMessage();
        Assert.Equal(2, s.Count);
        s.AddUser("в");
        s.RemoveLastUserMessage();
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void System_prompt_is_bulgarian_mentions_provider_and_forbids_markdown()
    {
        var prompt = SystemPromptBuilder.Build("BgGPT", "Стевко", new DateTimeOffset(2026, 11, 1, 14, 30, 0, TimeSpan.FromHours(2)));
        Assert.Contains("Ведар", prompt);
        Assert.Contains("BgGPT", prompt);
        Assert.Contains("не си ChatGPT", prompt);
        Assert.Contains("Markdown", prompt);
        Assert.Contains("Стевко", prompt);
        Assert.Contains("не можеш да изпълняваш команди", prompt);
        Assert.Contains("първи ноември", prompt);
    }
}

public class LocalizerTests
{
    [Fact]
    public void Bulgarian_is_default_and_all_english_keys_exist_in_bulgarian()
    {
        var bg = Localizer.Load("bg");
        var en = Localizer.Load("en");
        Assert.Equal("bg", bg.Language);
        Assert.Equal("Готов", bg["state.Idle"]);
        Assert.Equal("Ready", en["state.Idle"]);
        // Missing English keys fall back to Bulgarian.
        Assert.Equal(bg["speech.help"], en["speech.help"]);
    }

    [Fact]
    public void Pick_returns_one_of_the_variants_and_formats()
    {
        var loc = Localizer.Load("bg", new Random(1));
        var text = loc.Pick("speech.greeting.named", "Стевко");
        Assert.Contains("Стевко", text);
        Assert.Contains(text, loc.Variants("speech.greeting.named").Select(v => string.Format(v, "Стевко")));
    }

    [Fact]
    public void Unknown_language_falls_back_to_bulgarian() => Assert.Equal("bg", Localizer.Load("xx").Language);

    [Theory]
    [InlineData("state.Starting")]
    [InlineData("state.Idle")]
    [InlineData("state.Listening")]
    [InlineData("state.Recognizing")]
    [InlineData("state.Processing")]
    [InlineData("state.Speaking")]
    [InlineData("state.Error")]
    [InlineData("state.Stopped")]
    public void Every_state_has_a_bulgarian_name(string key)
    {
        var loc = Localizer.Load("bg");
        Assert.True(loc.Contains(key));
        Assert.NotEqual(key, loc[key]);
    }
}

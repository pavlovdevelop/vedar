using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Vedar.Commands;
using Vedar.Commands.Actions;
using Vedar.Commands.Files;
using Vedar.Commands.Matching;
using Vedar.Core.Abstractions;
using Vedar.Core.Localization;
using Vedar.Core.Settings;
using Vedar.Speech.WakeWord;
using Vedar.Tests.Support;

namespace Vedar.Tests.Commands;

public class IntentRecognitionTests
{
    private static readonly CommandDispatcher Dispatcher = new(new TestSettingsStore(), new FakeSystemActions(), new FakeUiHost(), new FakeConversationAi(),
        new FakeHistoryStore(), Localizer.Load("bg"), Log.Null<CommandDispatcher>(), isWakeToken: new WakeWordMatcher().IsWakeToken);

    [Theory]
    // time
    [InlineData("Колко е часът?", "time")]
    [InlineData("колко часа е", "time")]
    [InlineData("Ведар, колко е часът?", "time")]
    [InlineData("кажи ми колко е часът", "time")]
    [InlineData("Колко чесът?", "time")]
    [InlineData("колко е часа моля", "time")]
    // date / day
    [InlineData("Коя дата сме?", "date")]
    [InlineData("каква дата е днес", "date")]
    [InlineData("кажи ми датата", "date")]
    [InlineData("Кое число сме днес?", "date")]
    [InlineData("Кой ден сме днес?", "dayOfWeek")]
    [InlineData("какъв ден е днес", "dayOfWeek")]
    // apps
    [InlineData("Отвори браузъра.", "openBrowser")]
    [InlineData("пусни интернет", "openBrowser")]
    [InlineData("Утвори броузера", "openBrowser")]
    [InlineData("отвори бележника", "openNotepad")]
    [InlineData("Отвори папка Документи", "openDocuments")]
    [InlineData("отвори моите документи", "openDocuments")]
    [InlineData("Отвори Уикипедия", "openWebsite")]
    [InlineData("отвори сайта на БНР", "openWebsite")]
    [InlineData("пусни ютюб", "openWebsite")]
    // reading
    [InlineData("Прочети файл.", "readFile")]
    [InlineData("прочети ми документа", "readFile")]
    [InlineData("Прочети ми този текст.", "readClipboard")]
    [InlineData("прочети копирания текст", "readClipboard")]
    // control
    [InlineData("Повтори.", "repeat")]
    [InlineData("можеш ли да повториш", "repeat")]
    [InlineData("кажи го пак", "repeat")]
    [InlineData("Спри.", "stop")]
    [InlineData("стоп", "stop")]
    [InlineData("млъкни", "stop")]
    [InlineData("спри да говориш", "stop")]
    [InlineData("Усили звука.", "volumeUp")]
    [InlineData("по-силно", "volumeUp")]
    [InlineData("Намали звука.", "volumeDown")]
    [InlineData("по-тихо", "volumeDown")]
    [InlineData("звука на 50", "volumeSet")]
    [InlineData("сложи звука на петдесет процента", "volumeSet")]
    [InlineData("говори по-бавно", "voiceSlower")]
    [InlineData("говори по-бързо", "voiceFaster")]
    [InlineData("Отвори настройките.", "openSettings")]
    [InlineData("Отвори на стръките.", "openSettings")]
    // conversation
    [InlineData("Искам да поговорим.", "startConversation")]
    [InlineData("да си поговорим", "startConversation")]
    [InlineData("отвори разговор", "startConversation")]
    [InlineData("Искам да си говоря с ChatGPT.", "startConversation.chatgpt")]
    [InlineData("искам да говоря с чат джи пи ти", "startConversation.chatgpt")]
    [InlineData("Затвори разговора.", "endConversation")]
    [InlineData("край на разговора", "endConversation")]
    [InlineData("Довиждане.", "endConversation")]
    // misc
    [InlineData("Помощ.", "help")]
    [InlineData("какво можеш да правиш", "help")]
    [InlineData("изключи микрофона", "muteMic")]
    [InlineData("изключи се", "exit")]
    [InlineData("затвори Ведар", "exit")]
    [InlineData("изтрий историята на разговорите", "deleteHistory")]
    [InlineData("кой си ти", "whoAreYou")]
    [InlineData("благодаря", "thanks")]
    public async Task Understands_natural_bulgarian_commands(string text, string intent)
    {
        var match = Dispatcher.Recognizer.Recognize(text);
        var id = match?.IntentId;
        if (intent.StartsWith("startConversation"))
        {
            var result = await Dispatcher.DispatchAsync(text, CancellationToken.None);
            id = result.CommandId == "conversation.start.chatgpt" ? "startConversation.chatgpt" : result.CommandId == "conversation.start" ? "startConversation" : id;
        }

        Assert.Equal(intent, id);
    }

    [Theory]
    [InlineData("Колко е часът в Ню Йорк в момента?")]
    [InlineData("Разкажи ми нещо интересно за България.")]
    [InlineData("Скучно ми е.")]
    [InlineData("Кой е написал Под игото?")]
    [InlineData("Какво е времето утре в Пловдив?")]
    [InlineData("Отвори ми душата си и ми разкажи приказка.")]
    [InlineData("Защо небето е синьо?")]
    [InlineData("Колко е часовата разлика с Лондон?")]
    public void Questions_are_left_for_the_AI(string text) => Assert.Null(Dispatcher.Recognizer.Recognize(text));

    [Theory]
    [InlineData("петдесет", 50)]
    [InlineData("двадесет и пет", 25)]
    [InlineData("сто", 100)]
    [InlineData("75%", 75)]
    [InlineData("нула", 0)]
    public void Parses_spoken_numbers(string text, int expected) =>
        Assert.Equal(expected, BulgarianNumberParser.Parse(text.Split(' '), 0)?.Value);
}

public class CommandDispatcherTests
{
    private readonly TestSettingsStore _settings = new();
    private readonly FakeSystemActions _system = new();
    private readonly FakeUiHost _ui = new();
    private readonly FakeHistoryStore _history = new();
    private readonly CommandDispatcher _dispatcher;

    public CommandDispatcherTests()
    {
        _dispatcher = new CommandDispatcher(_settings, _system, _ui, new FakeConversationAi(), _history, Localizer.Load("bg"),
            Log.Null<CommandDispatcher>(), new FixedClock(new DateTimeOffset(2026, 11, 1, 9, 5, 0, TimeSpan.FromHours(2))));
    }

    private Task<CommandResult> Run(string text) => _dispatcher.DispatchAsync(text, CancellationToken.None);

    [Fact]
    public async Task Time_and_date_are_spoken_in_bulgarian_without_internet()
    {
        var time = await Run("колко е часът");
        Assert.Equal(CommandOutcome.Handled, time.Outcome);
        Assert.Contains("девет и пет минути", time.SpokenText);
        var date = await Run("коя дата сме");
        Assert.Contains("неделя, първи ноември две хиляди двадесет и шеста година", date.SpokenText);
    }

    [Fact]
    public async Task Opens_only_allow_listed_targets()
    {
        await Run("отвори браузъра");
        await Run("отвори бележника");
        await Run("отвори документи");
        await Run("отвори уикипедия");
        Assert.Equal(["browser", "notepad", "documents", "url:https://bg.wikipedia.org/"], _system.Actions);
    }

    [Fact]
    public async Task Unknown_website_lists_the_known_ones()
    {
        var r = await Run("отвори сайта на някаква непозната фирма");
        Assert.Contains("Познавам", r.SpokenText);
        Assert.Empty(_system.Actions);
    }

    [Fact]
    public async Task Failure_to_open_is_reported()
    {
        _system.Succeed = false;
        var r = await Run("отвори бележника");
        Assert.Equal(CommandOutcome.Failed, r.Outcome);
        Assert.Contains("Не успях", r.SpokenText);
    }

    [Fact]
    public async Task Volume_down_never_goes_below_the_audible_minimum()
    {
        _system.Volume = 0.2f;
        var r = await Run("намали звука");
        Assert.Contains("Няма да намалявам", r.SpokenText);
        Assert.Equal(CommandDispatcher.MinimumVoiceVolume, _system.Volume, 3);
        await Run("звука на 0");
        Assert.Equal(CommandDispatcher.MinimumVoiceVolume, _system.Volume, 3);
        await Run("звука на 80");
        Assert.Equal(0.8f, _system.Volume, 3);
    }

    [Fact]
    public async Task Exit_and_delete_history_require_confirmation()
    {
        var exit = await Run("изключи се");
        Assert.Equal(CommandOutcome.NeedsConfirmation, exit.Outcome);
        Assert.Equal(0, _ui.ExitRequests);
        await exit.ConfirmAction!(CancellationToken.None);
        Assert.Equal(1, _ui.ExitRequests);

        var delete = await Run("изтрий историята");
        Assert.Equal(CommandOutcome.NeedsConfirmation, delete.Outcome);
        Assert.Equal(0, _history.DeleteCount);
        await delete.ConfirmAction!(CancellationToken.None);
        Assert.Equal(1, _history.DeleteCount);
    }

    [Fact]
    public async Task Reads_the_file_the_user_selected()
    {
        var dir = TestModels.NewTempDir();
        var file = Path.Combine(dir, "бележка.txt");
        await File.WriteAllTextAsync(file, "Купи хляб и мляко.", Encoding.UTF8);
        _ui.FileToPick = file;
        var r = await Run("прочети файл");
        Assert.Equal(CommandOutcome.Handled, r.Outcome);
        Assert.Contains("Чета файла бележка.", r.SpokenText);
        Assert.Contains("Купи хляб и мляко.", r.SpokenText);
    }

    [Fact]
    public async Task Cancelled_file_dialog_is_reported()
    {
        _ui.FileToPick = null;
        Assert.Contains("Не е избран файл", (await Run("прочети файл")).SpokenText);
    }

    [Fact]
    public async Task Reads_clipboard_text_only_when_asked()
    {
        _ui.Clipboard = "Копиран текст за четене.";
        Assert.Equal("Копиран текст за четене.", (await Run("прочети копирания текст")).SpokenText);
        _ui.Clipboard = null;
        Assert.Contains("Няма копиран текст", (await Run("прочети ми този текст")).SpokenText);
    }

    [Fact]
    public async Task Voice_speed_command_changes_settings()
    {
        var before = _settings.Current.Voice.Rate;
        await Run("говори по-бавно");
        Assert.True(_settings.Current.Voice.Rate < before);
        Assert.Equal(1, _settings.SaveCount);
    }

    [Fact]
    public async Task Directives_for_assistant_level_commands()
    {
        Assert.Equal(AssistantDirective.Stop, (await Run("спри")).Directive);
        Assert.Equal(AssistantDirective.Repeat, (await Run("повтори")).Directive);
        Assert.Equal(AssistantDirective.StartConversation, (await Run("искам да поговорим")).Directive);
        Assert.Equal(AssistantDirective.EndConversation, (await Run("затвори разговора")).Directive);
        Assert.Equal(AssistantDirective.MuteMicrophone, (await Run("изключи микрофона")).Directive);
    }

    [Fact]
    public async Task Not_a_command_is_not_recognized()
    {
        Assert.Equal(CommandOutcome.NotRecognized, (await Run("Разкажи ми за Рилския манастир.")).Outcome);
    }
}

public class TextFileReaderTests
{
    private readonly TextFileReader _reader = new();

    [Fact]
    public void Reads_utf8_with_and_without_bom()
    {
        var dir = TestModels.NewTempDir();
        var a = Path.Combine(dir, "a.txt");
        var b = Path.Combine(dir, "b.txt");
        File.WriteAllText(a, "Здравей, свят!", new UTF8Encoding(true));
        File.WriteAllText(b, "Здравей, свят!", new UTF8Encoding(false));
        Assert.Equal("Здравей, свят!", _reader.Read(a).Text);
        Assert.Equal("Здравей, свят!", _reader.Read(b).Text);
    }

    [Fact]
    public void Reads_legacy_windows_1251_cyrillic()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Combine(TestModels.NewTempDir(), "old.txt");
        File.WriteAllBytes(path, Encoding.GetEncoding(1251).GetBytes("Стар документ на кирилица."));
        Assert.Equal("Стар документ на кирилица.", _reader.Read(path).Text);
    }

    [Fact]
    public void Reads_utf16()
    {
        var path = Path.Combine(TestModels.NewTempDir(), "u16.txt");
        File.WriteAllText(path, "Текст в UTF-16.", Encoding.Unicode);
        Assert.Equal("Текст в UTF-16.", _reader.Read(path).Text);
    }

    [Fact]
    public void Reads_word_docx_paragraphs()
    {
        var path = Path.Combine(TestModels.NewTempDir(), "doc.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write("""
                <?xml version="1.0" encoding="UTF-8"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
                <w:p><w:r><w:t>Първи абзац.</w:t></w:r></w:p>
                <w:p><w:r><w:t xml:space="preserve">Втори </w:t></w:r><w:r><w:t>абзац.</w:t></w:r></w:p>
                </w:body></w:document>
                """);
        }

        var text = _reader.Read(path).Text;
        Assert.Contains("Първи абзац.", text);
        Assert.Contains("Втори абзац.", text);
    }

    [Fact]
    public void Long_files_are_truncated_at_a_sentence()
    {
        var path = Path.Combine(TestModels.NewTempDir(), "long.txt");
        File.WriteAllText(path, string.Concat(Enumerable.Repeat("Това е изречение. ", 3000)));
        var r = _reader.Read(path);
        Assert.True(r.Truncated);
        Assert.True(r.Text.Length <= TextFileReader.MaxCharacters);
        Assert.EndsWith(".", r.Text);
    }

    [Fact]
    public void Unsupported_files_are_rejected()
    {
        Assert.False(TextFileReader.IsSupported("program.exe"));
        Assert.True(TextFileReader.IsSupported("писмо.DOCX"));
    }
}

public class SystemActionsSafetyTests
{
    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("ftp://example.com/x")]
    public void Refuses_non_web_urls(string url)
    {
        var started = new List<ProcessStartInfo>();
        var actions = new WindowsSystemActions(null, Log.Null<WindowsSystemActions>(), psi => { started.Add(psi); return true; });
        Assert.False(actions.OpenUrl(new Uri(url)));
        Assert.Empty(started);
    }

    [Fact]
    public void Opens_https_urls_through_the_shell_without_arguments()
    {
        var started = new List<ProcessStartInfo>();
        var actions = new WindowsSystemActions(null, Log.Null<WindowsSystemActions>(), psi => { started.Add(psi); return true; });
        Assert.True(actions.OpenUrl(new Uri("https://bg.wikipedia.org")));
        var psi = Assert.Single(started);
        Assert.True(psi.UseShellExecute);
        Assert.Equal("https://bg.wikipedia.org/", psi.FileName);
        Assert.Empty(psi.Arguments);
    }

    [Fact]
    public void System_actions_interface_has_no_way_to_run_arbitrary_commands()
    {
        var methods = typeof(ISystemActions).GetMethods().Select(m => m.Name).ToList();
        Assert.DoesNotContain(methods, m => m.Contains("Run", StringComparison.OrdinalIgnoreCase) || m.Contains("Execute", StringComparison.OrdinalIgnoreCase)
                                             || m.Contains("Shell", StringComparison.OrdinalIgnoreCase) || m.Contains("Command", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(7, methods.Count);
    }

    [Fact]
    public void Website_shortcuts_only_accept_http_and_https()
    {
        Assert.True(WebsiteShortcut.IsSafeUrl("https://bnr.bg"));
        Assert.False(WebsiteShortcut.IsSafeUrl("file:///C:/x.exe"));
        var s = new VedarSettings { Websites = [new WebsiteShortcut { Name = "Лош", Url = "file:///C:/Windows/notepad.exe" }] }.Normalize();
        Assert.Empty(s.Websites);
    }
}

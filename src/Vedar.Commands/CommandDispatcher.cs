using System.Globalization;
using Microsoft.Extensions.Logging;
using Vedar.Commands.Files;
using Vedar.Commands.Matching;
using Vedar.Core.Abstractions;
using Vedar.Core.Localization;
using Vedar.Core.Settings;
using Vedar.Core.Text;

namespace Vedar.Commands;

/// <summary>
/// Executes local voice commands. Works without internet and without AI.
/// Only the intents registered here exist; anything else is "not recognized" and may be passed to the
/// conversational AI as plain text — AI output can never trigger an action.
/// Sensitive actions (closing VEDAR, deleting history) always ask for confirmation.
/// </summary>
public sealed class CommandDispatcher : ICommandDispatcher
{
    /// <summary>Lowest system volume VEDAR sets by voice, so the user can still hear VEDAR.</summary>
    public const float MinimumVoiceVolume = 0.15f;
    public const float VolumeStep = 0.10f;

    private readonly ISettingsStore _settings;
    private readonly ISystemActions _system;
    private readonly IUserInterfaceHost _ui;
    private readonly IConversationAi _ai;
    private readonly IConversationHistoryStore _history;
    private readonly Localizer _loc;
    private readonly IClock _clock;
    private readonly ILogger<CommandDispatcher> _logger;
    private readonly TextFileReader _reader = new();
    private readonly IntentRecognizer _recognizer;

    public CommandDispatcher(
        ISettingsStore settings,
        ISystemActions system,
        IUserInterfaceHost ui,
        IConversationAi ai,
        IConversationHistoryStore history,
        Localizer localizer,
        ILogger<CommandDispatcher> logger,
        IClock? clock = null,
        Func<string, bool>? isWakeToken = null)
    {
        _settings = settings;
        _system = system;
        _ui = ui;
        _ai = ai;
        _history = history;
        _loc = localizer;
        _logger = logger;
        _clock = clock ?? SystemClock.Instance;
        _recognizer = BuildRecognizer(new IntentRecognizer(isWakeToken), this);
    }

    public IntentRecognizer Recognizer => _recognizer;

    /// <summary>All command patterns. Order matters only for ties.</summary>
    internal static IntentRecognizer BuildRecognizer(IntentRecognizer r, CommandDispatcher? owner)
    {
        Func<string, bool> isSite = slot => owner is null || owner.FindWebsite(slot) is not null;
        return r
            .Add("time", "колко е? часът|часа|час", 1)
            .Add("time", "колко часа стана|е", 1)
            .Add("time", "кажи ми? колко? е? часа|часът", 1)
            .Add("date", "коя|каква дата сме|е?", 2)
            .Add("date", "коя|каква е датата|дата", 2)
            .Add("date", "кажи ми? датата|дата", 1)
            .Add("date", "днешната дата", 1)
            .Add("date", "кое|какво число сме|е", 2)
            .Add("dayOfWeek", "кой|какъв ден сме|е", 2)
            .Add("openBrowser", "отвори|пусни|стартирай|включи браузъра|браузър|браузера|интернет|интернета", 1)
            .Add("openNotepad", "отвори|пусни|стартирай|включи бележника|бележник|нотпад|notepad|бележките", 1)
            .Add("openDocuments", "отвори|покажи папка|папката? моите? документи|документите", 1)
            .Add("openWebsite", "отвори|пусни|влез|покажи сайта|сайт|страницата {rest}", 3, _ => true)
            .Add("openWebsite", "отвори|пусни|влез|покажи {rest}", 0, isSite)
            .Add("readFile", "прочети|чети|прочитай файл|файла|файлът|документ|документа|документът", 2)
            .Add("readFile", "отвори файл|документ", 1)
            .Add("readClipboard", "прочети|чети копирания|копираното|копирания текст|текста?", 2)
            .Add("readClipboard", "прочети|чети клипборда|клипборд|клипбоарда", 2)
            .Add("readClipboard", "прочети|чети този|тоя|това текст|текста", 2)
            .Add("readClipboard", "прочети|чети текста|текст", 1)
            .Add("repeat", "повтори|повториш|повторете", 1)
            .Add("repeat", "кажи? го? пак|отново", 1)
            .Add("repeat", "какво каза|казах", 1)
            .Add("repeat", "не те? чух|разбрах", 1)
            .Add("stop", "спри|стоп|млъкни|тихо|стига|престани|достатъчно|спрете|мълчи", 1)
            .Add("stop", "спри да говориш|приказваш|четеш", 1)
            .Add("volumeSet", "звука|звук|звукът|звуците|силата на? {n}", 2)
            .Add("volumeSet", "сложи|направи|усили|намали|пусни звука|звук на? {n}", 2)
            .Add("volumeUp", "усили|увеличи|надуй|дай звука|звук|звукът", 1)
            .Add("volumeUp", "по силно|високо|силничко", 1)
            .Add("volumeUp", "звука|звукът е? тих|слаб", 1)
            .Add("volumeDown", "намали|намалете|спусни звука|звук|звукът", 1)
            .Add("volumeDown", "по тихо|ниско|тихичко", 1)
            .Add("volumeDown", "звука|звукът е? силен|висок", 1)
            .Add("voiceLouder", "говори|говориш по силно|високо|ясно", 1)
            .Add("voiceQuieter", "говори|говориш по тихо|ниско", 1)
            .Add("voiceFaster", "говори|говориш? по бързо|бързичко", 1)
            .Add("voiceSlower", "говори|говориш? по бавно|бавничко", 1)
            .Add("voiceSetup", "гласова|гласовата|първоначална|първоначалната настройка|настройката", 1)
            .Add("voiceSetup", "започни|пусни|направи настройката|настройка", 1)
            .Add("voiceSetup", "настрой ме|се", 1)
            .Add("voiceSetup", "настройка", 0)
            .Add("pasteKey", "постави|запиши|сложи|въведи|добави ключа|ключ", 3)
            .Add("pasteKey", "вземи ключа|ключ от? клипборда|клипборд", 1)
            .Add("openSettings", "отвори|покажи настройките|настройки", 1)
            .Add("openSettings", "настройки|настройките", 0)
            .Add("openMainWindow", "покажи|отвори прозореца|прозорец|главния", 2)
            .Add("openMainWindow", "покажи се", 0)
            .Add("startConversation.chatgpt", "chatgpt", 4)
            .Add("startConversation", "поговорим|поговорим|побъбрим|поприказваме|говорим|приказваме", 2)
            .Add("startConversation", "искам|нека|хайде да? си? говоря|поговоря|поговорим", 2)
            .Add("startConversation", "отвори|започни|започнем|стартирай|пусни разговор|разговора|чат|чата", 1)
            .Add("startConversation", "bggpt|изкуствен|изкуствения интелект?", 3)
            .Add("endConversation", "затвори|приключи|прекрати|спри|край|приключваме разговора|разговор|разговорът|чата|чат", 1)
            .Add("endConversation", "край на? разговора", 1)
            .Add("endConversation", "довиждане|чао", 1)
            .Add("endConversation", "това е всичко", 1)
            .Add("help", "помощ|помогни", 1)
            .Add("help", "какво можеш|умееш правиш?", 1)
            .Add("help", "какви команди|неща знаеш|има|разбираш|можеш?", 2)
            .Add("help", "какво да? кажа|казвам", 1)
            .Add("muteMic", "изключи|спри|заглуши микрофона|микрофон", 1)
            .Add("muteMic", "спри|престани да слушаш", 1)
            .Add("muteMic", "не слушай", 1)
            .Add("exit", "изключи|затвори|спри се", 0)
            .Add("exit", "изключи|затвори|спри ведар|ведара|програмата|приложението", 1)
            .Add("exit", "изход|излез", 0)
            .Add("deleteHistory", "изтрий|изчисти историята", 3)
            .Add("whoAreYou", "кой|какво си ти?", 1)
            .Add("whoAreYou", "как се казваш", 1)
            .Add("thanks", "благодаря|мерси|благодарим|благодаря", 1)
            .Add("howAreYou", "как си|сте", 1);
    }

    public IReadOnlyList<string> DescribeCommands() =>
    [
        "колко е часът", "коя дата сме", "кой ден сме", "отвори браузъра", "отвори бележника", "отвори документи",
        "отвори Уикипедия (или друг зададен сайт)", "прочети файл", "прочети копирания текст", "повтори", "спри",
        "усили звука / намали звука / звука на 50", "говори по-бавно / по-бързо", "отвори настройките",
        "искам да поговорим", "затвори разговора", "изключи микрофона", "помощ",
    ];

    public async Task<CommandResult> DispatchAsync(string text, CancellationToken cancellationToken)
    {
        var match = _recognizer.Recognize(text);
        if (match is null)
        {
            return CommandResult.NotRecognized;
        }

        // "Искам да си говоря с ChatGPT": open the conversation and explain that BgGPT is used.
        if (match.IntentId.StartsWith("startConversation", StringComparison.Ordinal) && _recognizer.Tokenize(text).Contains("chatgpt"))
        {
            match = match with { IntentId = "startConversation.chatgpt" };
        }

        _logger.LogInformation("Intent {Intent} (score {Score:F2}, extras {Extras}).", match.IntentId, match.Score, match.Extras);
        try
        {
            return await ExecuteAsync(match, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {Intent} failed.", match.IntentId);
            return CommandResult.Fail(match.IntentId, _loc.Get("speech.errorGeneric"));
        }
    }

    private async Task<CommandResult> ExecuteAsync(IntentMatch match, CancellationToken ct)
    {
        var now = _clock.Now;
        var id = match.IntentId;
        switch (id)
        {
            case "time":
                return CommandResult.Done(id, _loc.Pick("speech.time", BulgarianDateTime.SpokenTime(now.Hour, now.Minute)));
            case "date":
                return CommandResult.Done(id, _loc.Pick("speech.date", BulgarianDateTime.SpokenDate(now.DateTime)));
            case "dayOfWeek":
                return CommandResult.Done(id, _loc.Pick("speech.dayOfWeek", BulgarianDateTime.DaysOfWeek[(int)now.DayOfWeek]));
            case "openBrowser":
                return _system.OpenDefaultBrowser()
                    ? CommandResult.Done(id, _loc.Pick("speech.openBrowser"))
                    : CommandResult.Fail(id, _loc.Format("speech.openFailed", "браузъра"));
            case "openNotepad":
                return _system.OpenNotepad()
                    ? CommandResult.Done(id, _loc.Get("speech.openNotepad"))
                    : CommandResult.Fail(id, _loc.Format("speech.openFailed", "Бележника"));
            case "openDocuments":
                return _system.OpenDocumentsFolder()
                    ? CommandResult.Done(id, _loc.Get("speech.openDocuments"))
                    : CommandResult.Fail(id, _loc.Format("speech.openFailed", "папка Документи"));
            case "openWebsite":
                return OpenWebsite(match.Slot ?? string.Empty);
            case "readFile":
                return await ReadFileAsync(ct).ConfigureAwait(false);
            case "readClipboard":
                return await ReadClipboardAsync(ct).ConfigureAwait(false);
            case "repeat":
                return CommandResult.Done(id, null, AssistantDirective.Repeat);
            case "stop":
                return CommandResult.Done(id, null, AssistantDirective.Stop);
            case "volumeUp":
                return ChangeVolume(+VolumeStep);
            case "volumeDown":
                return ChangeVolume(-VolumeStep);
            case "volumeSet":
                return SetVolume(match.Number ?? 50);
            case "voiceLouder":
                return ChangeVoiceVolume(+0.15f);
            case "voiceQuieter":
                return ChangeVoiceVolume(-0.15f);
            case "voiceFaster":
                return ChangeRate(+0.15f, "speech.voice.faster");
            case "voiceSlower":
                return ChangeRate(-0.15f, "speech.voice.slower");
            case "voiceSetup":
                return CommandResult.Done(id, null, AssistantDirective.StartVoiceSetup);
            case "pasteKey":
                return CommandResult.Done(id, null, AssistantDirective.PasteApiKey);
            case "openSettings":
                _ui.ShowSettings();
                return CommandResult.Done(id, _loc.Get("speech.openSettings"));
            case "openMainWindow":
                _ui.ShowMainWindow();
                return CommandResult.Done(id, _loc.Get("speech.openMainWindow"));
            case "startConversation":
                return CommandResult.Done("conversation.start", null, AssistantDirective.StartConversation);
            case "startConversation.chatgpt":
                return CommandResult.Done("conversation.start.chatgpt", null, AssistantDirective.StartConversation);
            case "endConversation":
                return CommandResult.Done("conversation.end", null, AssistantDirective.EndConversation);
            case "help":
                return CommandResult.Done(id, _loc.Get("speech.help"));
            case "muteMic":
                return CommandResult.Done(id, null, AssistantDirective.MuteMicrophone);
            case "exit":
                return CommandResult.Confirm(id, _loc.Get("speech.exit.confirm"), _ =>
                {
                    _ui.RequestExit();
                    return Task.FromResult(CommandResult.Done(id, _loc.Get("speech.exit.bye")));
                });
            case "deleteHistory":
                return CommandResult.Confirm(id, "Сигурен ли си, че искаш да изтрия запазената история на разговорите? Кажи „да“ или „не“.", _ =>
                {
                    _history.DeleteAll();
                    return Task.FromResult(CommandResult.Done(id, _loc.Get("settings.privacy.historyDeleted")));
                });
            case "whoAreYou":
                return CommandResult.Done(id, _loc.Format("speech.whoAreYou", _ai.ActiveProviderName));
            case "thanks":
                return CommandResult.Done(id, _loc.Pick("speech.thanks"));
            case "howAreYou":
                return CommandResult.Done(id, _loc.Pick("speech.howAreYou"), AssistantDirective.None);
            default:
                return CommandResult.NotRecognized;
        }
    }

    public WebsiteShortcut? FindWebsite(string spoken)
    {
        var target = BulgarianText.NormalizeForMatching(spoken);
        foreach (var word in new[] { "сайта", "сайт", "страницата", "на" })
        {
            if (target.StartsWith(word + " ", StringComparison.Ordinal))
            {
                target = target[(word.Length + 1)..];
            }
        }

        WebsiteShortcut? best = null;
        double bestScore = 0;
        foreach (var site in _settings.Current.Websites)
        {
            foreach (var name in site.Aliases.Prepend(site.Name))
            {
                var candidate = BulgarianText.NormalizeForMatching(name);
                var score = FuzzyMatcher.Similarity(target, candidate);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = site;
                }
            }
        }

        return bestScore >= 0.75 ? best : null;
    }

    private CommandResult OpenWebsite(string spoken)
    {
        var site = FindWebsite(spoken);
        if (site is null)
        {
            var names = string.Join(", ", _settings.Current.Websites.Select(w => w.Name));
            return CommandResult.Done("openWebsite", _loc.Format("speech.websiteUnknown", names));
        }

        return WebsiteShortcut.IsSafeUrl(site.Url) && _system.OpenUrl(new Uri(site.Url))
            ? CommandResult.Done("openWebsite", _loc.Format("speech.openWebsite", site.Name))
            : CommandResult.Fail("openWebsite", _loc.Format("speech.openFailed", site.Name));
    }

    private async Task<CommandResult> ReadFileAsync(CancellationToken ct)
    {
        var path = await _ui.PickTextFileAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(path))
        {
            return CommandResult.Done("readFile", _loc.Get("speech.readFile.cancelled"));
        }

        if (!TextFileReader.IsSupported(path))
        {
            return CommandResult.Fail("readFile", _loc.Get("speech.readFile.unsupported"));
        }

        try
        {
            var result = await Task.Run(() => _reader.Read(path), ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                return CommandResult.Done("readFile", _loc.Get("speech.readFile.empty"));
            }

            var intro = _loc.Format("speech.readFile.start", result.FileName);
            if (result.Truncated)
            {
                intro += " " + _loc.Get("speech.readFile.tooLarge");
            }

            return CommandResult.Done("readFile", intro + "\n" + result.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not read the selected file.");
            return CommandResult.Fail("readFile", _loc.Get("speech.readFile.error"));
        }
    }

    private async Task<CommandResult> ReadClipboardAsync(CancellationToken ct)
    {
        var text = await _ui.GetClipboardTextAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return CommandResult.Done("readClipboard", _loc.Get("speech.readClipboard.empty"));
        }

        if (text.Length > TextFileReader.MaxCharacters)
        {
            text = text[..TextFileReader.MaxCharacters];
        }

        ct.ThrowIfCancellationRequested();
        return CommandResult.Done("readClipboard", text);
    }

    private CommandResult ChangeVolume(float delta)
    {
        var current = _system.GetSystemVolume();
        if (current is null)
        {
            return CommandResult.Fail("volume", _loc.Get("speech.volume.unsupported"));
        }

        var target = current.Value + delta;
        if (delta < 0 && target < MinimumVoiceVolume)
        {
            var level = _system.SetSystemVolume(MinimumVoiceVolume) ?? current.Value;
            return CommandResult.Done("volumeDown", _loc.Format("speech.volume.minimum", Percent(level)));
        }

        var result = _system.SetSystemVolume(Math.Clamp(target, MinimumVoiceVolume, 1f));
        if (result is null)
        {
            return CommandResult.Fail("volume", _loc.Get("speech.volume.unsupported"));
        }

        return CommandResult.Done(delta > 0 ? "volumeUp" : "volumeDown", _loc.Format(delta > 0 ? "speech.volume.up" : "speech.volume.down", Percent(result.Value)));
    }

    private CommandResult SetVolume(int percent)
    {
        var level = Math.Clamp(percent, 0, 100) / 100f;
        if (level < MinimumVoiceVolume)
        {
            var applied = _system.SetSystemVolume(MinimumVoiceVolume);
            return applied is null
                ? CommandResult.Fail("volumeSet", _loc.Get("speech.volume.unsupported"))
                : CommandResult.Done("volumeSet", _loc.Format("speech.volume.minimum", Percent(applied.Value)));
        }

        var result = _system.SetSystemVolume(level);
        return result is null
            ? CommandResult.Fail("volumeSet", _loc.Get("speech.volume.unsupported"))
            : CommandResult.Done("volumeSet", _loc.Format("speech.volume.set", Percent(result.Value)));
    }

    private CommandResult ChangeVoiceVolume(float delta)
    {
        var s = _settings.Current;
        s.Voice.Volume = Math.Clamp(s.Voice.Volume + delta, 0.2f, 1f);
        _settings.Save(s);
        return CommandResult.Done(delta > 0 ? "voiceLouder" : "voiceQuieter", _loc.Format("speech.volume.set", Percent(s.Voice.Volume)));
    }

    private CommandResult ChangeRate(float delta, string key)
    {
        var s = _settings.Current;
        s.Voice.Rate = Math.Clamp(s.Voice.Rate + delta, 0.5f, 2f);
        _settings.Save(s);
        return CommandResult.Done(key, _loc.Get(key));
    }

    private static string Percent(float level) => Math.Round(level * 100).ToString(CultureInfo.InvariantCulture);
}

using System.Globalization;
using Vedar.Core.Abstractions;
using Vedar.Core.Localization;
using Vedar.Core.Settings;
using Vedar.Core.Text;

namespace Vedar.Core.Setup;

/// <summary>What VEDAR says next in the spoken setup.</summary>
/// <param name="Finished">The setup is over (completed, skipped or paused).</param>
public sealed record SetupReply(string Text, bool Finished);

/// <summary>
/// The first-run setup as a spoken dialog, so that a blind user never has to type: name, speaking rate, how to
/// call and stop VEDAR, automatic start, and API keys taken from the clipboard ("копирай ключа и кажи „готово“").
/// Every question can be skipped with "пропусни"; "край на настройката" ends it.
/// </summary>
public sealed class VoiceSetupDialog
{
    private enum Step { Name, ConfirmName, Speed, Autostart, BgGptKey, GroqKey }

    private const float RateStep = 0.2f;
    private const float MinRate = 0.6f;
    private const float MaxRate = 2.0f;

    private readonly ISettingsStore _settings;
    private readonly Localizer _loc;
    private readonly ApiKeyPaste? _keys;
    private readonly IAutoStartManager? _autoStart;
    private readonly string _talkHotkey;
    private readonly string _stopHotkey;
    private Step _step = Step.Name;
    private string? _candidateName;
    private int _nameAttempts;
    private int _speedRounds;
    private int _keyAttempts;
    private int _silences;

    public VoiceSetupDialog(ISettingsStore settings, Localizer localizer, ApiKeyPaste? keys, IAutoStartManager? autoStart, string talkHotkeyText, string stopHotkeyText)
    {
        _settings = settings;
        _loc = localizer;
        _keys = keys;
        _autoStart = autoStart;
        _talkHotkey = talkHotkeyText;
        _stopHotkey = stopHotkeyText;
    }

    /// <summary>Words that help the recognizer with the expected answer (null = free speech, e.g. a name).</summary>
    public string? RecognitionPrompt => _step switch
    {
        Step.ConfirmName or Step.Autostart => "Да. Не.",
        Step.Speed => "По-бързо. По-бавно. Добре.",
        Step.BgGptKey or Step.GroqKey => "Готово. Пропусни.",
        _ => null,
    };

    /// <summary>
    /// How long to wait for the answer (seconds; 0 = the usual command window). Copying a key with the screen reader
    /// takes a while, so VEDAR waits a minute there.
    /// </summary>
    public int ListenSeconds => _step is Step.BgGptKey or Step.GroqKey ? KeyListenSeconds : 0;

    public const int KeyListenSeconds = 60;

    public SetupReply Start() => Ask(_loc.Get("setup.intro") + " " + _loc.Get("setup.askName"));

    /// <summary>Nothing was heard after a question: repeat it once, then pause the setup.</summary>
    public SetupReply OnSilence()
    {
        if (++_silences >= 2)
        {
            return new SetupReply(_loc.Get("setup.paused"), true);
        }

        // While a key is being copied, a short reminder is enough (the long explanation was given).
        return _step is Step.BgGptKey or Step.GroqKey
            ? Ask(_loc.Get("setup.keyUnclear"))
            : Ask(_loc.Get("setup.silence") + " " + CurrentQuestion());
    }

    /// <param name="userBusy">
    /// The user has used the keyboard since the question (for example to copy the key with the screen reader): an
    /// unclear answer is then probably the screen reader talking, and VEDAR keeps quiet instead of answering it.
    /// </param>
    public async Task<SetupReply> HandleAsync(string text, CancellationToken cancellationToken, bool userBusy = false)
    {
        _silences = 0;
        var norm = BulgarianText.NormalizeForMatching(text);
        if (IsQuit(norm))
        {
            return Finish(_loc.Get("setup.quit"));
        }

        var skip = IsSkip(norm);
        if (userBusy && !skip && (_step is Step.BgGptKey or Step.GroqKey) && !IsReady(norm) && ConfirmationParser.Parse(norm) == ConfirmationAnswer.Unknown)
        {
            return Ask(string.Empty); // keep listening quietly
        }
        switch (_step)
        {
            case Step.Name:
                if (skip)
                {
                    return Next(Step.Speed, _loc.Get("setup.nameSkipped"));
                }

                _candidateName = ExtractName(text);
                if (_candidateName is null)
                {
                    return ++_nameAttempts >= 3 ? Next(Step.Speed, _loc.Get("setup.nameSkipped")) : Ask(_loc.Get("setup.nameAgain"));
                }

                _step = Step.ConfirmName;
                return Ask(_loc.Format("setup.confirmName", _candidateName));

            case Step.ConfirmName:
                switch (ConfirmationParser.Parse(text))
                {
                    case ConfirmationAnswer.Yes:
                        var s = _settings.Current;
                        s.General.UserName = _candidateName!;
                        _settings.Save(s);
                        return Next(Step.Speed, _loc.Format("setup.nameSaved", _candidateName!));
                    case ConfirmationAnswer.No:
                        _step = Step.Name;
                        return ++_nameAttempts >= 3 ? Next(Step.Speed, _loc.Get("setup.nameSkipped")) : Ask(_loc.Get("setup.nameAgain"));
                    default:
                        return skip ? Next(Step.Speed, _loc.Get("setup.nameSkipped")) : Ask(_loc.Get("setup.yesNoUnclear"));
                }

            case Step.Speed:
                return HandleSpeed(norm, skip);

            case Step.Autostart:
                if (skip)
                {
                    return Next(Step.BgGptKey, null);
                }

                switch (ConfirmationParser.Parse(text))
                {
                    case ConfirmationAnswer.Yes:
                        SetAutoStart(true);
                        return Next(Step.BgGptKey, _loc.Get("setup.autostartOn"));
                    case ConfirmationAnswer.No:
                        SetAutoStart(false);
                        return Next(Step.BgGptKey, _loc.Get("setup.autostartOff"));
                    default:
                        return Ask(_loc.Get("setup.yesNoUnclear"));
                }

            case Step.BgGptKey:
            case Step.GroqKey:
                return await HandleKeyAsync(norm, skip, cancellationToken).ConfigureAwait(false);
        }

        return Finish(_loc.Get("setup.done"));
    }

    /// <summary>+1 faster, -1 slower, 0 keep as it is ("добре"), null unclear.</summary>
    private static int? SpeedAnswer(string norm)
    {
        var tokens = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool faster = norm.Contains("по бързо", StringComparison.Ordinal) || norm.Contains("побързо", StringComparison.Ordinal) || tokens.Contains("бързо") || tokens.Contains("ускори");
        bool slower = norm.Contains("по бавно", StringComparison.Ordinal) || norm.Contains("побавно", StringComparison.Ordinal) || tokens.Contains("бавно") || tokens.Contains("забави");
        if (!faster && !slower && (tokens.Contains("добре") || ConfirmationParser.Parse(norm) == ConfirmationAnswer.Yes))
        {
            return 0;
        }

        return faster == slower ? null : faster ? 1 : -1;
    }

    private SetupReply HandleSpeed(string norm, bool skip)
    {
        var answer = SpeedAnswer(norm);
        if (skip || answer == 0)
        {
            return Next(Step.Autostart, _loc.Format("setup.explainUse", _talkHotkey, _stopHotkey));
        }

        if (answer is null)
        {
            return Ask(_loc.Get("setup.speedUnclear"));
        }

        var faster = answer > 0;
        var s = _settings.Current;
        var rate = (float)Math.Round(Math.Clamp(s.Voice.Rate + (faster ? RateStep : -RateStep), MinRate, MaxRate), 2);
        var atLimit = Math.Abs(rate - s.Voice.Rate) < 0.01f;
        s.Voice.Rate = rate;
        _settings.Save(s);
        if (++_speedRounds >= 8)
        {
            return Next(Step.Autostart, _loc.Format("setup.explainUse", _talkHotkey, _stopHotkey));
        }

        return Ask(atLimit ? _loc.Get("setup.speedLimit") : _loc.Get(faster ? "setup.speedFaster" : "setup.speedSlower"));
    }

    private async Task<SetupReply> HandleKeyAsync(string norm, bool skip, CancellationToken ct)
    {
        var groq = _step == Step.GroqKey;
        if (skip || ConfirmationParser.Parse(norm) == ConfirmationAnswer.No || _keys is null)
        {
            return groq ? Finish(_loc.Get("setup.done")) : Next(Step.GroqKey, null);
        }

        if (!IsReady(norm))
        {
            return Ask(_loc.Get("setup.keyUnclear"));
        }

        var result = await _keys.PasteAsync(groq ? ApiKeyPaste.Groq : ApiKeyPaste.BgGpt, ct).ConfigureAwait(false);
        _keyAttempts++;
        if (result.Works || (result.Saved && _keyAttempts >= 3))
        {
            _keyAttempts = 0;
            return groq ? Finish(result.Spoken + " " + _loc.Get("setup.done")) : Next(Step.GroqKey, result.Spoken);
        }

        if (_keyAttempts >= 3)
        {
            _keyAttempts = 0;
            return groq ? Finish(result.Spoken + " " + _loc.Get("setup.done")) : Next(Step.GroqKey, result.Spoken);
        }

        return Ask(result.Spoken + " " + _loc.Get("setup.keyRetry"));
    }

    /// <summary>"Готово", "копирах", "да" — the key is on the clipboard.</summary>
    private static bool IsReady(string norm) =>
        norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(t => t is "готово" or "готов" or "готова" or "копирах" or "копирано" or "поставих")
        || ConfirmationParser.Parse(norm) == ConfirmationAnswer.Yes;

    private void SetAutoStart(bool enabled)
    {
        try
        {
            if (_autoStart is { IsSupported: true })
            {
                if (enabled)
                {
                    _autoStart.Enable();
                }
                else
                {
                    _autoStart.Disable();
                }
            }

            var s = _settings.Current;
            s.General.StartWithWindows = enabled;
            _settings.Save(s);
        }
        catch (Exception)
        {
            // The setting can be changed later in the settings window.
        }
    }

    private SetupReply Ask(string text) => new(text, false);

    private SetupReply Next(Step step, string? prefix)
    {
        _step = step;
        var question = CurrentQuestion();
        return Ask(string.IsNullOrWhiteSpace(prefix) ? question : prefix + " " + question);
    }

    private SetupReply Finish(string text)
    {
        var s = _settings.Current;
        s.General.FirstRunCompleted = true;
        _settings.Save(s);
        return new SetupReply(text, true);
    }

    private string CurrentQuestion() => _step switch
    {
        Step.Name => _loc.Get("setup.askName"),
        Step.ConfirmName => _loc.Format("setup.confirmName", _candidateName ?? string.Empty),
        Step.Speed => _loc.Get("setup.askSpeed"),
        Step.Autostart => _loc.Get("setup.askAutostart"),
        Step.BgGptKey => _loc.Get("setup.askBgGptKey"),
        _ => _loc.Get("setup.askGroqKey"),
    };

    private static bool IsQuit(string norm) =>
        norm.Contains("край на настройката", StringComparison.Ordinal) || norm.Contains("спри настройката", StringComparison.Ordinal)
        || norm.Contains("откажи настройката", StringComparison.Ordinal) || norm.Contains("прекрати настройката", StringComparison.Ordinal)
        || norm.Contains("без настройка", StringComparison.Ordinal);

    private static bool IsSkip(string norm)
    {
        var tokens = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(t => t is "пропусни" or "пропускам" or "нататък" or "следващ" or "следващия" or "следващото" or "прескочи")
               || norm.Contains("нямам ключ", StringComparison.Ordinal);
    }

    private static readonly string[] NamePrefixes =
    [
        "моето име е", "името ми е", "аз се казвам", "казвам се", "наричай ме", "викай ми", "викат ми", "аз съм", "казват ми", "ми викат",
    ];

    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "да", "не", "добре", "аз", "ми", "се", "е", "казвам", "името", "моето", "ведар", "здравей", "здрасти",
    };

    /// <summary>"Казвам се Стевко." → "Стевко"; at most three words; null if nothing that looks like a name.</summary>
    public static string? ExtractName(string? heard)
    {
        var norm = BulgarianText.NormalizeForMatching(heard);
        foreach (var prefix in NamePrefixes)
        {
            var i = norm.IndexOf(prefix, StringComparison.Ordinal);
            if (i >= 0)
            {
                norm = norm[(i + prefix.Length)..];
                break;
            }
        }

        var words = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !NotNames.Contains(w) && w.All(char.IsLetter))
            .Take(3)
            .Select(w => char.ToUpper(w[0], BulgarianText.Culture) + w[1..])
            .ToList();
        var name = string.Join(' ', words);
        return name.Length is >= 2 and <= 40 ? name : null;
    }
}

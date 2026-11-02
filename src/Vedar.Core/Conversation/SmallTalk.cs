using Vedar.Core.Abstractions;
using Vedar.Core.Localization;
using Vedar.Core.Text;

namespace Vedar.Core.Conversation;

/// <summary>How the user says they are, in answer to "А ти как си?".</summary>
public enum Feeling { Unknown, Good, SoSo, Bad }

/// <summary>
/// Short social exchanges — greetings, "как си", the answer to it, thanks and goodbyes — answered at once and
/// locally, the way a person would: by the time of day, with the user's name, and without saying "здравей" again
/// right after VEDAR has greeted. One instance is shared by the command dispatcher and the orchestrator, so both
/// know what was said a moment ago.
/// </summary>
public sealed class SmallTalk
{
    /// <summary>Command ids of small talk start with this; the orchestrator keeps these exchanges as conversation context.</summary>
    public const string CommandPrefix = "smalltalk.";

    /// <summary>"Добре." / "Ясно." after an answer: nothing to say, like a person who just nods.</summary>
    public const string AckCommandId = CommandPrefix + "ack";

    public const string FeelingCommandId = CommandPrefix + "feeling";

    /// <summary>Intents of greetings, "как си", thanks and goodbyes.</summary>
    public static bool IsSocialIntent(string? intentId) => intentId is "greeting" or "howAreYou" or "thanks" or "farewell";

    /// <summary>A greeting within this time counts as "we have already said hello".</summary>
    public static readonly TimeSpan GreetingMemory = TimeSpan.FromMinutes(10);

    /// <summary>A short answer like "добре" is taken as the answer to VEDAR's "А ти как си?" within this time.</summary>
    public static readonly TimeSpan QuestionMemory = TimeSpan.FromSeconds(90);

    private static readonly string[] BadPhrases =
    [
        "не съм добре", "не съм много добре", "не много добре", "не толкова добре", "не особено", "не е добре", "не съвсем",
        "не много", "не е много добре", "не ми е добре", "не се чувствам добре",
    ];

    private static readonly HashSet<string> BadWords = new(StringComparer.Ordinal)
    {
        "зле", "лошо", "тъжен", "тъжна", "тъжно", "уморен", "уморена", "уморено", "изморен", "изморена", "болен", "болна", "болно",
        "скучно", "самотен", "самотна", "самотно", "тежко", "ядосан", "ядосана", "нервен", "нервна", "притеснен", "притеснена",
        "разстроен", "разстроена", "отвратително", "ужасно", "кофти", "гадно", "мъчно", "боли", "болки", "депресиран", "депресирана",
    };

    private static readonly string[] SoSoPhrases =
    [
        "горе долу", "така и така", "криво ляво", "нищо особено", "както винаги", "като всеки ден", "по старому", "не се оплаквам", "бива",
    ];

    private static readonly HashSet<string> SoSoWords = new(StringComparer.Ordinal)
    {
        "нормално", "средно", "горедолу", "криволяво", "ок", "окей", "поносимо",
    };

    private static readonly HashSet<string> GoodWords = new(StringComparer.Ordinal)
    {
        "добре", "чудесно", "отлично", "супер", "страхотно", "прекрасно", "великолепно", "екстра", "перфектно", "фантастично", "хубаво",
        "щастлив", "щастлива", "радостен", "радостна", "весел", "весела", "бодър", "бодра", "топ",
    };

    private static readonly HashSet<string> GreetingWords = new(StringComparer.Ordinal)
    {
        "здравей", "здрасти", "здравейте", "здрасте", "привет", "утро", "добро", "добър", "добра", "ден", "вечер",
    };

    private readonly Localizer _loc;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private DateTimeOffset? _greetedAt;
    private DateTimeOffset? _askedHowAreYouAt;

    public SmallTalk(Localizer localizer, IClock? clock = null)
    {
        _loc = localizer;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>VEDAR asked "А ти как си?" a moment ago and waits for the answer.</summary>
    public bool AwaitsFeeling
    {
        get
        {
            lock (_gate)
            {
                return _askedHowAreYouAt is { } t && _clock.Now - t < QuestionMemory;
            }
        }
    }

    /// <summary>"Добро утро" / "Добър ден" / "Добър вечер" / "Здравей" by the hour.</summary>
    public string TimeGreeting() => _loc.Get(TimeGreetingKey(_clock.Now.Hour));

    internal static string TimeGreetingKey(int hour) => hour switch
    {
        >= 5 and < 11 => "smalltalk.time.morning",
        >= 11 and < 18 => "smalltalk.time.day",
        >= 18 => "smalltalk.time.evening",
        _ => "smalltalk.time.night",
    };

    /// <summary>The greeting when VEDAR starts ("Добър вечер, Павел! Аз съм Ведар. Слушам те.").</summary>
    public string StartupGreeting(string? userName)
    {
        lock (_gate)
        {
            _greetedAt = _clock.Now;
            var greet = TimeGreeting();
            return string.IsNullOrWhiteSpace(userName)
                ? _loc.Pick("speech.greeting.anonymous", greet)
                : _loc.Pick("speech.greeting.named", greet, userName.Trim());
        }
    }

    /// <summary>The user greets VEDAR ("Здравей, Ведар", "Добро утро").</summary>
    public string ReplyToGreeting(string heard, string? userName)
    {
        lock (_gate)
        {
            var now = _clock.Now;
            var greetedRecently = _greetedAt is { } g && now - g < GreetingMemory;
            _greetedAt = now;
            if (greetedRecently)
            {
                // VEDAR said hello a moment ago (for example when it started): carry on instead of greeting twice.
                _askedHowAreYouAt = now;
                return _loc.Pick("smalltalk.greetedBack", NameSuffix(userName));
            }

            var follow = _loc.Pick("smalltalk.greetingFollowUp");
            if (BulgarianText.NormalizeForMatching(follow).Contains("как си", StringComparison.Ordinal))
            {
                _askedHowAreYouAt = now;
            }

            return $"{MirrorGreeting(heard) ?? TimeGreeting()}{NameSuffix(userName)}! {follow}";
        }
    }

    /// <summary>"Как си?" — VEDAR answers and asks back (greeting first if the user greeted and VEDAR has not yet).</summary>
    public string ReplyToHowAreYou(string heard, string? userName)
    {
        lock (_gate)
        {
            var now = _clock.Now;
            var greet = ContainsGreeting(heard) && !(_greetedAt is { } g && now - g < GreetingMemory);
            _askedHowAreYouAt = now;
            var reply = _loc.Pick("speech.howAreYou");
            if (greet)
            {
                _greetedAt = now;
                reply = $"{MirrorGreeting(heard) ?? TimeGreeting()}{NameSuffix(userName)}! {reply}";
            }

            return reply;
        }
    }

    /// <summary>
    /// The answer to VEDAR's "А ти как си?". Null when VEDAR did not ask, when the text is not about how the user is,
    /// or when the user feels bad and the AI can answer with real empathy (it gets the exchange as context).
    /// </summary>
    public string? ReplyToFeeling(string heard, bool aiAvailable)
    {
        if (!AwaitsFeeling)
        {
            return null;
        }

        var feeling = ClassifyFeeling(heard);
        if (feeling == Feeling.Unknown)
        {
            return null;
        }

        lock (_gate)
        {
            _askedHowAreYouAt = null;
        }

        var reply = feeling switch
        {
            Feeling.Good => _loc.Pick("smalltalk.feeling.good"),
            Feeling.SoSo => _loc.Pick(aiAvailable ? "smalltalk.feeling.sosoAi" : "smalltalk.feeling.soso"),
            _ => aiAvailable ? null : _loc.Pick("smalltalk.feeling.bad"),
        };
        if (reply is not null && feeling != Feeling.Bad && AsksBack(heard))
        {
            reply = _loc.Get("smalltalk.meToo") + " " + reply;
        }

        return reply;
    }

    /// <summary>"Довиждане", "лека нощ", "чао" — a warm goodbye by the time of day.</summary>
    public string Farewell(string heard, string? userName)
    {
        lock (_gate)
        {
            _greetedAt = null;
            _askedHowAreYouAt = null;
        }

        var hour = _clock.Now.Hour;
        var night = BulgarianText.NormalizeForMatching(heard).Contains("лека нощ", StringComparison.Ordinal) || hour >= 21 || hour < 4;
        var key = night ? "smalltalk.farewell.night" : hour >= 18 ? "smalltalk.farewell.evening" : "smalltalk.farewell.day";
        return _loc.Pick(key, NameSuffix(userName));
    }

    public static Feeling ClassifyFeeling(string? heard)
    {
        var norm = BulgarianText.NormalizeForMatching(heard);
        if (norm.Length == 0)
        {
            return Feeling.Unknown;
        }

        var padded = " " + norm + " ";
        if (BadPhrases.Any(p => padded.Contains(" " + p + " ", StringComparison.Ordinal)))
        {
            return Feeling.Bad;
        }

        var tokens = norm.Split(' ');
        if (tokens.Any(BadWords.Contains))
        {
            return Feeling.Bad;
        }

        if (SoSoPhrases.Any(p => padded.Contains(" " + p + " ", StringComparison.Ordinal)) || tokens.Any(SoSoWords.Contains))
        {
            return Feeling.SoSo;
        }

        return tokens.Any(GoodWords.Contains) ? Feeling.Good : Feeling.Unknown;
    }

    public static bool ContainsGreeting(string? heard)
    {
        var tokens = BulgarianText.Tokenize(heard);
        return tokens.Any(t => t is "здравей" or "здрасти" or "здравейте" or "здрасте" or "привет")
               || Contains(tokens, "добро", "утро") || Contains(tokens, "добър", "ден") || Contains(tokens, "добър", "вечер") || Contains(tokens, "добра", "вечер");
    }

    /// <summary>True when every word of the text is part of a greeting ("Здравей", "Добро утро").</summary>
    public static bool IsOnlyGreeting(string? heard)
    {
        var tokens = BulgarianText.Tokenize(heard);
        return tokens.Length > 0 && ContainsGreeting(heard) && tokens.All(GreetingWords.Contains);
    }

    /// <summary>Answers the greeting the user used ("Добро утро" → "Добро утро"); null for a plain "здравей".</summary>
    private string? MirrorGreeting(string heard)
    {
        var tokens = BulgarianText.Tokenize(heard);
        if (Contains(tokens, "добро", "утро"))
        {
            return _loc.Get("smalltalk.time.morning");
        }

        if (Contains(tokens, "добър", "ден"))
        {
            return _loc.Get("smalltalk.time.day");
        }

        if (Contains(tokens, "добър", "вечер") || Contains(tokens, "добра", "вечер"))
        {
            return _loc.Get("smalltalk.time.evening");
        }

        return tokens.Any(t => t is "здравей" or "здрасти" or "здравейте" or "здрасте" or "привет") ? _loc.Get("smalltalk.time.night") : null;
    }

    /// <summary>"Добре съм, а ти?" — the user asks back.</summary>
    private static bool AsksBack(string heard)
    {
        var norm = " " + BulgarianText.NormalizeForMatching(heard) + " ";
        return norm.Contains(" а ти ", StringComparison.Ordinal) || norm.Contains(" ти как си ", StringComparison.Ordinal) || norm.Contains(" а при теб ", StringComparison.Ordinal);
    }

    private static bool Contains(string[] tokens, string first, string second)
    {
        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i] == first && tokens[i + 1] == second)
            {
                return true;
            }
        }

        return false;
    }

    private static string NameSuffix(string? userName) => string.IsNullOrWhiteSpace(userName) ? string.Empty : ", " + userName.Trim();
}

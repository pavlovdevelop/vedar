namespace Vedar.Core.Text;

/// <summary>
/// Recognises a sentence that was cut off in a pause: "искам да говорим за", "разкажи ми нещо и", "ами…".
/// Then VEDAR waits a little longer for the rest instead of answering half a question.
/// </summary>
public static class UtteranceCompleteness
{
    /// <summary>Words a Bulgarian sentence does not end with (prepositions, conjunctions, particles, fillers).</summary>
    private static readonly HashSet<string> NotLastWords = new(StringComparer.Ordinal)
    {
        "за", "на", "в", "във", "с", "със", "към", "от", "по", "до", "при", "без", "през", "след", "преди", "между",
        "около", "срещу", "чрез", "освен", "покрай", "под", "над", "пред", "зад", "у",
        "и", "или", "но", "а", "ако", "когато", "че", "да", "защото", "понеже", "докато", "както", "като", "нито",
        "който", "която", "което", "които", "какво", "как", "колко", "кой", "коя", "кое", "кои", "къде", "кога", "защо",
        "най", "много", "още", "един", "една", "едно", "някой", "някоя", "нещо", "този", "тази", "това", "тези",
        "моя", "моят", "моята", "моето", "моите", "твоя", "твоят", "нашия", "нашата",
        "ами", "ъъ", "ъ", "ъм", "мм", "ммм", "ее", "еее", "хм", "абе", "значи", "тоест", "демек",
    };

    /// <summary>What people say before the actual request ("Искам да те питам…", "Кажи ми…").</summary>
    private static readonly HashSet<string> Openers = new(StringComparer.Ordinal)
    {
        "искам да", "искам да те питам", "искам да питам", "искам да знам", "искам да говорим", "искам да поговорим",
        "искам да си поговорим", "да си поговорим", "кажи ми", "разкажи ми", "обясни ми", "имам въпрос", "имам един въпрос",
        "въпросът ми е", "чудя се", "интересува ме", "слушай", "виж", "виж сега", "знаеш ли", "може ли", "можеш ли",
        "да те питам", "един въпрос", "питам се",
    };

    public static bool LooksUnfinished(string? text)
    {
        var tokens = BulgarianText.Tokenize(text);
        return tokens.Length > 0 && NotLastWords.Contains(tokens[^1]);
    }

    /// <summary>
    /// Only an opener so far ("Искам да те питам.", "Кажи ми."): complete as a sentence, but the request usually
    /// follows, so VEDAR does not answer early.
    /// </summary>
    public static bool LooksLikeOpener(string? text) => Openers.Contains(BulgarianText.NormalizeForMatching(text));
}

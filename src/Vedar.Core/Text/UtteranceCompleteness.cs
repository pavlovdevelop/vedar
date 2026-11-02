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

    public static bool LooksUnfinished(string? text)
    {
        var tokens = BulgarianText.Tokenize(text);
        return tokens.Length > 0 && NotLastWords.Contains(tokens[^1]);
    }
}

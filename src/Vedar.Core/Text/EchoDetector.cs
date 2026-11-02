namespace Vedar.Core.Text;

/// <summary>
/// Recognizes VEDAR's own words coming back through the microphone — from the loudspeakers, or from a screen
/// reader that reads VEDAR's answer aloud — while VEDAR listens without the wake word. Such text must never be
/// taken as the user's next request (VEDAR would end up talking to itself).
/// </summary>
public static class EchoDetector
{
    /// <summary>Short replies ("Добре съм", "Да, разбира се") often repeat VEDAR's words; they are never echo.</summary>
    public const int MinWords = 4;

    /// <param name="heard">What the microphone heard.</param>
    /// <param name="spoken">What VEDAR said last.</param>
    public static bool IsLikelyEcho(string? heard, string? spoken)
    {
        var words = BulgarianText.Tokenize(heard);
        if (words.Length < MinWords)
        {
            return false;
        }

        var said = BulgarianText.Tokenize(spoken).ToHashSet(StringComparer.Ordinal);
        return said.Count > 0 && words.Count(said.Contains) >= 0.75 * words.Length;
    }
}

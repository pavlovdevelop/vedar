namespace Vedar.Core.Text;

/// <summary>What to do with something heard while VEDAR speaks.</summary>
public enum BargeInAction
{
    /// <summary>Not meant for VEDAR (or VEDAR's own voice): keep speaking.</summary>
    None,
    /// <summary>"Ведар, спри", "стоп", "стига": stop at once.</summary>
    Stop,
    /// <summary>"Ведар, …" with something else: stop speaking and handle the new request.</summary>
    Interrupt,
}

/// <summary>
/// Decides whether something heard while VEDAR speaks is meant for VEDAR ("Ведар, спри", "стоп", "Ведар, колко е часът").
/// </summary>
/// <remarks>
/// <para>Without headphones the microphone also hears VEDAR's own voice. Words that VEDAR is saying (or something
/// very close to them, as Whisper may spell an echo slightly differently) are therefore not counted as the user's.</para>
/// <para>The fast recognition model often garbles "спри" when it is mixed with VEDAR's voice ("сприк", "инсприт",
/// "Ведар из тига"; measured with the real models), so stop words are matched by their stems, and the user's own
/// "Ведар" followed by such a word is always a stop. In a long piece of audio a stop-like word alone is not
/// enough: the wake word must be heard too.</para>
/// </remarks>
public static class StopPhraseDetector
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "спри", "спрете", "спирай", "спирайте", "стоп", "стига", "млъкни", "млъкнете", "замълчи", "мълчи",
        "тихо", "достатъчно", "прекъсни", "прекъснете", "stop", "чакай", "почакай", "изчакай", "чакайте",
        "тига", // "Ведар, стига" is often heard as "Ведар из тига"
    };

    /// <summary>Pieces of up to this many words may stop VEDAR without the wake word.</summary>
    public const int ShortPieceWords = 5;

    public static bool IsStopRequest(string? heard, string? beingSpoken, Func<string, bool> isWakeToken) =>
        Classify(heard, beingSpoken, isWakeToken, startsWithWakeWord: false) == BargeInAction.Stop;

    /// <param name="heard">Transcript of the piece of audio.</param>
    /// <param name="beingSpoken">Everything VEDAR has said in the current turn (for the echo check).</param>
    /// <param name="isWakeToken">Recognises "Ведар" and the ways Whisper spells it.</param>
    /// <param name="startsWithWakeWord">The transcript starts with the wake word (as for a normal command).</param>
    public static BargeInAction Classify(string? heard, string? beingSpoken, Func<string, bool> isWakeToken, bool startsWithWakeWord)
    {
        var tokens = BulgarianText.Tokenize(heard);
        if (tokens.Length == 0)
        {
            return BargeInAction.None;
        }

        var spoken = BulgarianText.Tokenize(beingSpoken).ToHashSet(StringComparer.Ordinal);
        var own = tokens.Select(t => !IsEcho(t, spoken)).ToArray();
        int firstOwnWake = -1;
        bool ownStop = false;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (own[i] && firstOwnWake < 0 && isWakeToken(tokens[i]))
            {
                firstOwnWake = i;
                continue;
            }

            if (!IsStopLike(tokens[i]))
            {
                continue;
            }

            // The user's own "Ведар" and then a stop word: a stop even if VEDAR has said "спри" itself (e.g. in the help).
            if (firstOwnWake >= 0)
            {
                return BargeInAction.Stop;
            }

            ownStop |= own[i];
        }

        if (ownStop && tokens.Length <= ShortPieceWords)
        {
            return BargeInAction.Stop;
        }

        return startsWithWakeWord && firstOwnWake >= 0 ? BargeInAction.Interrupt : BargeInAction.None;
    }

    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal) { "а", "е", "и", "о", "у", "ъ", "ъъ", "мм", "хм", "ам", "ем" };

    /// <summary>Words heard that are not VEDAR's own (echo) and not mere sounds: the user's own words.</summary>
    public static int CountOwnWords(string? heard, string? beingSpoken)
    {
        var spoken = BulgarianText.Tokenize(beingSpoken).ToHashSet(StringComparer.Ordinal);
        return BulgarianText.Tokenize(heard).Count(t => t.Length >= 2 && !Fillers.Contains(t) && !IsEcho(t, spoken));
    }

    /// <summary>A stop word, or one of the ways the fast model garbles "спри" ("сприк", "инсприт", "изприт").</summary>
    public static bool IsStopLike(string token)
    {
        if (StopWords.Contains(token))
        {
            return true;
        }

        return token.Length >= 5 && (token.StartsWith("спри", StringComparison.Ordinal)
                                      || token.StartsWith("изпри", StringComparison.Ordinal)
                                      || token.StartsWith("испри", StringComparison.Ordinal)
                                      || token.Contains("спри", StringComparison.Ordinal));
    }

    private static bool IsEcho(string token, HashSet<string> spoken)
    {
        if (spoken.Contains(token))
        {
            return true;
        }

        // Whisper may spell VEDAR's own words slightly differently ("спря" → "спри"); treat those as echo too.
        return token.Length >= 4 && spoken.Any(s => Math.Abs(s.Length - token.Length) <= 1 && EditDistanceAtMostOne(s, token));
    }

    private static bool EditDistanceAtMostOne(string a, string b)
    {
        if (a.Length > b.Length)
        {
            (a, b) = (b, a);
        }

        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                i++;
                j++;
                continue;
            }

            if (++edits > 1)
            {
                return false;
            }

            if (a.Length == b.Length)
            {
                i++;
            }

            j++;
        }

        return edits + (b.Length - j) + (a.Length - i) <= 1;
    }
}

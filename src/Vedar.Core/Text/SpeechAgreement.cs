namespace Vedar.Core.Text;

/// <summary>
/// How alike two transcripts of the same audio are (the main model's and the fast model's). Real speech gives
/// similar letters even when both models garble it ("Колко е часът" / "Коку отчисут"); a noise gives unrelated
/// "words" ("Слъпи" / "Пърж"). Compares letters only (spaces are where the models disagree most): the letters found
/// in the same order in both, against the length of both (2·common / (a + b)). Measuring against the shorter text
/// alone let short unrelated words agree ("Отвори" / "Оте", 0.67 instead of 0.44).
/// </summary>
public static class SpeechAgreement
{
    /// <summary>About the same letters: with a human voice in the sound, this is speech.</summary>
    public const double Threshold = 0.5;

    /// <summary>Nearly the same: speech even if no voice was found in the sound (two models rarely invent the same).</summary>
    public const double Strong = 0.8;

    public static double Similarity(string a, string b)
    {
        var x = new string(a.Where(char.IsLetterOrDigit).ToArray());
        var y = new string(b.Where(char.IsLetterOrDigit).ToArray());
        if (x.Length == 0 || y.Length == 0)
        {
            return x.Length == y.Length ? 1 : 0;
        }

        return 2.0 * Lcs(x, y) / (x.Length + y.Length);
    }

    private static int Lcs(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }
}

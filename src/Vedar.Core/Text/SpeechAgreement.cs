namespace Vedar.Core.Text;

/// <summary>
/// How alike two transcripts of the same audio are (the main model's and the fast model's). Real speech gives
/// similar letters even when both models garble it ("Колко е часът" / "Коку отчисут"); a noise gives unrelated
/// "words" ("Слъпи" / "Пърж"). Compares letters only (spaces are where the models disagree most), as the share of
/// the shorter text found in order in the longer one.
/// </summary>
public static class SpeechAgreement
{
    public const double Threshold = 0.5;

    public static double Similarity(string a, string b)
    {
        var x = new string(a.Where(char.IsLetterOrDigit).ToArray());
        var y = new string(b.Where(char.IsLetterOrDigit).ToArray());
        if (x.Length == 0 || y.Length == 0)
        {
            return x.Length == y.Length ? 1 : 0;
        }

        // Longest common subsequence relative to the shorter text, damped when the lengths differ a lot.
        var lcs = Lcs(x, y);
        var shorter = Math.Min(x.Length, y.Length);
        var longer = Math.Max(x.Length, y.Length);
        return lcs / (double)shorter * Math.Min(1.0, 2.0 * shorter / longer);
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

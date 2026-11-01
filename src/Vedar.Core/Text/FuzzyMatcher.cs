namespace Vedar.Core.Text;

/// <summary>
/// Weighted edit distance tuned for typical Whisper confusions in Bulgarian
/// (unstressed vowels а/ъ, е/и/я, voicing pairs б/п, д/т, в/ф, з/с, ж/ш, г/к).
/// </summary>
public static class FuzzyMatcher
{
    private static readonly string[] SimilarGroups = ["аъя", "еия", "оу", "бп", "дт", "вф", "зс", "жш", "гк", "йи", "цс", "чш"];

    private static double SubstitutionCost(char a, char b)
    {
        if (a == b)
        {
            return 0;
        }

        foreach (var group in SimilarGroups)
        {
            if (group.Contains(a) && group.Contains(b))
            {
                return 0.4;
            }
        }

        return 1;
    }

    public static double Distance(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var prev = new double[b.Length + 1];
        var curr = new double[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var sub = prev[j - 1] + SubstitutionCost(a[i - 1], b[j - 1]);
                var del = prev[j] + 1;
                var ins = curr[j - 1] + 1;
                curr[j] = Math.Min(sub, Math.Min(del, ins));
            }

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    /// <summary>Similarity in [0,1]; 1 = identical.</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return 1;
        }

        var max = Math.Max(a.Length, b.Length);
        return 1 - Distance(a, b) / max;
    }

    /// <summary>
    /// Token match with a length-aware threshold: short words must match almost exactly,
    /// longer words tolerate one or two recognition errors.
    /// </summary>
    public static bool TokenMatches(string spoken, string expected)
    {
        if (spoken == expected)
        {
            return true;
        }

        if (expected.Length <= 2)
        {
            return false;
        }

        var threshold = expected.Length switch
        {
            <= 3 => 0.66,
            <= 5 => 0.70,
            <= 8 => 0.74,
            _ => 0.78,
        };
        return Similarity(spoken, expected) >= threshold;
    }
}

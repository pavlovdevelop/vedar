using Vedar.Infrastructure.Configuration;

namespace Vedar.Tests.Support;

/// <summary>Finds the real speech models (downloaded by build/fetch-models.ps1) for integration tests.</summary>
public static class TestModels
{
    private static readonly AppPaths Paths = new(dataRoot: Path.Combine(Path.GetTempPath(), "vedar-tests-data"));

    public static string? Find(string relative) => Paths.FindModel(relative);

    public static string? WhisperSmall => Find("whisper/ggml-small-q5_1.bin");
    public static string? WhisperBase => Find("whisper/ggml-base-q5_1.bin");
    public static string? PiperVoice => Find("piper/bg_BG-dimitar-medium/bg_BG-dimitar-medium.onnx");
    public static string? EspeakData => Find("piper/espeak-ng-data");

    public static Func<string, string?> Resolver => Find;

    public static string MissingMessage(string what) =>
        $"{what} is not available. Run build/fetch-models.ps1 to download the speech models (this is a real-model test, not a mock).";

    public static string TestDataDir => Path.Combine(AppContext.BaseDirectory, "TestData");

    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vedar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public static class TextMetrics
{
    public static int Levenshtein<T>(IList<T> a, IList<T> b)
    {
        var d = new int[b.Count + 1];
        for (int j = 0; j <= b.Count; j++) { d[j] = j; }
        for (int i = 1; i <= a.Count; i++)
        {
            int prev = d[0];
            d[0] = i;
            for (int j = 1; j <= b.Count; j++)
            {
                int tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (EqualityComparer<T>.Default.Equals(a[i - 1], b[j - 1]) ? 0 : 1));
                prev = tmp;
            }
        }

        return d[b.Count];
    }

    public static double Cer(string reference, string hypothesis)
    {
        var r = Vedar.Core.Text.BulgarianText.NormalizeForMatching(reference);
        var h = Vedar.Core.Text.BulgarianText.NormalizeForMatching(hypothesis);
        return r.Length == 0 ? 0 : Levenshtein(r.ToCharArray(), h.ToCharArray()) / (double)r.Length;
    }

    public static double Wer(string reference, string hypothesis)
    {
        var r = Vedar.Core.Text.BulgarianText.Tokenize(reference);
        var h = Vedar.Core.Text.BulgarianText.Tokenize(hypothesis);
        return r.Length == 0 ? 0 : Levenshtein(r, h) / (double)r.Length;
    }
}

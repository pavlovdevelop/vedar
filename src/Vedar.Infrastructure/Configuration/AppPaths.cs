namespace Vedar.Infrastructure.Configuration;

/// <summary>
/// File system locations. User data lives in %LOCALAPPDATA%\VEDAR (per Windows user, no admin rights needed).
/// <c>VEDAR_DATA_DIR</c> overrides the data root (used by tests and portable setups).
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? dataRoot = null, string? appBase = null)
    {
        DataRoot = dataRoot
            ?? Environment.GetEnvironmentVariable("VEDAR_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "VEDAR");
        AppBase = appBase ?? AppContext.BaseDirectory;
    }

    public string DataRoot { get; }
    public string AppBase { get; }
    public string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public string SecretsDir => Path.Combine(DataRoot, "secrets");
    public string LogsDir => Path.Combine(DataRoot, "logs");
    public string HistoryDir => Path.Combine(DataRoot, "history");
    public string CacheDir => Path.Combine(DataRoot, "cache");
    public string UserModelsDir => Path.Combine(DataRoot, "models");
    public string AppModelsDir => Path.Combine(AppBase, "models");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(CacheDir);
    }

    /// <summary>
    /// Model search order: VEDAR_MODELS_DIR, user models, installed models, then a "models" folder found by walking
    /// up from the executable (developer builds running from bin/).
    /// </summary>
    public IEnumerable<string> ModelSearchRoots()
    {
        var env = Environment.GetEnvironmentVariable("VEDAR_MODELS_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env;
        }

        yield return UserModelsDir;
        yield return AppModelsDir;
        var dir = new DirectoryInfo(AppBase);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "models");
            if (Directory.Exists(candidate) && !string.Equals(candidate, AppModelsDir, StringComparison.OrdinalIgnoreCase))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>Finds a model file by relative path (e.g. "whisper/ggml-small-q5_1.bin").</summary>
    public string? FindModel(string relativePath)
    {
        foreach (var root in ModelSearchRoots())
        {
            var path = Path.Combine(root, relativePath);
            if (File.Exists(path) || Directory.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}

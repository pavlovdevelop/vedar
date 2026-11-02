using Microsoft.Extensions.Logging;
using Vedar.Core.Abstractions;
using Vedar.Infrastructure.Configuration;

namespace Vedar.Infrastructure.Security;

/// <summary>Deletes all local user data: settings, secrets, history, logs, caches and the autostart entry.</summary>
public sealed class UserDataEraser
{
    private readonly AppPaths _paths;
    private readonly ISecretStore _secrets;
    private readonly IAutoStartManager _autoStart;
    private readonly IConversationHistoryStore _history;
    private readonly ILogger<UserDataEraser> _logger;

    public UserDataEraser(AppPaths paths, ISecretStore secrets, IAutoStartManager autoStart, IConversationHistoryStore history, ILogger<UserDataEraser> logger)
    {
        _paths = paths;
        _secrets = secrets;
        _autoStart = autoStart;
        _history = history;
        _logger = logger;
    }

    /// <summary>Returns the list of items that could not be deleted (empty on full success).</summary>
    public IReadOnlyList<string> EraseAll(bool includeDownloadedModels = false)
    {
        var failures = new List<string>();
        Try("api key", () => _secrets.Delete(SecretNames.BgGptApiKey), failures);
        Try("groq api key", () => _secrets.Delete(SecretNames.GroqApiKey), failures);
        Try("history", _history.DeleteAll, failures);
        Try("autostart", () => { if (_autoStart.IsSupported) { _autoStart.Disable(); } }, failures);
        Try("secrets", () => DeleteDir(_paths.SecretsDir), failures);
        Try("cache", () => DeleteDir(_paths.CacheDir), failures);
        Try("settings", () =>
        {
            foreach (var f in Directory.Exists(_paths.DataRoot) ? Directory.EnumerateFiles(_paths.DataRoot, "settings.json*") : [])
            {
                File.Delete(f);
            }
        }, failures);
        if (includeDownloadedModels)
        {
            Try("models", () => DeleteDir(_paths.UserModelsDir), failures);
        }

        _logger.LogInformation("User data erased. Failures: {Count}", failures.Count);
        // Logs last, so the line above is not written to a deleted file afterwards.
        Try("logs", () =>
        {
            if (Directory.Exists(_paths.LogsDir))
            {
                foreach (var f in Directory.EnumerateFiles(_paths.LogsDir))
                {
                    try { File.Delete(f); } catch (IOException) { /* current log may be in use */ }
                }
            }
        }, failures);
        return failures;
    }

    private static void DeleteDir(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private void Try(string what, Action action, List<string> failures)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to erase {What}.", what);
            failures.Add(what);
        }
    }
}

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vedar.Core.Abstractions;
using Vedar.Core.Settings;
using Vedar.Infrastructure.Configuration;
using Vedar.Infrastructure.Security;

namespace Vedar.Infrastructure.History;

/// <summary>
/// Optional local conversation history. Disabled by default; only writes when <see cref="AiSettings.SaveHistory"/> is on.
/// Each record is encrypted with DPAPI for the current Windows user.
/// </summary>
public sealed class EncryptedHistoryStore : IConversationHistoryStore
{
    private const string Purpose = "history";
    private readonly AppPaths _paths;
    private readonly ISettingsStore _settings;
    private readonly ILogger<EncryptedHistoryStore> _logger;
    private readonly object _gate = new();

    public EncryptedHistoryStore(AppPaths paths, ISettingsStore settings, ILogger<EncryptedHistoryStore> logger)
    {
        _paths = paths;
        _settings = settings;
        _logger = logger;
    }

    public void Append(string conversationId, ChatMessage message)
    {
        if (!_settings.Current.Ai.SaveHistory || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(new { t = DateTimeOffset.Now, r = message.Role.ToString(), c = message.Content });
            var cipher = DpapiSecretStore.ProtectBytes(Encoding.UTF8.GetBytes(json), Purpose);
            lock (_gate)
            {
                Directory.CreateDirectory(_paths.HistoryDir);
                File.AppendAllText(Path.Combine(_paths.HistoryDir, Sanitize(conversationId) + ".vhist"), Convert.ToBase64String(cipher) + "\n");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save history.");
        }
    }

    public IReadOnlyList<string> ListConversations()
    {
        if (!Directory.Exists(_paths.HistoryDir))
        {
            return [];
        }

        return Directory.EnumerateFiles(_paths.HistoryDir, "*.vhist").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderDescending().ToList();
    }

    /// <summary>Decrypts one conversation (for a future history viewer and for tests).</summary>
    public IReadOnlyList<ChatMessage> Read(string conversationId)
    {
        var path = Path.Combine(_paths.HistoryDir, Sanitize(conversationId) + ".vhist");
        if (!File.Exists(path))
        {
            return [];
        }

        var result = new List<ChatMessage>();
        foreach (var line in File.ReadAllLines(path).Where(l => l.Length > 0))
        {
            var json = Encoding.UTF8.GetString(DpapiSecretStore.UnprotectBytes(Convert.FromBase64String(line), Purpose));
            using var doc = JsonDocument.Parse(json);
            var role = Enum.Parse<ChatRole>(doc.RootElement.GetProperty("r").GetString()!);
            result.Add(new ChatMessage(role, doc.RootElement.GetProperty("c").GetString() ?? string.Empty));
        }

        return result;
    }

    public void DeleteAll()
    {
        lock (_gate)
        {
            if (Directory.Exists(_paths.HistoryDir))
            {
                Directory.Delete(_paths.HistoryDir, recursive: true);
            }
        }
    }

    private static string Sanitize(string id) => new(id.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
}

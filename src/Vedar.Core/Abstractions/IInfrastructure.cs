using Vedar.Core.Settings;

namespace Vedar.Core.Abstractions;

public interface ISettingsStore
{
    VedarSettings Current { get; }
    VedarSettings Load();
    void Save(VedarSettings settings);
    event EventHandler<VedarSettings>? SettingsChanged;
}

/// <summary>Protected storage for secrets such as the BgGPT API key.</summary>
public interface ISecretStore
{
    bool IsSupported { get; }
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
    bool Exists(string name);
}

public static class SecretNames
{
    public const string BgGptApiKey = "bggpt-api-key";
}

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset Now => DateTimeOffset.Now;
}

public interface IAutoStartManager
{
    bool IsSupported { get; }
    bool IsEnabled();
    /// <summary>True when the entry exists but the user disabled it in Task Manager → Startup apps.</summary>
    bool IsDisabledByUser();
    void Enable();
    void Disable();
}

public interface IConversationHistoryStore
{
    void Append(string conversationId, ChatMessage message);
    IReadOnlyList<string> ListConversations();
    void DeleteAll();
}

using System.Runtime.Versioning;
using Microsoft.Win32;
using Vedar.Core.Abstractions;

namespace Vedar.Infrastructure.Startup;

/// <summary>Abstraction over the per-user Run registry key (testable without touching the registry).</summary>
public interface IRunKeyStore
{
    bool IsSupported { get; }
    string? GetValue(string name);
    void SetValue(string name, string value);
    void DeleteValue(string name);
    /// <summary>Raw value from Explorer\StartupApproved\Run (set when the user toggles the app in Task Manager).</summary>
    byte[]? GetStartupApproved(string name);
}

/// <summary>
/// Starts VEDAR after the user signs in to Windows, using HKCU\...\CurrentVersion\Run.
/// This runs in the user's own context and needs no administrator rights.
/// </summary>
public sealed class AutoStartManager : IAutoStartManager
{
    public const string ValueName = "VEDAR";
    public const string BackgroundArgument = "--background";

    private readonly IRunKeyStore _store;
    private readonly string _executablePath;

    public AutoStartManager(IRunKeyStore store, string executablePath)
    {
        _store = store;
        _executablePath = executablePath;
    }

    public bool IsSupported => _store.IsSupported;

    public string CommandLine => $"\"{_executablePath}\" {BackgroundArgument}";

    public bool IsEnabled()
    {
        var value = _store.GetValue(ValueName);
        return value is not null && ExtractPath(value) is { } path &&
               string.Equals(Path.GetFullPath(path), Path.GetFullPath(_executablePath), StringComparison.OrdinalIgnoreCase);
    }

    public bool IsDisabledByUser()
    {
        var data = _store.GetStartupApproved(ValueName);
        // First byte: 0x02/0x06 = enabled, odd values (0x03, 0x07) = disabled in Task Manager.
        return data is { Length: > 0 } && (data[0] & 1) == 1;
    }

    public void Enable() => _store.SetValue(ValueName, CommandLine);

    public void Disable()
    {
        if (_store.GetValue(ValueName) is not null)
        {
            _store.DeleteValue(ValueName);
        }
    }

    internal static string? ExtractPath(string commandLine)
    {
        commandLine = commandLine.Trim();
        if (commandLine.StartsWith('"'))
        {
            var end = commandLine.IndexOf('"', 1);
            return end > 1 ? commandLine[1..end] : null;
        }

        var space = commandLine.IndexOf(' ');
        return space > 0 ? commandLine[..space] : commandLine;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsRunKeyStore : IRunKeyStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public bool IsSupported => true;

    public string? GetValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(name) as string;
    }

    public void SetValue(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public byte[]? GetStartupApproved(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: false);
        return key?.GetValue(name) as byte[];
    }
}

/// <summary>In-memory run key store for tests and non-Windows development.</summary>
public sealed class InMemoryRunKeyStore : IRunKeyStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, byte[]> Approved { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsSupported => true;
    public string? GetValue(string name) => _values.GetValueOrDefault(name);
    public void SetValue(string name, string value) => _values[name] = value;
    public void DeleteValue(string name) => _values.Remove(name);
    public byte[]? GetStartupApproved(string name) => Approved.GetValueOrDefault(name);
}

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Vedar.Core.Abstractions;
using Vedar.Infrastructure.Configuration;

namespace Vedar.Infrastructure.Security;

/// <summary>
/// Stores secrets encrypted with Windows DPAPI (<see cref="DataProtectionScope.CurrentUser"/>).
/// Only the same Windows user on the same computer can decrypt them. Secrets never go to settings.json or logs.
/// </summary>
public sealed partial class DpapiSecretStore : ISecretStore
{
    private readonly string _dir;
    private readonly ILogger<DpapiSecretStore> _logger;

    public DpapiSecretStore(AppPaths paths, ILogger<DpapiSecretStore> logger)
    {
        _dir = paths.SecretsDir;
        _logger = logger;
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex NamePattern();

    private string PathFor(string name)
    {
        if (!NamePattern().IsMatch(name))
        {
            throw new ArgumentException("Invalid secret name.", nameof(name));
        }

        return Path.Combine(_dir, name + ".dat");
    }

    private static byte[] Entropy(string name) => Encoding.UTF8.GetBytes("VEDAR.secret.v1:" + name);

    public bool Exists(string name) => File.Exists(PathFor(name));

    public string? Get(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }

        EnsureSupported();
        try
        {
            var plain = Unprotect(File.ReadAllBytes(path), Entropy(name));
            var value = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            SecretRedactor.Register(value);
            return value;
        }
        catch (CryptographicException ex)
        {
            // Happens if the file was copied from another user or computer.
            _logger.LogWarning(ex, "Secret {Name} cannot be decrypted for this user.", name);
            return null;
        }
    }

    public void Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        EnsureSupported();
        var path = PathFor(name);
        Directory.CreateDirectory(_dir);
        var plain = Encoding.UTF8.GetBytes(value.Trim());
        var cipher = Protect(plain, Entropy(name));
        CryptographicOperations.ZeroMemory(plain);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, cipher);
        File.Move(temp, path, overwrite: true);
        SecretRedactor.Register(value.Trim());
        _logger.LogInformation("Secret {Name} saved (DPAPI, current user).", name);
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path))
        {
            // Overwrite before deleting so the ciphertext does not linger in the same clusters.
            var len = new FileInfo(path).Length;
            File.WriteAllBytes(path, new byte[len]);
            File.Delete(path);
            _logger.LogInformation("Secret {Name} deleted.", name);
        }
    }

    private void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] data, byte[] entropy) => ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] data, byte[] entropy) => ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser);

    private static byte[] Protect(byte[] data, byte[] entropy) =>
        OperatingSystem.IsWindows() ? ProtectWindows(data, entropy) : throw new PlatformNotSupportedException();

    private static byte[] Unprotect(byte[] data, byte[] entropy) =>
        OperatingSystem.IsWindows() ? UnprotectWindows(data, entropy) : throw new PlatformNotSupportedException();

    /// <summary>Exposed for the history store, which encrypts each record the same way.</summary>
    internal static byte[] ProtectBytes(byte[] data, string purpose) => Protect(data, Entropy(purpose));

    internal static byte[] UnprotectBytes(byte[] data, string purpose) => Unprotect(data, Entropy(purpose));
}

/// <summary>Volatile secret store for platforms without DPAPI (development and tests on Linux). Nothing is persisted.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public bool IsSupported => true;
    public bool Exists(string name) { lock (_values) { return _values.ContainsKey(name); } }
    public string? Get(string name) { lock (_values) { return _values.GetValueOrDefault(name); } }

    public void Set(string name, string value)
    {
        lock (_values)
        {
            _values[name] = value.Trim();
        }

        SecretRedactor.Register(value.Trim());
    }

    public void Delete(string name) { lock (_values) { _values.Remove(name); } }
}

using System.Text;
using Vedar.Core.Abstractions;
using Vedar.Core.Settings;
using Vedar.Desktop.Input;
using Vedar.Infrastructure.Configuration;
using Vedar.Infrastructure.History;
using Vedar.Infrastructure.Security;
using Vedar.Infrastructure.Startup;
using Vedar.Tests.Support;

namespace Vedar.Tests.Windows;

/// <summary>Real Windows APIs (DPAPI, registry). These run only on Windows.</summary>
public class DpapiSecretStoreWindowsTests
{
    [Fact]
    public void Stores_api_key_encrypted_for_current_user_and_reads_it_back()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var paths = new AppPaths(TestModels.NewTempDir());
        var store = new DpapiSecretStore(paths, Log.Null<DpapiSecretStore>());
        const string key = "bggpt-test-key-ÄÖÜ-ключ-0123456789";
        store.Set(SecretNames.BgGptApiKey, key);

        Assert.True(store.Exists(SecretNames.BgGptApiKey));
        Assert.Equal(key, store.Get(SecretNames.BgGptApiKey));
        var file = Path.Combine(paths.SecretsDir, SecretNames.BgGptApiKey + ".dat");
        var bytes = File.ReadAllBytes(file);
        Assert.DoesNotContain("bggpt-test-key", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("bggpt-test-key", Encoding.Unicode.GetString(bytes));
        Assert.False(File.Exists(paths.SettingsFile) && File.ReadAllText(paths.SettingsFile).Contains(key));

        store.Delete(SecretNames.BgGptApiKey);
        Assert.False(store.Exists(SecretNames.BgGptApiKey));
        Assert.Null(store.Get(SecretNames.BgGptApiKey));
    }

    [Fact]
    public void Ciphertext_from_another_purpose_cannot_be_decrypted_as_the_key()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var paths = new AppPaths(TestModels.NewTempDir());
        Directory.CreateDirectory(paths.SecretsDir);
        File.WriteAllBytes(Path.Combine(paths.SecretsDir, SecretNames.BgGptApiKey + ".dat"), DpapiSecretStore.ProtectBytes(Encoding.UTF8.GetBytes("x"), "other-purpose"));
        Assert.Null(new DpapiSecretStore(paths, Log.Null<DpapiSecretStore>()).Get(SecretNames.BgGptApiKey));
    }

    [Fact]
    public void Optional_history_is_encrypted_on_disk()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var paths = new AppPaths(TestModels.NewTempDir());
        var settings = new TestSettingsStore(new VedarSettings { Ai = new AiSettings { SaveHistory = true } });
        var history = new EncryptedHistoryStore(paths, settings, Log.Null<EncryptedHistoryStore>());
        history.Append("conv-1", ChatMessage.User("Тайна рецепта за баница"));
        history.Append("conv-1", ChatMessage.Assistant("Ето я."));
        var raw = string.Concat(Directory.GetFiles(paths.HistoryDir).Select(File.ReadAllText));
        Assert.DoesNotContain("баница", raw);
        Assert.Equal(["Тайна рецепта за баница", "Ето я."], history.Read("conv-1").Select(m => m.Content));
        history.DeleteAll();
        Assert.False(Directory.Exists(paths.HistoryDir));
    }

    [Fact]
    public void History_is_not_written_when_disabled()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var paths = new AppPaths(TestModels.NewTempDir());
        var history = new EncryptedHistoryStore(paths, new TestSettingsStore(), Log.Null<EncryptedHistoryStore>());
        history.Append("c", ChatMessage.User("нищо"));
        Assert.False(Directory.Exists(paths.HistoryDir));
    }
}

public class RegistryAutoStartTests
{
    /// <summary>Maps the product value name to a unique test name so a real VEDAR installation is not touched.</summary>
    private sealed class IsolatedRunKeyStore(string testName) : IRunKeyStore
    {
        private readonly WindowsRunKeyStore _inner = new();
        public bool IsSupported => true;
        public string? GetValue(string name) => _inner.GetValue(testName);
        public void SetValue(string name, string value) => _inner.SetValue(testName, value);
        public void DeleteValue(string name) => _inner.DeleteValue(testName);
        public byte[]? GetStartupApproved(string name) => _inner.GetStartupApproved(testName);
    }

    [Fact]
    public void Enables_and_disables_start_with_windows_in_HKCU_Run()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var name = "VEDAR-Test-" + Guid.NewGuid().ToString("N")[..8];
        var exe = Path.Combine(Path.GetTempPath(), "VEDAR Test", "VEDAR.exe");
        var manager = new AutoStartManager(new IsolatedRunKeyStore(name), exe);
        var raw = new WindowsRunKeyStore();
        try
        {
            Assert.False(manager.IsEnabled());
            manager.Enable();
            Assert.True(manager.IsEnabled());
            Assert.Equal($"\"{exe}\" --background", raw.GetValue(name));
            manager.Disable();
            Assert.False(manager.IsEnabled());
            Assert.Null(raw.GetValue(name));
        }
        finally
        {
            raw.DeleteValue(name);
        }
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+Space", "Ctrl+Shift+Space")]
    [InlineData("ctrl+shift+back", "Ctrl+Shift+Back")]
    [InlineData("Ctrl+Shift+Backspace", "Ctrl+Shift+Back")]
    [InlineData("Ctrl+Shift+F8", "Ctrl+Shift+F8")]
    [InlineData("Alt+Shift+1", "Alt+Shift+1")]
    [InlineData("Win+Alt+V", "Alt+Win+V")]
    public void Parses_and_formats(string text, string expected)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(expected, hk.ToString());
    }

    [Theory]
    [InlineData("Space")]
    [InlineData("Shift+A")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+NoSuchKey")]
    [InlineData("")]
    public void Rejects_combinations_that_would_steal_typing(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Default_hotkeys_are_valid_and_distinct()
    {
        var a = new ActivationSettings();
        Assert.True(Hotkey.TryParse(a.PushToTalkHotkey, out var talk));
        Assert.True(Hotkey.TryParse(a.StopHotkey, out var stop));
        Assert.True(Hotkey.TryParse(a.MicrophoneToggleHotkey, out var mic));
        Assert.Equal(3, new[] { talk, stop, mic }.Distinct().Count());
    }
}

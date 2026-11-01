using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Vedar.Core.Abstractions;
using Vedar.Core.Accessibility;
using Vedar.Core.Settings;
using Vedar.Infrastructure.Configuration;
using Vedar.Infrastructure.Logging;
using Vedar.Infrastructure.Models;
using Vedar.Infrastructure.Security;
using Vedar.Infrastructure.Startup;
using Vedar.Infrastructure.SystemInfo;
using Vedar.Tests.Support;

namespace Vedar.Tests.Infrastructure;

public class SettingsStoreTests
{
    private static (JsonSettingsStore Store, AppPaths Paths) Create()
    {
        var paths = new AppPaths(TestModels.NewTempDir());
        return (new JsonSettingsStore(paths, Log.Null<JsonSettingsStore>()), paths);
    }

    [Fact]
    public void Missing_file_gives_safe_defaults()
    {
        var (store, _) = Create();
        var s = store.Load();
        Assert.Equal("bg", s.General.UiLanguage);
        Assert.False(s.General.FirstRunCompleted);
        Assert.Equal("Ctrl+Shift+Space", s.Activation.PushToTalkHotkey);
        Assert.Equal("bg_BG-dimitar-medium", s.Voice.VoiceId);
        Assert.Equal("https://api.bggpt.ai/v1", s.Ai.BgGptEndpoint);
        Assert.Equal("bggpt-gemma-3-27b-fp8", s.Ai.BgGptModel);
        Assert.False(s.Ai.SaveHistory);
        Assert.False(s.Privacy.DiagnosticLogging);
        Assert.False(s.Microphone.Muted);
    }

    [Fact]
    public void Saves_and_loads_settings_atomically()
    {
        var (store, paths) = Create();
        var s = store.Load();
        s.General.UserName = "Стевко";
        s.Voice.Rate = 1.2f;
        s.Activation.PushToTalkHotkey = "Ctrl+Alt+V";
        store.Save(s);
        Assert.False(File.Exists(paths.SettingsFile + ".tmp"));
        var json = File.ReadAllText(paths.SettingsFile);
        Assert.Contains("Стевко", json); // human-readable Cyrillic

        var reloaded = new JsonSettingsStore(paths, Log.Null<JsonSettingsStore>()).Load();
        Assert.Equal("Стевко", reloaded.General.UserName);
        Assert.Equal(1.2f, reloaded.Voice.Rate, 3);
        Assert.Equal("Ctrl+Alt+V", reloaded.Activation.PushToTalkHotkey);
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_defaults_are_used()
    {
        var (store, paths) = Create();
        Directory.CreateDirectory(paths.DataRoot);
        File.WriteAllText(paths.SettingsFile, "{ this is not json ");
        var s = store.Load();
        Assert.Equal("bg", s.General.UiLanguage);
        Assert.Single(Directory.GetFiles(paths.DataRoot, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        var (store, paths) = Create();
        Directory.CreateDirectory(paths.DataRoot);
        File.WriteAllText(paths.SettingsFile, """{ "Voice": { "Rate": 10, "Volume": -3 }, "Microphone": { "Sensitivity": 7 }, "Ai": { "MaxResponseTokens": 1 }, "General": { "UserName": "  Иван  " } }""");
        var s = store.Load();
        Assert.Equal(2.0f, s.Voice.Rate);
        Assert.Equal(0.05f, s.Voice.Volume);
        Assert.Equal(1.0f, s.Microphone.Sensitivity);
        Assert.Equal(50, s.Ai.MaxResponseTokens);
        Assert.Equal("Иван", s.General.UserName);
    }

    [Fact]
    public void Settings_model_has_no_place_for_an_api_key()
    {
        var props = typeof(VedarSettings).Assembly.GetTypes().Where(t => t.Namespace == "Vedar.Core.Settings").SelectMany(t => t.GetProperties()).Select(p => p.Name);
        Assert.DoesNotContain(props, p => p.Contains("Key", StringComparison.OrdinalIgnoreCase) && !p.Contains("Hotkey", StringComparison.OrdinalIgnoreCase)
                                          || p.Contains("Secret", StringComparison.OrdinalIgnoreCase) || p.Contains("Token", StringComparison.OrdinalIgnoreCase) && p != "MaxResponseTokens");
    }

    [Fact]
    public void Raises_change_event()
    {
        var (store, _) = Create();
        VedarSettings? seen = null;
        store.SettingsChanged += (_, s) => seen = s;
        store.Save(store.Load());
        Assert.NotNull(seen);
    }
}

public class SecretTests
{
    [Fact]
    public void Redactor_masks_registered_secrets_and_common_key_formats()
    {
        SecretRedactor.Register("my-secret-value-123");
        Assert.Equal("key=***!", SecretRedactor.Redact("key=my-secret-value-123!"));
        Assert.Equal("Authorization: Bearer ***", SecretRedactor.Redact("Authorization: Bearer abc.def-ghi_jkl"));
        Assert.Equal("sk-***", SecretRedactor.Redact("sk-ABCDEFGHIJKLMNOPQRST"));
        Assert.Equal("\"api_key\": \"***\"", SecretRedactor.Redact("\"api_key\": \"zzzzzzzzzz\""));
    }

    [Fact]
    public void File_logger_never_writes_secrets()
    {
        var dir = TestModels.NewTempDir();
        const string key = "bggpt-live-key-ABCDEF0123456789";
        SecretRedactor.Register(key);
        using (var provider = new FileLoggerProvider(dir, LogLevel.Debug))
        {
            var logger = provider.CreateLogger("Test");
            logger.LogInformation("Using key {Key} and header Bearer {Key2}", key, key);
            logger.LogError(new InvalidOperationException("failed with " + key), "Boom");
        }

        var text = string.Concat(Directory.GetFiles(dir).Select(File.ReadAllText));
        Assert.Contains("Using key", text);
        Assert.DoesNotContain(key, text);
    }

    [Fact]
    public void Dpapi_store_is_windows_only_and_refuses_elsewhere()
    {
        var store = new DpapiSecretStore(new AppPaths(TestModels.NewTempDir()), Log.Null<DpapiSecretStore>());
        if (OperatingSystem.IsWindows())
        {
            Assert.True(store.IsSupported);
            return; // real DPAPI is covered by the Windows tests
        }

        Assert.False(store.IsSupported);
        Assert.Throws<PlatformNotSupportedException>(() => store.Set(SecretNames.BgGptApiKey, "x-123456"));
        Assert.Null(store.Get(SecretNames.BgGptApiKey));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("C:\\x")]
    [InlineData("")]
    public void Secret_names_cannot_escape_the_secrets_folder(string name)
    {
        var store = new DpapiSecretStore(new AppPaths(TestModels.NewTempDir()), Log.Null<DpapiSecretStore>());
        Assert.ThrowsAny<ArgumentException>(() => store.Exists(name));
    }
}

public class AutoStartTests
{
    private const string Exe = @"C:\Program Files\VEDAR\VEDAR.exe";

    [Fact]
    public void Enable_writes_quoted_path_with_background_flag()
    {
        var store = new InMemoryRunKeyStore();
        var m = new AutoStartManager(store, Exe);
        Assert.False(m.IsEnabled());
        m.Enable();
        Assert.Equal("\"C:\\Program Files\\VEDAR\\VEDAR.exe\" --background", store.GetValue(AutoStartManager.ValueName));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(m.IsEnabled());
        }

        m.Disable();
        Assert.Null(store.GetValue(AutoStartManager.ValueName));
        Assert.False(m.IsEnabled());
    }

    [Fact]
    public void Entry_pointing_to_another_installation_is_not_ours()
    {
        var store = new InMemoryRunKeyStore();
        store.SetValue(AutoStartManager.ValueName, "\"D:\\Old\\VEDAR.exe\" --background");
        Assert.False(new AutoStartManager(store, Path.Combine(Path.GetTempPath(), "VEDAR.exe")).IsEnabled());
    }

    [Fact]
    public void Detects_when_the_user_disabled_it_in_task_manager()
    {
        var store = new InMemoryRunKeyStore();
        var m = new AutoStartManager(store, Exe);
        store.Approved[AutoStartManager.ValueName] = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(m.IsDisabledByUser());
        store.Approved[AutoStartManager.ValueName] = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.False(m.IsDisabledByUser());
    }

    [Theory]
    [InlineData("\"C:\\A B\\VEDAR.exe\" --background", "C:\\A B\\VEDAR.exe")]
    [InlineData("C:\\VEDAR\\VEDAR.exe --background", "C:\\VEDAR\\VEDAR.exe")]
    [InlineData("C:\\VEDAR\\VEDAR.exe", "C:\\VEDAR\\VEDAR.exe")]
    public void Extracts_executable_path(string commandLine, string expected) => Assert.Equal(expected, AutoStartManager.ExtractPath(commandLine));
}

public class ModelDownloaderTests : IAsyncLifetime
{
    private MockHttpServer _server = null!;
    private readonly byte[] _payload = RandomNumberGenerator.GetBytes(300_000);

    public async ValueTask InitializeAsync() => _server = await MockHttpServer.StartAsync();

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private ModelInfo Model(string sha) => new("test", "Тест", ModelKind.WhisperStt, "whisper/test.bin", _server.BaseUrl + "/test.bin", sha, _payload.Length, 1, "", false);

    [Fact]
    public async Task Downloads_verifies_and_reports_progress()
    {
        _server.Handler = (ctx, _) => { ctx.Response.ContentLength = _payload.Length; return ctx.Response.Body.WriteAsync(_payload).AsTask(); };
        var root = TestModels.NewTempDir();
        var reports = new List<double>();
        var sha = Convert.ToHexString(SHA256.HashData(_payload)).ToLowerInvariant();
        var path = await new ModelDownloader(new HttpClient(), Log.Null<ModelDownloader>()).DownloadAsync(Model(sha), root, new SyncProgress(reports), CancellationToken.None);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(path));
        Assert.Equal(1.0, reports[^1]);
        Assert.True(reports.Count > 3);
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task Corrupted_download_is_rejected_and_removed()
    {
        _server.Handler = (ctx, _) => ctx.Response.Body.WriteAsync(_payload).AsTask();
        var root = TestModels.NewTempDir();
        await Assert.ThrowsAsync<ModelIntegrityException>(() =>
            new ModelDownloader(new HttpClient(), Log.Null<ModelDownloader>()).DownloadAsync(Model(new string('0', 64)), root, null, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(root, "whisper", "test.bin")));
        Assert.False(File.Exists(Path.Combine(root, "whisper", "test.bin.part")));
    }

    [Fact]
    public void Catalog_contains_only_multilingual_whisper_models_with_pinned_hashes()
    {
        Assert.All(ModelCatalog.Whisper, m =>
        {
            Assert.DoesNotContain(".en.", m.RelativePath);
            Assert.Matches("^[0-9a-f]{64}$", m.Sha256);
            Assert.StartsWith("https://huggingface.co/ggerganov/whisper.cpp/", m.Url);
        });
        Assert.Contains(ModelCatalog.Whisper, m => m.RelativePath.EndsWith(RecognitionSettings.DefaultModel));
    }

    private sealed class SyncProgress(List<double> list) : IProgress<double>
    {
        public void Report(double value) => list.Add(value);
    }
}

public class UserDataTests
{
    [Fact]
    public void Erase_all_removes_settings_history_logs_secrets_and_autostart()
    {
        var paths = new AppPaths(TestModels.NewTempDir());
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsFile, "{}");
        Directory.CreateDirectory(paths.SecretsDir);
        File.WriteAllText(Path.Combine(paths.SecretsDir, "bggpt-api-key.dat"), "cipher");
        File.WriteAllText(Path.Combine(paths.LogsDir, "vedar-20261101.log"), "log");
        var secrets = new InMemorySecretStore();
        secrets.Set(SecretNames.BgGptApiKey, "abcdefgh");
        var runKeys = new InMemoryRunKeyStore();
        var autostart = new AutoStartManager(runKeys, "C:\\VEDAR\\VEDAR.exe");
        autostart.Enable();
        var history = new FakeHistoryStore();

        var failures = new UserDataEraser(paths, secrets, autostart, history, Log.Null<UserDataEraser>()).EraseAll();
        Assert.Empty(failures);
        Assert.False(File.Exists(paths.SettingsFile));
        Assert.False(Directory.Exists(paths.SecretsDir));
        Assert.Empty(Directory.GetFiles(paths.LogsDir));
        Assert.Null(secrets.Get(SecretNames.BgGptApiKey));
        Assert.Null(runKeys.GetValue(AutoStartManager.ValueName));
        Assert.Equal(1, history.DeleteCount);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(8, true)]
    [InlineData(32, true)]
    public void Local_AI_requires_enough_memory(double gb, bool ok) => Assert.Equal(ok, SystemResources.Evaluate(gb, 8).SufficientForLocalAi);

    [Fact]
    public void App_paths_honour_override_and_find_models_by_walking_up()
    {
        var root = TestModels.NewTempDir();
        var bin = Path.Combine(root, "src", "app", "bin");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(root, "models", "whisper"));
        File.WriteAllText(Path.Combine(root, "models", "whisper", "m.bin"), "x");
        var paths = new AppPaths(Path.Combine(root, "data"), bin);
        Assert.Equal(Path.Combine(root, "models", "whisper", "m.bin"), paths.FindModel(Path.Combine("whisper", "m.bin")));
        Assert.Null(paths.FindModel(Path.Combine("whisper", "missing.bin")));
    }
}

public class ScreenReaderPolicyTests
{
    [Theory]
    // No screen reader → never notify.
    [InlineData(ScreenReaderNotificationMode.All, UiNotificationKind.Error, false, false, false)]
    // Off → never.
    [InlineData(ScreenReaderNotificationMode.Off, UiNotificationKind.Error, false, true, false)]
    // Auto → only what VEDAR did not say aloud, and not mere status.
    [InlineData(ScreenReaderNotificationMode.Auto, UiNotificationKind.Response, true, true, false)]
    [InlineData(ScreenReaderNotificationMode.Auto, UiNotificationKind.Response, false, true, true)]
    [InlineData(ScreenReaderNotificationMode.Auto, UiNotificationKind.Status, false, true, false)]
    [InlineData(ScreenReaderNotificationMode.Auto, UiNotificationKind.Error, false, true, true)]
    // Important → errors and important changes only.
    [InlineData(ScreenReaderNotificationMode.Important, UiNotificationKind.Error, true, true, true)]
    [InlineData(ScreenReaderNotificationMode.Important, UiNotificationKind.Response, false, true, false)]
    // All → everything.
    [InlineData(ScreenReaderNotificationMode.All, UiNotificationKind.Response, true, true, true)]
    public void Avoids_double_speech(ScreenReaderNotificationMode mode, UiNotificationKind kind, bool spoken, bool readerActive, bool expected) =>
        Assert.Equal(expected, ScreenReaderNotificationPolicy.ShouldNotify(mode, kind, spoken, readerActive));
}

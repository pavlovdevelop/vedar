using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Vedar.Accessibility;
using Vedar.Audio.Dsp;
using Vedar.Audio.Windows;
using Vedar.Core.Abstractions;
using Vedar.Core.Localization;
using Vedar.Core.Settings;
using Vedar.Core.Text;
using Vedar.Desktop.Hosting;
using Vedar.Desktop.Input;
using Vedar.Infrastructure.Configuration;
using Vedar.Infrastructure.Models;
using Vedar.Infrastructure.SystemInfo;

namespace Vedar.Desktop.ViewModels;

public sealed record Option<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// Settings dialog. Edits a copy of the settings; nothing changes until "Запази" is pressed.
/// The API key is written only to the DPAPI secret store, never to settings.json.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<IReadOnlyList<string>> _reRegisterHotkeys;
    private readonly Localizer _loc;
    private readonly VedarSettings _original;

    public SettingsViewModel(AppServices services, Func<IReadOnlyList<string>> reRegisterHotkeys)
    {
        _services = services;
        _reRegisterHotkeys = reRegisterHotkeys;
        _loc = services.Localizer;
        _original = services.Settings.Current;
        var s = JsonSettingsStore.Clone(_original);

        _userName = s.General.UserName;
        _greetOnStartup = s.General.GreetOnStartup;
        _startMinimized = s.General.StartMinimizedToTray;
        _startWithWindows = SafeAutoStartEnabled();
        AutoStartDisabledByUser = SafeAutoStartDisabledByUser();
        Languages = [new("bg", "Български"), new("en", "English")];
        _selectedLanguage = Languages.FirstOrDefault(l => l.Value == s.General.UiLanguage) ?? Languages[0];

        _voiceRate = s.Voice.Rate;
        _voiceVolume = Math.Round(s.Voice.Volume * 100);
        _earcons = s.Voice.Earcons;
        OutputDevices = DeviceOptions(() => services.AudioOutput.GetDevices(), _loc["settings.outputDefault"]);
        _selectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Value == s.Voice.OutputDeviceId) ?? OutputDevices[0];

        InputDevices = DeviceOptions(() => services.AudioInput.GetDevices(), _loc["settings.mic.default"]);
        _selectedInputDevice = InputDevices.FirstOrDefault(d => d.Value == s.Microphone.DeviceId) ?? InputDevices[0];
        _sensitivity = Math.Round(s.Microphone.Sensitivity * 100);
        _gain = s.Microphone.Gain;
        _noiseFilter = s.Microphone.NoiseFilter;
        _microphoneMuted = s.Microphone.Muted;

        SttModels = new ObservableCollection<Option<string>>(ModelCatalog.Whisper.Select(m =>
            new Option<string>(Path.GetFileName(m.RelativePath), $"{m.DisplayName} — {(services.Paths.FindModel(m.RelativePath) is null ? _loc["settings.stt.notInstalled"] : _loc["settings.stt.installed"])}")));
        _selectedSttModel = SttModels.FirstOrDefault(m => m.Value == s.Recognition.ModelFile) ?? SttModels[1];

        _handsFree = s.Activation.HandsFreeEnabled;
        _pushToTalkHotkey = s.Activation.PushToTalkHotkey;
        _stopHotkey = s.Activation.StopHotkey;
        _micToggleHotkey = s.Activation.MicrophoneToggleHotkey;
        _followUpSeconds = s.Activation.FollowUpSeconds;

        AiProviders = Enum.GetValues<AiProviderKind>().Select(k => new Option<AiProviderKind>(k, _loc["settings.ai.provider." + k])).ToList();
        _selectedAiProvider = AiProviders.First(p => p.Value == s.Ai.Provider);
        _bgGptEndpoint = s.Ai.BgGptEndpoint;
        _bgGptModel = s.Ai.BgGptModel;
        _ollamaEndpoint = s.Ai.OllamaEndpoint;
        _ollamaModel = s.Ai.OllamaModel;
        _maxTokens = s.Ai.MaxResponseTokens;
        _announceCloud = s.Ai.AnnounceCloudUse;
        UpdateKeyStatus();
        var resources = SystemResources.Evaluate();
        ResourcesText = _loc.Format("settings.ai.resources", Math.Round(resources.TotalMemoryGb, 1), resources.Explanation);

        _saveHistory = s.Ai.SaveHistory;
        _diagnosticLogging = s.Privacy.DiagnosticLogging;
        NotificationModes = Enum.GetValues<ScreenReaderNotificationMode>().Select(m => new Option<ScreenReaderNotificationMode>(m, _loc["settings.a11y.notifications." + m])).ToList();
        _selectedNotificationMode = NotificationModes.First(m => m.Value == s.Accessibility.ScreenReaderNotifications);
        _speakStatus = s.Accessibility.SpeakStatusChanges;
        _fontScale = s.Accessibility.FontScale;
        var reader = ScreenReaderDetector.Detect();
        ScreenReaderText = reader.IsActive ? _loc.Format("settings.a11y.screenReader", reader.Name) : _loc["settings.a11y.screenReaderNone"];
    }

    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<Option<string>> Languages { get; }
    public IReadOnlyList<Option<string?>> OutputDevices { get; }
    public IReadOnlyList<Option<string?>> InputDevices { get; }
    public ObservableCollection<Option<string>> SttModels { get; }
    public IReadOnlyList<Option<AiProviderKind>> AiProviders { get; }
    public ObservableCollection<string> OllamaModels { get; } = [];
    public IReadOnlyList<Option<ScreenReaderNotificationMode>> NotificationModes { get; }
    public bool AutoStartDisabledByUser { get; }
    public string AutoStartDisabledText => AutoStartDisabledByUser ? _loc["settings.autostart.disabledByUser"] : string.Empty;
    public string ResourcesText { get; }
    public string ScreenReaderText { get; }
    public string DataFolder => _services.Paths.DataRoot;

    /// <summary>The new API key typed by the user (set by the view from the PasswordBox; never persisted here).</summary>
    public string? PendingApiKey { get; set; }

    [ObservableProperty] private string _userName;
    [ObservableProperty] private bool _greetOnStartup;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private Option<string> _selectedLanguage;
    [ObservableProperty] private double _voiceRate;
    [ObservableProperty] private double _voiceVolume;
    [ObservableProperty] private bool _earcons;
    [ObservableProperty] private Option<string?> _selectedOutputDevice;
    [ObservableProperty] private Option<string?> _selectedInputDevice;
    [ObservableProperty] private double _sensitivity;
    [ObservableProperty] private double _gain;
    [ObservableProperty] private bool _noiseFilter;
    [ObservableProperty] private bool _microphoneMuted;
    [ObservableProperty] private Option<string> _selectedSttModel;
    [ObservableProperty] private bool _handsFree;
    [ObservableProperty] private string _pushToTalkHotkey;
    [ObservableProperty] private string _stopHotkey;
    [ObservableProperty] private string _micToggleHotkey;
    [ObservableProperty] private int _followUpSeconds;
    [ObservableProperty] private Option<AiProviderKind> _selectedAiProvider;
    [ObservableProperty] private string _bgGptEndpoint;
    [ObservableProperty] private string _bgGptModel;
    [ObservableProperty] private string _ollamaEndpoint;
    [ObservableProperty] private string _ollamaModel;
    [ObservableProperty] private int _maxTokens;
    [ObservableProperty] private bool _announceCloud;
    [ObservableProperty] private string _keyStatusText = string.Empty;
    [ObservableProperty] private bool _saveHistory;
    [ObservableProperty] private bool _diagnosticLogging;
    [ObservableProperty] private Option<ScreenReaderNotificationMode> _selectedNotificationMode;
    [ObservableProperty] private bool _speakStatus;
    [ObservableProperty] private double _fontScale;
    /// <summary>Result of the last action (shown in a polite live region and read by screen readers).</summary>
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _microphoneTestResult = string.Empty;
    [ObservableProperty] private string _aiTestResult = string.Empty;
    [ObservableProperty] private string _ollamaInfo = string.Empty;
    [ObservableProperty] private string _downloadStatus = string.Empty;
    [ObservableProperty] private bool _isBusy;

    private bool SafeAutoStartEnabled()
    {
        try { return _services.AutoStart.IsEnabled(); } catch (Exception) { return false; }
    }

    private bool SafeAutoStartDisabledByUser()
    {
        try { return _services.AutoStart.IsDisabledByUser(); } catch (Exception) { return false; }
    }

    private static List<Option<string?>> DeviceOptions(Func<IReadOnlyList<AudioDeviceInfo>> list, string defaultText)
    {
        var result = new List<Option<string?>> { new(null, defaultText) };
        try
        {
            result.AddRange(list().Select(d => new Option<string?>(d.Id, d.Name)));
        }
        catch (Exception)
        {
            // Audio subsystem unavailable; only the default entry is offered.
        }

        return result;
    }

    private void UpdateKeyStatus()
    {
        bool exists;
        try { exists = _services.Secrets.Exists(SecretNames.BgGptApiKey); } catch (Exception) { exists = false; }
        KeyStatusText = exists ? _loc["settings.ai.keyPresent"] : _loc["settings.ai.keyMissing"];
    }

    /// <summary>Validates and builds the settings that will be saved.</summary>
    internal VedarSettings BuildSettings(out string? error)
    {
        error = null;
        foreach (var hk in new[] { PushToTalkHotkey, StopHotkey, MicToggleHotkey })
        {
            if (!Hotkey.TryParse(hk, out _))
            {
                error = _loc["settings.hotkey.invalid"] + " (" + hk + ")";
            }
        }

        if (new[] { PushToTalkHotkey, StopHotkey, MicToggleHotkey }.Distinct(StringComparer.OrdinalIgnoreCase).Count() < 3)
        {
            error = _loc["settings.hotkey.invalid"];
        }

        var s = JsonSettingsStore.Clone(_original);
        s.General.UserName = UserName;
        s.General.GreetOnStartup = GreetOnStartup;
        s.General.StartMinimizedToTray = StartMinimized;
        s.General.StartWithWindows = StartWithWindows;
        s.General.UiLanguage = SelectedLanguage.Value;
        s.Voice.Rate = (float)VoiceRate;
        s.Voice.Volume = (float)(VoiceVolume / 100.0);
        s.Voice.Earcons = Earcons;
        s.Voice.OutputDeviceId = SelectedOutputDevice.Value;
        s.Microphone.DeviceId = SelectedInputDevice.Value;
        s.Microphone.Sensitivity = (float)(Sensitivity / 100.0);
        s.Microphone.Gain = (float)Gain;
        s.Microphone.NoiseFilter = NoiseFilter;
        s.Microphone.Muted = MicrophoneMuted;
        s.Recognition.ModelFile = SelectedSttModel.Value;
        s.Activation.HandsFreeEnabled = HandsFree;
        s.Activation.PushToTalkHotkey = PushToTalkHotkey;
        s.Activation.StopHotkey = StopHotkey;
        s.Activation.MicrophoneToggleHotkey = MicToggleHotkey;
        s.Activation.FollowUpSeconds = FollowUpSeconds;
        s.Ai.Provider = SelectedAiProvider.Value;
        s.Ai.BgGptEndpoint = BgGptEndpoint;
        s.Ai.BgGptModel = BgGptModel;
        s.Ai.OllamaEndpoint = OllamaEndpoint;
        s.Ai.OllamaModel = OllamaModel ?? string.Empty;
        s.Ai.MaxResponseTokens = MaxTokens;
        s.Ai.AnnounceCloudUse = AnnounceCloud;
        s.Ai.SaveHistory = SaveHistory;
        s.Privacy.DiagnosticLogging = DiagnosticLogging;
        s.Accessibility.ScreenReaderNotifications = SelectedNotificationMode.Value;
        s.Accessibility.SpeakStatusChanges = SpeakStatus;
        s.Accessibility.FontScale = FontScale;
        return s.Normalize();
    }

    /// <summary>Saves everything. Returns false (with a spoken/announced message) if validation fails.</summary>
    public async Task<bool> SaveAsync()
    {
        var settings = BuildSettings(out var error);
        if (error is not null)
        {
            StatusMessage = error;
            return false;
        }

        var modelChanged = settings.Recognition.ModelFile != _original.Recognition.ModelFile;
        var voiceChanged = settings.Voice.VoiceId != _original.Voice.VoiceId;
        var languageChanged = settings.General.UiLanguage != _original.General.UiLanguage;
        if (!string.IsNullOrWhiteSpace(PendingApiKey))
        {
            try
            {
                _services.Secrets.Set(SecretNames.BgGptApiKey, PendingApiKey.Trim());
                PendingApiKey = null;
            }
            catch (Exception ex)
            {
                _services.LoggerFactory.CreateLogger("Settings").LogError(ex, "Could not store the API key.");
                StatusMessage = ex.Message;
                return false;
            }
        }

        _services.Settings.Save(settings);
        try
        {
            if (StartWithWindows && !_services.AutoStart.IsEnabled())
            {
                _services.AutoStart.Enable();
            }
            else if (!StartWithWindows && _services.AutoStart.IsEnabled())
            {
                _services.AutoStart.Disable();
            }
        }
        catch (Exception ex)
        {
            _services.LoggerFactory.CreateLogger("Settings").LogWarning(ex, "Autostart change failed.");
        }

        _services.FileLogger.MinimumLevel = settings.Privacy.DiagnosticLogging ? LogLevel.Debug : LogLevel.Information;
        var failed = _reRegisterHotkeys();
        if (modelChanged)
        {
            await _services.Recognizer.InitializeAsync(CancellationToken.None);
        }

        if (voiceChanged)
        {
            await _services.Synthesizer.InitializeAsync(CancellationToken.None);
        }

        StatusMessage = _loc["settings.saved"] + (failed.Count > 0 ? " " + string.Join(" ", failed.Select(h => _loc.Format("main.hotkeyFailed", h))) : string.Empty)
                        + (languageChanged ? " Рестартирайте Ведар, за да смените езика." : string.Empty);
        return failed.Count == 0;
    }

    [RelayCommand]
    private Task TestVoice()
    {
        // Applies the chosen rate/volume temporarily for the test.
        var s = _services.Settings.Current;
        var (rate, volume) = (s.Voice.Rate, s.Voice.Volume);
        s.Voice.Rate = (float)VoiceRate;
        _services.AudioOutput.Volume = (float)(VoiceVolume / 100.0);
        var text = _loc.Format("settings.voice.testPhrase", BulgarianDateTime.SpokenDate(DateTime.Now, includeYear: false));
        return _services.Orchestrator.SpeakTextAsync(text).ContinueWith(_ =>
        {
            s.Voice.Rate = rate;
            _services.AudioOutput.Volume = volume;
        }, TaskScheduler.Default);
    }

    /// <summary>Records three seconds (in memory only) and reports the level.</summary>
    [RelayCommand]
    private async Task TestMicrophone()
    {
        IsBusy = true;
        MicrophoneTestResult = _loc["settings.mic.testing"];
        float peak = 0;
        try
        {
            using var input = new WasapiAudioInput(_services.LoggerFactory.CreateLogger<WasapiAudioInput>());
            input.FrameAvailable += (_, e) =>
            {
                var samples = e.Samples.ToArray();
                AudioMath.ApplyGain(samples, (float)Gain);
                peak = Math.Max(peak, AudioMath.Peak(samples));
            };
            await input.StartAsync(SelectedInputDevice.Value, CancellationToken.None);
            await Task.Delay(3000);
            await input.StopAsync();
            var percent = (int)Math.Round(Math.Clamp((AudioMath.ToDecibels(peak) + 60) / 60, 0, 1) * 100);
            MicrophoneTestResult = peak < 0.003f ? _loc["settings.mic.testSilent"]
                : peak < 0.05f ? _loc.Format("settings.mic.testQuiet", percent)
                : _loc.Format("settings.mic.testOk", percent);
        }
        catch (Exception ex)
        {
            MicrophoneTestResult = _loc["speech.micNotFound"] + " (" + ex.Message + ")";
        }
        finally
        {
            IsBusy = false;
        }

        await _services.Orchestrator.SpeakTextAsync(MicrophoneTestResult);
    }

    [RelayCommand]
    private async Task TestAi()
    {
        IsBusy = true;
        AiTestResult = _loc["settings.ai.testing"];
        try
        {
            if (!string.IsNullOrWhiteSpace(PendingApiKey))
            {
                _services.Secrets.Set(SecretNames.BgGptApiKey, PendingApiKey.Trim());
                PendingApiKey = null;
                UpdateKeyStatus();
            }

            // Test with the values currently shown in the dialog.
            var live = _services.Settings.Current;
            var backup = JsonSettingsStore.Clone(live);
            live.Ai.Provider = SelectedAiProvider.Value;
            live.Ai.BgGptEndpoint = BgGptEndpoint;
            live.Ai.BgGptModel = BgGptModel;
            live.Ai.OllamaEndpoint = OllamaEndpoint;
            live.Ai.OllamaModel = OllamaModel ?? string.Empty;
            var status = await _services.Orchestrator.RefreshAiStatusAsync();
            live.Ai.Provider = backup.Ai.Provider;
            live.Ai.BgGptEndpoint = backup.Ai.BgGptEndpoint;
            live.Ai.BgGptModel = backup.Ai.BgGptModel;
            live.Ai.OllamaEndpoint = backup.Ai.OllamaEndpoint;
            live.Ai.OllamaModel = backup.Ai.OllamaModel;
            AiTestResult = status.Availability switch
            {
                AiAvailability.Available => _loc.Format("ai.status.available", status.ProviderName),
                AiAvailability.MissingApiKey => _loc.Format("ai.status.missingKey", status.ProviderName),
                AiAvailability.Offline => _loc.Format("speech.ai.offline", status.ProviderName),
                AiAvailability.Unauthorized => _loc.Format("speech.ai.unauthorized", status.ProviderName),
                AiAvailability.NotConfigured => _loc["ai.status.notConfigured"],
                _ => _loc.Format("ai.status.unavailable", status.ProviderName) + " " + status.Detail,
            };
        }
        catch (Exception ex)
        {
            AiTestResult = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshOllama()
    {
        var info = await _services.Ollama.GetInfoAsync(CancellationToken.None);
        OllamaModels.Clear();
        foreach (var m in info.Models)
        {
            OllamaModels.Add(m);
        }

        OllamaInfo = !info.Installed ? _loc["settings.ai.ollamaNotInstalled"]
            : !info.Running ? _loc["settings.ai.ollamaNotRunning"]
            : _loc.Format("settings.ai.ollamaRunning", info.Models.Count == 0 ? "0" : string.Join(", ", info.Models));
    }

    [RelayCommand]
    private void DeleteKey()
    {
        try
        {
            _services.Secrets.Delete(SecretNames.BgGptApiKey);
            StatusMessage = _loc["settings.ai.keyDeleted"];
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }

        UpdateKeyStatus();
    }

    [RelayCommand]
    private async Task DownloadModel()
    {
        var model = ModelCatalog.FindWhisperByFile(SelectedSttModel.Value);
        if (model is null)
        {
            return;
        }

        IsBusy = true;
        int lastSpoken = -25;
        var progress = new Progress<double>(p =>
        {
            var percent = (int)(p * 100);
            DownloadStatus = _loc.Format("settings.stt.downloading", percent);
            if (percent - lastSpoken >= 25)
            {
                lastSpoken = percent;
                StatusMessage = DownloadStatus;
            }
        });
        try
        {
            await _services.Downloader.DownloadAsync(model, _services.Paths.UserModelsDir, progress, CancellationToken.None);
            DownloadStatus = _loc["settings.stt.downloaded"];
        }
        catch (Exception ex)
        {
            DownloadStatus = _loc.Format("settings.stt.downloadFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            StatusMessage = DownloadStatus;
        }
    }

    [RelayCommand]
    private void DeleteHistory()
    {
        _services.History.DeleteAll();
        StatusMessage = _loc["settings.privacy.historyDeleted"];
    }

    /// <summary>Deletes all user data. The view asks for confirmation first.</summary>
    public IReadOnlyList<string> DeleteAllData() => _services.Eraser.EraseAll();

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            using var _ = Process.Start(new ProcessStartInfo(DataFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vedar.Accessibility;
using Vedar.Core.Localization;
using Vedar.Desktop.Hosting;
using Vedar.Infrastructure.Models;

namespace Vedar.Desktop.ViewModels;

public enum WizardStep
{
    Welcome,
    Name,
    Microphone,
    Voice,
    Models,
    AiKey,
    AiTest,
    AutoStart,
    Commands,
    Done,
}

/// <summary>
/// First-run wizard. Every step is keyboard accessible; VEDAR reads each step aloud unless a screen reader is
/// running (then the screen reader reads it, so the user does not hear it twice). The AI steps can be skipped.
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    private static readonly WizardStep[] Steps = Enum.GetValues<WizardStep>();
    private readonly AppServices _services;
    private readonly Localizer _loc;
    private readonly bool _speakSteps;

    public WizardViewModel(AppServices services, SettingsViewModel settings)
    {
        _services = services;
        _loc = services.Localizer;
        Settings = settings;
        Settings.StartWithWindows = true; // recommended default, the user can untick it
        _speakSteps = !ScreenReaderDetector.Detect().IsActive;
        UpdateTexts();
    }

    public SettingsViewModel Settings { get; }

    public event EventHandler? Finished;
    public event EventHandler? StepChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsName), nameof(IsMicrophone), nameof(IsVoice), nameof(IsModels), nameof(IsAiKey),
        nameof(IsAiTest), nameof(IsAutoStart), nameof(IsCommands), nameof(IsDone), nameof(CanGoBack), nameof(IsLast), nameof(CanSkip), nameof(StepNumberText))]
    private WizardStep _current = WizardStep.Welcome;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private string _modelsResult = string.Empty;

    public bool IsWelcome => Current == WizardStep.Welcome;
    public bool IsName => Current == WizardStep.Name;
    public bool IsMicrophone => Current == WizardStep.Microphone;
    public bool IsVoice => Current == WizardStep.Voice;
    public bool IsModels => Current == WizardStep.Models;
    public bool IsAiKey => Current == WizardStep.AiKey;
    public bool IsAiTest => Current == WizardStep.AiTest;
    public bool IsAutoStart => Current == WizardStep.AutoStart;
    public bool IsCommands => Current == WizardStep.Commands;
    public bool IsDone => Current == WizardStep.Done;
    public bool CanGoBack => Current != WizardStep.Welcome;
    public bool IsLast => Current == WizardStep.Done;
    public bool CanSkip => Current is WizardStep.AiKey or WizardStep.AiTest;
    public string StepNumberText => _loc.Format("wizard.stepOf", Array.IndexOf(Steps, Current) + 1, Steps.Length);

    private void UpdateTexts()
    {
        var key = Current switch
        {
            WizardStep.Welcome => "welcome",
            WizardStep.Name => "name",
            WizardStep.Microphone => "mic",
            WizardStep.Voice => "voice",
            WizardStep.Models => "models",
            WizardStep.AiKey => "ai",
            WizardStep.AiTest => "aiTest",
            WizardStep.AutoStart => "autostart",
            WizardStep.Commands => "activation",
            _ => "done",
        };
        Title = _loc[$"wizard.{key}.title"];
        var a = _services.Settings.Current.Activation;
        Text = Current == WizardStep.Commands
            ? _loc.Format("wizard.activation.text", a.PushToTalkHotkey, a.StopHotkey)
            : _loc[$"wizard.{key}.text"];
        if (Current == WizardStep.Models)
        {
            CheckModels();
        }
    }

    private void CheckModels()
    {
        var stt = _services.Paths.FindModel(Path.Combine("whisper", _services.Settings.Current.Recognition.ModelFile));
        var voiceId = _services.Settings.Current.Voice.VoiceId;
        var tts = _services.Paths.FindModel(Path.Combine("piper", voiceId, voiceId + ".onnx"));
        ModelsResult = stt is not null && tts is not null
            ? _loc.Format("wizard.models.ok", Path.GetFileName(stt), voiceId)
            : _loc.Format("wizard.models.missing", stt is null ? "Whisper" : "Piper " + voiceId);
        _ = ModelCatalog.Whisper.Count;
    }

    partial void OnCurrentChanged(WizardStep value)
    {
        UpdateTexts();
        StepChanged?.Invoke(this, EventArgs.Empty);
        SpeakStep();
    }

    public void SpeakStep()
    {
        if (_speakSteps && _services.Synthesizer.IsReady)
        {
            var extra = Current == WizardStep.Models ? " " + ModelsResult : string.Empty;
            _ = _services.Orchestrator.SpeakTextAsync(Title + ". " + Text + extra);
        }
    }

    [RelayCommand]
    private async Task Next()
    {
        if (Current == WizardStep.Name || Current == WizardStep.AiKey)
        {
            // Persist what was entered so far (name for greetings, key for the connection test).
            await Settings.SaveAsync();
        }

        if (IsLast)
        {
            await FinishAsync();
            return;
        }

        Current = Steps[Array.IndexOf(Steps, Current) + 1];
    }

    [RelayCommand]
    private void Back()
    {
        if (CanGoBack)
        {
            Current = Steps[Array.IndexOf(Steps, Current) - 1];
        }
    }

    /// <summary>Skips the optional AI configuration; offline commands keep working.</summary>
    [RelayCommand]
    private void Skip()
    {
        Settings.PendingApiKey = null;
        Current = WizardStep.AutoStart;
    }

    private async Task FinishAsync()
    {
        await Settings.SaveAsync();
        var s = _services.Settings.Current;
        s.General.FirstRunCompleted = true;
        _services.Settings.Save(s);
        Finished?.Invoke(this, EventArgs.Empty);
    }
}

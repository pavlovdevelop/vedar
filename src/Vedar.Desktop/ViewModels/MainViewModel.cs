using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vedar.Core.Abstractions;
using Vedar.Core.Localization;
using Vedar.Core.Orchestration;
using Vedar.Core.Settings;
using Vedar.Core.State;

namespace Vedar.Desktop.ViewModels;

public sealed record TranscriptItem(string Speaker, string Text, string Time)
{
    /// <summary>What a screen reader announces for this line.</summary>
    public string AccessibleText => $"{Speaker}: {Text}";
}

/// <summary>State and actions of the main window (home + conversation).</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AssistantOrchestrator _assistant;
    private readonly ISettingsStore _settings;
    private readonly Localizer _loc;
    private readonly Dispatcher _dispatcher;

    public MainViewModel(AssistantOrchestrator assistant, ISettingsStore settings, Localizer loc, Dispatcher dispatcher)
    {
        _assistant = assistant;
        _settings = settings;
        _loc = loc;
        _dispatcher = dispatcher;
        _statusText = loc["state.Starting"];
        _aiStatusText = loc["ai.status.checking"];
        _microphoneText = loc["mic.ok"];
        UpdateHotkeyTexts();
        UpdateMicrophone(assistant.MicrophoneStatus);

        assistant.StateMachine.StateChanged += (_, e) => OnUi(() => UpdateState(e.Current));
        assistant.MicrophoneStatusChanged += (_, s) => OnUi(() => UpdateMicrophone(s));
        assistant.AiStatusChanged += (_, s) => OnUi(() => UpdateAi(s));
        assistant.TranscriptAdded += (_, t) => OnUi(() => AddTranscript(t));
        assistant.ConfirmationChanged += (_, _) => OnUi(UpdateConfirmation);
        assistant.ConversationChanged += (_, active) => OnUi(() => InConversation = active);
        settings.SettingsChanged += (_, _) => OnUi(() =>
        {
            UpdateHotkeyTexts();
            UpdateMicrophone(assistant.MicrophoneStatus);
        });
    }

    public ObservableCollection<TranscriptItem> Transcript { get; } = [];

    /// <summary>Raised when the user asks for the settings window.</summary>
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    [ObservableProperty] private string _statusText;
    [ObservableProperty] private AssistantState _state = AssistantState.Starting;
    [ObservableProperty] private string _microphoneText;
    [ObservableProperty] private string _handsFreeText = string.Empty;
    [ObservableProperty] private string _aiStatusText;
    [ObservableProperty] private string _aiBadgeText = string.Empty;
    [ObservableProperty] private bool _isMicrophoneMuted;
    [ObservableProperty] private string _micToggleText = string.Empty;
    [ObservableProperty] private string _talkHelp = string.Empty;
    [ObservableProperty] private string _stopHelp = string.Empty;
    [ObservableProperty] private string _micHelp = string.Empty;
    [ObservableProperty] private string _hotkeyTalkText = string.Empty;
    [ObservableProperty] private string _hotkeyStopText = string.Empty;
    [ObservableProperty] private string _hotkeyMicText = string.Empty;
    [ObservableProperty] private string _hotkeyWarning = string.Empty;
    [ObservableProperty] private string _messageText = string.Empty;
    [ObservableProperty] private string? _confirmationQuestion;
    [ObservableProperty] private bool _hasConfirmation;
    [ObservableProperty] private bool _inConversation;
    [ObservableProperty] private bool _isListening;
    [ObservableProperty] private bool _isBusy;

    public string WindowTitle => _loc["app.slogan"];

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    internal void UpdateState(AssistantState state)
    {
        State = state;
        StatusText = state == AssistantState.Idle && _settings.Current.Activation.HandsFreeEnabled && !_settings.Current.Microphone.Muted
            ? _loc["state.IdleHandsFree"]
            : _loc["state." + state];
        IsListening = state == AssistantState.Listening;
        IsBusy = state is AssistantState.Recognizing or AssistantState.Processing or AssistantState.Speaking;
    }

    internal void UpdateMicrophone(MicrophoneStatus status)
    {
        IsMicrophoneMuted = status == MicrophoneStatus.Muted || _settings.Current.Microphone.Muted;
        MicrophoneText = status switch
        {
            MicrophoneStatus.Muted => _loc["mic.muted"],
            MicrophoneStatus.NotFound => _loc["mic.notFound"],
            MicrophoneStatus.Error => _loc["mic.error"],
            _ => _loc["mic.ok"],
        };
        HandsFreeText = _settings.Current.Activation.HandsFreeEnabled && !IsMicrophoneMuted ? _loc["mic.handsFree"] : _loc["mic.handsFreeOff"];
        MicToggleText = IsMicrophoneMuted ? _loc["main.micToggle.unmute"] : _loc["main.micToggle.mute"];
        UpdateState(State);
    }

    internal void UpdateAi(AiStatus status)
    {
        AiStatusText = status.Availability switch
        {
            AiAvailability.Available => _loc.Format("ai.status.available", status.ProviderName),
            AiAvailability.MissingApiKey => _loc.Format("ai.status.missingKey", status.ProviderName),
            AiAvailability.Offline => _loc.Format("ai.status.offline", status.ProviderName),
            AiAvailability.Unauthorized => _loc.Format("ai.status.unauthorized", status.ProviderName),
            AiAvailability.NotConfigured => _loc["ai.status.notConfigured"],
            _ => _loc.Format("ai.status.unavailable", status.ProviderName),
        };
        AiBadgeText = status.Availability == AiAvailability.NotConfigured ? string.Empty : status.IsCloud ? _loc["ai.cloudBadge"] : _loc["ai.localBadge"];
    }

    private void AddTranscript(TranscriptEntry entry)
    {
        var speaker = entry.Speaker switch
        {
            Speaker.User => _loc["main.speakerUser"],
            Speaker.Assistant => _loc["main.speakerAssistant"],
            _ => _loc["main.speakerSystem"],
        };
        Transcript.Add(new TranscriptItem(speaker, entry.Text, entry.Timestamp.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
        while (Transcript.Count > 200)
        {
            Transcript.RemoveAt(0);
        }
    }

    private void UpdateConfirmation()
    {
        ConfirmationQuestion = _assistant.PendingConfirmationQuestion;
        HasConfirmation = ConfirmationQuestion is not null;
    }

    internal void UpdateHotkeyTexts()
    {
        var a = _settings.Current.Activation;
        TalkHelp = _loc.Format("main.talk.help", a.PushToTalkHotkey);
        StopHelp = _loc.Format("main.stop.help", a.StopHotkey);
        MicHelp = _loc.Format("main.micToggle.help", a.MicrophoneToggleHotkey);
        HotkeyTalkText = _loc.Format("main.hotkeyTalk", a.PushToTalkHotkey);
        HotkeyStopText = _loc.Format("main.hotkeyStop", a.StopHotkey);
        HotkeyMicText = _loc.Format("main.hotkeyMic", a.MicrophoneToggleHotkey);
    }

    public void SetHotkeyFailures(IReadOnlyList<string> failed) =>
        HotkeyWarning = failed.Count == 0 ? string.Empty : string.Join(" ", failed.Select(h => _loc.Format("main.hotkeyFailed", h)));

    [RelayCommand]
    private void Talk() => _assistant.PushToTalk();

    [RelayCommand]
    private void Stop() => _assistant.Stop();

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private Task ToggleMicrophone() => _assistant.ToggleMicrophoneAsync();

    [RelayCommand]
    private Task Repeat() => _assistant.RepeatAsync();

    [RelayCommand]
    private void NewConversation()
    {
        _assistant.NewConversation();
        Transcript.Clear();
    }

    [RelayCommand]
    private Task EndConversation() => _assistant.EndConversationAsync();

    [RelayCommand]
    private async Task Send()
    {
        var text = MessageText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        MessageText = string.Empty;
        await _assistant.SubmitTextAsync(text);
    }

    [RelayCommand]
    private Task ConfirmYes() => _assistant.AnswerConfirmationAsync(true);

    [RelayCommand]
    private Task ConfirmNo() => _assistant.AnswerConfirmationAsync(false);

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);
}

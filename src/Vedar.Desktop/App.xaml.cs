using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Vedar.Accessibility;
using Vedar.Core.Abstractions;
using Vedar.Core.Localization;
using Vedar.Core.Orchestration;
using Vedar.Core.State;
using Vedar.Desktop.Hosting;
using Vedar.Desktop.Input;
using Vedar.Desktop.Themes;
using Vedar.Desktop.Tray;
using Vedar.Desktop.ViewModels;
using Vedar.Desktop.Views;
using Vedar.Infrastructure.Configuration;
using Vedar.Infrastructure.Startup;

namespace Vedar.Desktop;

/// <summary>
/// Application entry. Started by the user or automatically after Windows sign-in (with --background:
/// no window is shown and the focus is never taken from the current application).
/// </summary>
public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private AppServices? _services;
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private TrayIconService? _tray;
    private GlobalHotkeyService? _hotkeys;
    private ILogger? _logger;
    private bool _hiddenNoticeGiven;
    private bool _exiting;
    private SettingsWindow? _settingsWindow;

    public static bool StartedInBackground { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartedInBackground = e.Args.Contains(AutoStartManager.BackgroundArgument, StringComparer.OrdinalIgnoreCase);

        _singleInstance = SingleInstance.Acquire();
        var exitRequested = e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase);
        if (!_singleInstance.IsFirstInstance || exitRequested)
        {
            if (exitRequested)
            {
                _singleInstance.SignalExit();
            }
            else if (!StartedInBackground)
            {
                _singleInstance.SignalFirstInstance();
            }

            Shutdown(0);
            return;
        }

        try
        {
            var paths = new AppPaths();
            var uiHost = new DesktopUiHost(Dispatcher);
            _services = AppServices.Create(paths, uiHost);
            _logger = _services.LoggerFactory.CreateLogger("App");
            _logger.LogInformation("VEDAR {Version} starting (background: {Background}).", typeof(App).Assembly.GetName().Version, StartedInBackground);
            HookGlobalErrorHandlers();

            var settings = _services.Settings.Current;
            ThemeManager.Apply(this, settings.Accessibility.FontScale);
            ThemeManager.Watch(this, () => _services.Settings.Current.Accessibility.FontScale);

            var orchestrator = _services.Orchestrator;
            _mainViewModel = new MainViewModel(orchestrator, _services.Settings, _services.Localizer, Dispatcher);
            _mainViewModel.SettingsRequested += (_, _) => ShowSettings();
            _mainViewModel.ExitRequested += async (_, _) => await ExitAsync();
            _mainWindow = new MainWindow(_mainViewModel);
            _mainWindow.HiddenToTray += (_, _) => OnHiddenToTray();
            MainWindow = _mainWindow;

            uiHost.ShowMainWindowAction = () => _mainWindow.BringToFront();
            uiHost.ShowSettingsAction = ShowSettings;
            uiHost.ShowConversationAction = () => { _mainWindow.BringToFront(); _mainWindow.ShowConversation(); };
            uiHost.OwnerWindow = () => _mainWindow;
            uiHost.ExitAction = ExitAfterSpeechAsync;
            uiHost.Settings = () => _services.Settings.Current;
            uiHost.Announcer = new AccessibleAnnouncer(_mainWindow, _mainWindow.LiveRegion);
            orchestrator.Notification += (_, n) => uiHost.Notify(n.Kind, n.Text, n.SpokenByVedar);
            orchestrator.StateMachine.StateChanged += (_, s) => Dispatcher.BeginInvoke(() => UpdateTray(s.Current));
            orchestrator.MicrophoneStatusChanged += (_, m) => Dispatcher.BeginInvoke(() => _tray?.SetMicrophoneMuted(m == MicrophoneStatus.Muted, _services.Localizer));

            _tray = new TrayIconService(_services.Localizer,
                show: () => Dispatcher.BeginInvoke(() => _mainWindow.BringToFront()),
                talk: () => Dispatcher.BeginInvoke(orchestrator.PushToTalk),
                stop: () => Dispatcher.BeginInvoke(orchestrator.Stop),
                toggleMic: () => Dispatcher.BeginInvoke(async () => await orchestrator.ToggleMicrophoneAsync()),
                settings: () => Dispatcher.BeginInvoke(ShowSettings),
                exit: () => Dispatcher.BeginInvoke(async () => await ExitAsync()));
            _tray.SetMicrophoneMuted(settings.Microphone.Muted, _services.Localizer);

            _hotkeys = new GlobalHotkeyService(_services.LoggerFactory.CreateLogger("Hotkeys"));
            _hotkeys.Pressed += (_, action) => OnHotkey(action);
            _mainViewModel.SetHotkeyFailures(RegisterHotkeys());
            _singleInstance.ListenForShowRequests(
                () => Dispatcher.BeginInvoke(() => _mainWindow.BringToFront()),
                () => Dispatcher.BeginInvoke(async () => await ExitAsync()));
            SessionEnding += (_, _) => { if (_mainWindow is not null) { _mainWindow.AllowClose = true; } };

            var showWindow = !StartedInBackground && !(settings.General.StartMinimizedToTray && settings.General.FirstRunCompleted && e.Args.Contains("--minimized"));
            if (showWindow)
            {
                _mainWindow.Show();
            }

            await orchestrator.StartAsync();
            _mainViewModel.UpdateState(orchestrator.State);

            if (!_services.Settings.Current.General.FirstRunCompleted && !StartedInBackground)
            {
                ShowWizard();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Startup failed.");
            MessageBox.Show(Localizer.Current.Format("error.startup", ex.Message), Localizer.Current["error.title"], MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private IReadOnlyList<string> RegisterHotkeys()
    {
        var failed = new List<string>();
        if (_hotkeys is null || _services is null)
        {
            return failed;
        }

        var a = _services.Settings.Current.Activation;
        foreach (var (action, text) in new[] { (HotkeyAction.PushToTalk, a.PushToTalkHotkey), (HotkeyAction.Stop, a.StopHotkey), (HotkeyAction.ToggleMicrophone, a.MicrophoneToggleHotkey) })
        {
            if (!Hotkey.TryParse(text, out var hotkey) || !_hotkeys.Register(action, hotkey))
            {
                failed.Add(text);
            }
        }

        return failed;
    }

    private void OnHotkey(HotkeyAction action)
    {
        var o = _services?.Orchestrator;
        if (o is null)
        {
            return;
        }

        switch (action)
        {
            case HotkeyAction.PushToTalk:
                o.PushToTalk();
                break;
            case HotkeyAction.Stop:
                o.Stop();
                break;
            case HotkeyAction.ToggleMicrophone:
                _ = o.ToggleMicrophoneAsync();
                break;
        }
    }

    private void UpdateTray(AssistantState state)
    {
        if (_tray is null || _mainViewModel is null)
        {
            return;
        }

        _tray.SetTooltip(Localizer.Current.Format("tray.tooltip", _mainViewModel.StatusText));
    }

    private void OnHiddenToTray()
    {
        if (_hiddenNoticeGiven || _services is null)
        {
            return;
        }

        _hiddenNoticeGiven = true;
        _ = _services.Orchestrator.SpeakTextAsync("Ведар продължава да работи във фонов режим. Иконата е в областта за известия.");
    }

    private void ShowSettings()
    {
        if (_services is null || _mainWindow is null)
        {
            return;
        }

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var vm = new SettingsViewModel(_services, () =>
        {
            var failed = RegisterHotkeys();
            _mainViewModel?.SetHotkeyFailures(failed);
            return failed;
        });
        _settingsWindow = new SettingsWindow(vm);
        if (_mainWindow.IsVisible)
        {
            _settingsWindow.Owner = _mainWindow;
        }

        _settingsWindow.AllDataDeleted += async (_, _) => await ExitAsync();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ShowWizard()
    {
        if (_services is null)
        {
            return;
        }

        var settingsVm = new SettingsViewModel(_services, () =>
        {
            var failed = RegisterHotkeys();
            _mainViewModel?.SetHotkeyFailures(failed);
            return failed;
        });
        var wizard = new FirstRunWizard(new WizardViewModel(_services, settingsVm));
        if (_mainWindow?.IsVisible == true)
        {
            wizard.Owner = _mainWindow;
        }

        wizard.Show();
        wizard.Activate();
    }

    /// <summary>Exit requested by voice: let VEDAR finish saying goodbye first.</summary>
    private async Task ExitAfterSpeechAsync()
    {
        var o = _services?.Orchestrator;
        if (o is not null)
        {
            await Task.Delay(300);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (o.IsTurnActive && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }

        await ExitAsync();
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _logger?.LogInformation("VEDAR exiting.");
        _hotkeys?.Dispose();
        _tray?.Dispose();
        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            _mainWindow.Close();
        }

        if (_services is not null)
        {
            try
            {
                await _services.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error during shutdown.");
            }
        }

        _singleInstance?.Dispose();
        Shutdown(0);
    }

    private void HookGlobalErrorHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            _logger?.LogError(e.Exception, "Unhandled UI exception.");
            e.Handled = true; // keep VEDAR running; the assistant recovers on the next turn
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger?.LogError(e.Exception, "Unobserved task exception.");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => _logger?.LogCritical(e.ExceptionObject as Exception, "Fatal exception.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

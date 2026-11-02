using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Vedar.Accessibility;
using Vedar.Commands.Files;
using Vedar.Core.Abstractions;
using Vedar.Core.Accessibility;
using Vedar.Core.Settings;

namespace Vedar.Desktop.Hosting;

/// <summary>Implements the assistant's UI requests on the WPF dispatcher thread.</summary>
public sealed class DesktopUiHost : IUserInterfaceHost
{
    private readonly Dispatcher _dispatcher;

    public DesktopUiHost(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public Action? ShowMainWindowAction { get; set; }
    public Action? ShowSettingsAction { get; set; }
    public Action? ShowConversationAction { get; set; }
    public Func<Window?>? OwnerWindow { get; set; }
    public Func<Task>? ExitAction { get; set; }
    public AccessibleAnnouncer? Announcer { get; set; }
    public Func<VedarSettings>? Settings { get; set; }
    public Func<ScreenReaderInfo> ScreenReader { get; set; } = ScreenReaderDetector.Detect;

    public void ShowMainWindow() => _dispatcher.BeginInvoke(() => ShowMainWindowAction?.Invoke());

    public void ShowSettings() => _dispatcher.BeginInvoke(() => ShowSettingsAction?.Invoke());

    public void ShowConversation() => _dispatcher.BeginInvoke(() => ShowConversationAction?.Invoke());

    /// <summary>Standard Windows file dialog (fully accessible with Narrator/NVDA). The user explicitly picks the file.</summary>
    public Task<string?> PickTextFileAsync(CancellationToken cancellationToken) =>
        _dispatcher.InvokeAsync<string?>(() =>
        {
            ShowMainWindowAction?.Invoke();
            var exts = string.Join(";", TextFileReader.SupportedExtensions.Select(e => "*" + e));
            var dialog = new OpenFileDialog
            {
                Title = "Избери файл за четене",
                Filter = $"Текстови файлове и документи ({exts})|{exts}|Всички файлове (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            var owner = OwnerWindow?.Invoke();
            var ok = owner is not null && owner.IsVisible ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            return ok == true ? dialog.FileName : null;
        }).Task;

    public Task<string?> GetClipboardTextAsync() =>
        _dispatcher.InvokeAsync<string?>(() =>
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return null; // clipboard locked by another program
            }
        }).Task;

    public Task ClearClipboardAsync() =>
        _dispatcher.InvokeAsync(() =>
        {
            try
            {
                Clipboard.Clear();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Clipboard locked by another program; the key stays there.
            }
        }).Task;

    public void RequestExit() => _dispatcher.BeginInvoke(async () =>
    {
        if (ExitAction is not null)
        {
            await ExitAction();
        }
    });

    public void Notify(UiNotificationKind kind, string text) => Notify(kind, text, spokenByVedar: false);

    public void Notify(UiNotificationKind kind, string text, bool spokenByVedar)
    {
        var settings = Settings?.Invoke();
        var mode = settings?.Accessibility.ScreenReaderNotifications ?? ScreenReaderNotificationMode.Auto;
        var reader = ScreenReader();
        if (!ScreenReaderNotificationPolicy.ShouldNotify(mode, kind, spokenByVedar, reader.IsActive))
        {
            return;
        }

        _dispatcher.BeginInvoke(() => Announcer?.Announce(text, kind is UiNotificationKind.Error or UiNotificationKind.Important));
    }
}

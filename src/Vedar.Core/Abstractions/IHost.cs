namespace Vedar.Core.Abstractions;

public enum UiNotificationKind { Status, Response, Error, Important }

/// <summary>Services the desktop shell provides to the assistant (implemented by Vedar.Desktop).</summary>
/// <summary>
/// When the user last pressed a key or moved the mouse anywhere in Windows. While VEDAR listens without the wake
/// word, typing means the user is busy with something else — the screen reader may be talking — so only
/// "Ведар, …" counts then.
/// </summary>
public interface IUserActivityMonitor
{
    /// <summary>Time since the last keyboard or mouse input; null when unknown.</summary>
    TimeSpan? TimeSinceLastInput { get; }
}

public interface IUserInterfaceHost
{
    void ShowMainWindow();
    void ShowSettings();
    void ShowConversation();
    /// <summary>Shows an accessible file picker. Returns null when cancelled.</summary>
    Task<string?> PickTextFileAsync(CancellationToken cancellationToken);
    Task<string?> GetClipboardTextAsync();
    /// <summary>Empties the clipboard (after an API key was taken from it).</summary>
    Task ClearClipboardAsync();
    void RequestExit();
    /// <summary>Sends text to the screen reader according to the notification policy.</summary>
    void Notify(UiNotificationKind kind, string text);
}

/// <summary>Safe, allow-listed operating system actions. No arbitrary process execution.</summary>
public interface ISystemActions
{
    bool OpenDefaultBrowser();
    bool OpenUrl(Uri url);
    bool OpenNotepad();
    bool OpenDocumentsFolder();
    /// <summary>Changes the Windows master volume by delta (−1..1). Returns the new level 0..1 or null if unsupported.</summary>
    float? ChangeSystemVolume(float delta);
    float? GetSystemVolume();
    float? SetSystemVolume(float level);
}

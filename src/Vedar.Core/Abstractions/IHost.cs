namespace Vedar.Core.Abstractions;

public enum UiNotificationKind { Status, Response, Error, Important }

/// <summary>Services the desktop shell provides to the assistant (implemented by Vedar.Desktop).</summary>
public interface IUserInterfaceHost
{
    void ShowMainWindow();
    void ShowSettings();
    void ShowConversation();
    /// <summary>Shows an accessible file picker. Returns null when cancelled.</summary>
    Task<string?> PickTextFileAsync(CancellationToken cancellationToken);
    Task<string?> GetClipboardTextAsync();
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

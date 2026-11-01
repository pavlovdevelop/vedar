using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Vedar.Audio.Windows;
using Vedar.Core.Abstractions;

namespace Vedar.Commands.Actions;

/// <summary>
/// The complete list of operating system actions VEDAR can perform. There is deliberately no way to run an
/// arbitrary program, command line, PowerShell or CMD — neither from voice commands nor from AI output.
/// </summary>
public sealed class WindowsSystemActions : ISystemActions
{
    public const string DefaultHomePage = "https://www.google.com";
    private readonly ISystemVolume? _volume;
    private readonly ILogger<WindowsSystemActions> _logger;
    private readonly Func<ProcessStartInfo, bool> _start;

    public WindowsSystemActions(ISystemVolume? volume, ILogger<WindowsSystemActions> logger, Func<ProcessStartInfo, bool>? start = null)
    {
        _volume = volume;
        _logger = logger;
        _start = start ?? StartProcess;
    }

    private bool StartProcess(ProcessStartInfo info)
    {
        try
        {
            using var p = Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start {File}.", info.FileName);
            return false;
        }
    }

    /// <summary>Opens the default browser by asking Windows to open the home page URL.</summary>
    public bool OpenDefaultBrowser() => OpenUrl(new Uri(DefaultHomePage));

    public bool OpenUrl(Uri url)
    {
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            _logger.LogWarning("Refused to open non-web URL.");
            return false;
        }

        return _start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }

    public bool OpenNotepad()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var notepad = string.IsNullOrEmpty(system) ? "notepad.exe" : Path.Combine(system, "notepad.exe");
        return _start(new ProcessStartInfo(File.Exists(notepad) ? notepad : "notepad.exe") { UseShellExecute = true });
    }

    public bool OpenDocumentsFolder()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(docs) || !Directory.Exists(docs))
        {
            return false;
        }

        // Opening a folder path with the shell shows it in File Explorer; nothing is executed.
        return _start(new ProcessStartInfo(docs) { UseShellExecute = true });
    }

    public float? GetSystemVolume() => _volume?.Get();

    public float? SetSystemVolume(float level) => _volume?.Set(Math.Clamp(level, 0f, 1f));

    public float? ChangeSystemVolume(float delta)
    {
        var current = _volume?.Get();
        return current is null ? null : _volume!.Set(Math.Clamp(current.Value + delta, 0f, 1f));
    }
}

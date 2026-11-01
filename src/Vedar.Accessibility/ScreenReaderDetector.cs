using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Vedar.Accessibility;

public sealed record ScreenReaderInfo(bool IsActive, string? Name);

/// <summary>
/// Detects a running screen reader. VEDAR only reads this information; it never changes screen reader settings.
/// </summary>
public static partial class ScreenReaderDetector
{
    private const uint SPI_GETSCREENREADER = 0x0046;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    private static readonly (string Process, string Name)[] Known =
    [
        ("nvda", "NVDA"),
        ("Narrator", "Разказвач (Narrator)"),
        ("jfw", "JAWS"),
        ("ZoomText", "ZoomText"),
        ("Fusion", "Fusion"),
    ];

    public static ScreenReaderInfo Detect()
    {
        string? name = null;
        foreach (var (process, display) in Known)
        {
            try
            {
                var processes = Process.GetProcessesByName(process);
                var found = processes.Length > 0;
                foreach (var p in processes)
                {
                    p.Dispose();
                }

                if (found)
                {
                    name = display;
                    break;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        var flag = 0;
        try
        {
            SystemParametersInfo(SPI_GETSCREENREADER, 0, ref flag, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return new ScreenReaderInfo(name is not null || flag != 0, name ?? (flag != 0 ? "екранен четец" : null));
    }
}

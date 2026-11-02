using System.Runtime.InteropServices;
using Vedar.Core.Abstractions;

namespace Vedar.Desktop.Input;

/// <summary>
/// Reads when the user last pressed a key or moved the mouse anywhere in Windows (GetLastInputInfo). It is only a
/// time stamp: no key, text or position is read, and nothing is recorded.
/// </summary>
public sealed partial class UserActivityMonitor : IUserActivityMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetLastInputInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    public TimeSpan? TimeSinceLastInput
    {
        get
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info))
            {
                return null;
            }

            // Both tick counts wrap after 49.7 days; the unsigned difference is still right.
            var now = unchecked((uint)Environment.TickCount);
            return TimeSpan.FromMilliseconds(unchecked(now - info.Time));
        }
    }
}

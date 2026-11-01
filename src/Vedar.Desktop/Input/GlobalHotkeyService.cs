using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace Vedar.Desktop.Input;

public enum HotkeyAction { PushToTalk = 1, Stop = 2, ToggleMicrophone = 3 }

/// <summary>
/// System-wide hotkeys via RegisterHotKey on a message-only window. They work while another application has
/// the focus, so VEDAR never needs to take the focus away from the user's current program.
/// </summary>
public sealed partial class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    private readonly HwndSource _source;
    private readonly ILogger _logger;
    private readonly Dictionary<HotkeyAction, Hotkey> _registered = [];

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    public GlobalHotkeyService(ILogger logger)
    {
        _logger = logger;
        var parameters = new HwndSourceParameters("VEDAR.Hotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public event EventHandler<HotkeyAction>? Pressed;

    public IReadOnlyDictionary<HotkeyAction, Hotkey> Registered => _registered;

    /// <summary>Registers (or re-registers) a hotkey. Returns false if another program already uses it.</summary>
    public bool Register(HotkeyAction action, Hotkey hotkey)
    {
        Unregister(action);
        uint mods = MOD_NOREPEAT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Alt)) { mods |= MOD_ALT; }
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Control)) { mods |= MOD_CONTROL; }
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Shift)) { mods |= MOD_SHIFT; }
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Windows)) { mods |= MOD_WIN; }
        var vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);
        if (!RegisterHotKey(_source.Handle, (int)action, mods, vk))
        {
            _logger.LogWarning("Hotkey {Hotkey} for {Action} could not be registered (error {Error}).", hotkey, action, Marshal.GetLastWin32Error());
            return false;
        }

        _registered[action] = hotkey;
        _logger.LogInformation("Hotkey {Hotkey} registered for {Action}.", hotkey, action);
        return true;
    }

    public void Unregister(HotkeyAction action)
    {
        if (_registered.Remove(action))
        {
            UnregisterHotKey(_source.Handle, (int)action);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && Enum.IsDefined(typeof(HotkeyAction), wParam.ToInt32()))
        {
            handled = true;
            Pressed?.Invoke(this, (HotkeyAction)wParam.ToInt32());
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var action in _registered.Keys.ToList())
        {
            Unregister(action);
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}

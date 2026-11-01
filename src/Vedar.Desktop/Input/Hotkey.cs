using System.Globalization;
using System.Windows.Input;

namespace Vedar.Desktop.Input;

/// <summary>A global key combination such as "Ctrl+Shift+Space".</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "win" or "windows":
                    modifiers |= ModifierKeys.Windows;
                    break;
                case "backspace":
                    key = Key.Back;
                    break;
                case "esc":
                    key = Key.Escape;
                    break;
                default:
                    if (Enum.TryParse<Key>(raw, ignoreCase: true, out var k))
                    {
                        key = k;
                    }
                    else if (raw.Length == 1 && char.IsAsciiDigit(raw[0]))
                    {
                        key = Key.D0 + (raw[0] - '0');
                    }
                    else
                    {
                        return false;
                    }

                    break;
            }
        }

        if (key is null || IsModifierKey(key.Value))
        {
            return false;
        }

        hotkey = new Hotkey(modifiers, key.Value);
        return hotkey.IsValidGlobal;
    }

    /// <summary>Must contain Ctrl, Alt or Win so it does not steal normal typing.</summary>
    public bool IsValidGlobal => (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0 && !IsModifierKey(Key);

    public static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin or Key.System or Key.None;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(Key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(Key - Key.D0)).ToString(CultureInfo.InvariantCulture),
            _ => Key.ToString(),
        });
        return string.Join('+', parts);
    }
}

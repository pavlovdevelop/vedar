using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Vedar.Core.Localization;
using Vedar.Desktop.Input;

namespace Vedar.Desktop.Controls;

/// <summary>
/// Text box that records a key combination when focused. Tab and Shift+Tab still move the focus (keyboard
/// navigation is never trapped); Escape restores the previous value. The captured combination is announced.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((HotkeyBox)d).Text = (string)e.NewValue));

    private string _before = string.Empty;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = true;
        GotKeyboardFocus += (_, _) => _before = Hotkey;
    }

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (key == Key.Tab && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            base.OnPreviewKeyDown(e); // let Tab navigate
            return;
        }

        e.Handled = true;
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            Hotkey = _before;
            return;
        }

        if (Input.Hotkey.IsModifierKey(key))
        {
            return;
        }

        var candidate = new Hotkey(modifiers, key);
        if (!candidate.IsValidGlobal)
        {
            AutomationProperties.SetHelpText(this, Localizer.Current["settings.hotkey.invalid"]);
            return;
        }

        Hotkey = candidate.ToString();
        AutomationProperties.SetHelpText(this, Localizer.Current.Format("settings.hotkey.captured", Hotkey));
    }
}

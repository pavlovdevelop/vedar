using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Vedar.Accessibility;

/// <summary>
/// Exposes access keys to UI Automation as "Alt+X". WPF reports only the letter ("Г"), while the UI Automation
/// specification describes the full key combination ("Alt+Г") — the form Narrator and NVDA announce as a shortcut.
/// </summary>
/// <remarks>
/// Covers buttons, check boxes, radio buttons and tab items with an access key in their text, and text fields
/// reached through a <see cref="Label"/> with an access key. Texts that change at run time (for example
/// "Напред" → "Готово" in the wizard) are followed until the window closes.
/// </remarks>
public static class AccessKeyAnnotator
{
    private static readonly DependencyPropertyDescriptor ContentDescriptor =
        DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl));

    /// <summary>Annotates the window's controls once it is loaded and keeps them up to date while it is open.</summary>
    public static void Attach(Window window)
    {
        var tracked = new List<ContentControl>();
        EventHandler onContentChanged = (sender, _) =>
        {
            if (sender is Control c)
            {
                Annotate(c);
            }
        };

        window.Loaded += (_, _) =>
        {
            if (tracked.Count > 0)
            {
                return;
            }

            foreach (var c in AccessibilityAuditor.Descendants(window).OfType<Control>())
            {
                if (Annotate(c) && c is ContentControl cc)
                {
                    ContentDescriptor.AddValueChanged(cc, onContentChanged);
                    tracked.Add(cc);
                }
            }
        };

        window.Closed += (_, _) =>
        {
            foreach (var cc in tracked)
            {
                ContentDescriptor.RemoveValueChanged(cc, onContentChanged);
            }

            tracked.Clear();
        };
    }

    /// <summary>The UI Automation form of an access key, e.g. "Alt+Г".</summary>
    public static string Format(char key) => "Alt+" + char.ToUpper(key, CultureInfo.InvariantCulture);

    /// <summary>Sets the access key of one control. Returns true when the control carries (or labels) an access key.</summary>
    public static bool Annotate(Control c)
    {
        if (c is Label label)
        {
            if (label.Target is UIElement target && AccessibilityAuditor.AccessKey(label) is char labelKey)
            {
                AutomationProperties.SetAccessKey(target, Format(labelKey));
                return true;
            }

            return false;
        }

        if (!AccessibilityAuditor.IsInteractive(c))
        {
            return false;
        }

        if (AccessibilityAuditor.AccessKey(c) is char key)
        {
            AutomationProperties.SetAccessKey(c, Format(key));
            return true;
        }

        if (c is ContentControl && c.GetBindingExpression(ContentControl.ContentProperty) is not null)
        {
            // Bound text that currently has no access key may get one later; keep following it.
            c.ClearValue(AutomationProperties.AccessKeyProperty);
            return true;
        }

        return false;
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Vedar.Accessibility;

public sealed record AccessibilityIssue(string Element, string Problem);

/// <summary>
/// Static checks used by the automated tests: every interactive control must have an accessible name,
/// be reachable with Tab, and access keys must be unique within a window.
/// </summary>
public static class AccessibilityAuditor
{
    public static IReadOnlyList<Control> InteractiveControls(DependencyObject root) =>
        Descendants(root).OfType<Control>().Where(IsInteractive).ToList();

    public static bool IsInteractive(Control c) =>
        c is ButtonBase or TextBox or PasswordBox or ComboBox or Slider or ListBox or TabItem
        && c is not ScrollBar and not ComboBoxItem and not ListBoxItem
        && c.TemplatedParent is null;

    public static IReadOnlyList<AccessibilityIssue> Audit(DependencyObject root)
    {
        var issues = new List<AccessibilityIssue>();
        var accessKeys = new Dictionary<char, string>();
        foreach (var c in InteractiveControls(root))
        {
            var id = Describe(c);
            if (string.IsNullOrWhiteSpace(AccessibleName(c)))
            {
                issues.Add(new AccessibilityIssue(id, "missing accessible name"));
            }

            if (!c.Focusable || (!c.IsTabStop && c is not TabItem))
            {
                issues.Add(new AccessibilityIssue(id, "not reachable with the keyboard"));
            }

            if (AccessKey(c) is char key && c is not TabItem)
            {
                if (accessKeys.TryGetValue(key, out var other) && IsSameScope(c, other))
                {
                    issues.Add(new AccessibilityIssue(id, $"duplicate access key '{key}' (also {other})"));
                }

                accessKeys.TryAdd(key, id);
            }
        }

        return issues;
    }

    private static bool IsSameScope(Control c, string other) => true;

    /// <summary>The name a screen reader announces: AutomationProperties.Name, label target, or text content.</summary>
    public static string? AccessibleName(Control c)
    {
        var name = AutomationProperties.GetName(c);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        if (AutomationProperties.GetLabeledBy(c) is FrameworkElement label)
        {
            return label is Label l ? l.Content?.ToString() : (label as TextBlock)?.Text;
        }

        return c switch
        {
            ContentControl { Content: string s } => s.Replace("_", string.Empty, StringComparison.Ordinal),
            HeaderedItemsControl { Header: string h } => h,
            TabItem { Header: string th } => th,
            _ => null,
        };
    }

    public static char? AccessKey(Control c)
    {
        var text = c switch
        {
            ContentControl { Content: string s } => s,
            TabItem { Header: string h } => h,
            _ => null,
        };
        var i = text?.IndexOf('_') ?? -1;
        return i >= 0 && i + 1 < text!.Length ? char.ToLowerInvariant(text[i + 1]) : null;
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        var seen = new HashSet<DependencyObject>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            yield return current;
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
            {
                queue.Enqueue(child);
            }

            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                {
                    queue.Enqueue(VisualTreeHelper.GetChild(current, i));
                }
            }
        }
    }

    private static string Describe(Control c) =>
        $"{c.GetType().Name}{(string.IsNullOrEmpty(c.Name) ? string.Empty : "#" + c.Name)}";
}

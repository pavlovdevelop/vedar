using System.Windows.Markup;
using Vedar.Core.Localization;

namespace Vedar.Desktop.Localization;

/// <summary>XAML markup extension: <c>Content="{l:Loc main.talk}"</c>. Texts come from the JSON string tables.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Removes the access-key underscore (for AutomationProperties.Name and plain text).</summary>
    public bool Plain { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var text = Localizer.Current[Key];
        return Plain ? text.Replace("_", string.Empty, StringComparison.Ordinal) : text;
    }
}

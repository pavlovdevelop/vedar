using System.Globalization;
using System.Windows.Data;

namespace Vedar.Desktop.Controls;

/// <summary>Removes the access-key underscore so screen readers do not read it.</summary>
public sealed class StripAccessKeyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string)?.Replace("_", string.Empty, StringComparison.Ordinal) ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
}

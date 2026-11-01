using System.Windows;

namespace Vedar.Desktop.Themes;

/// <summary>Switches between the VEDAR dark blue theme and Windows High Contrast colors, and applies the font scale.</summary>
public static class ThemeManager
{
    private static readonly Uri DarkBlue = new("pack://application:,,,/VEDAR;component/Themes/DarkBlue.xaml", UriKind.Absolute);
    private static readonly Uri HighContrast = new("pack://application:,,,/VEDAR;component/Themes/HighContrast.xaml", UriKind.Absolute);
    private static ResourceDictionary? _current;

    public static bool IsHighContrast => SystemParameters.HighContrast;

    public static void Apply(Application app, double fontScale)
    {
        var dict = new ResourceDictionary { Source = IsHighContrast ? HighContrast : DarkBlue };
        if (_current is not null)
        {
            app.Resources.MergedDictionaries.Remove(_current);
        }

        app.Resources.MergedDictionaries.Insert(0, dict);
        _current = dict;
        app.Resources["Vedar.FontSize"] = 18.0 * fontScale;
        app.Resources["Vedar.FontSizeLarge"] = 26.0 * fontScale;
        app.Resources["Vedar.FontSizeHuge"] = 34.0 * fontScale;
    }

    /// <summary>Re-applies the theme when the user turns High Contrast on or off.</summary>
    public static void Watch(Application app, Func<double> fontScale)
    {
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
            {
                app.Dispatcher.BeginInvoke(() => Apply(app, fontScale()));
            }
        };
    }
}

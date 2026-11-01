using System.Windows;
using Vedar.Accessibility;
using Vedar.Core.Localization;
using Vedar.Desktop.ViewModels;

namespace Vedar.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        AccessKeyAnnotator.Attach(this);
    }

    public SettingsWindow(SettingsViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += (_, saved) =>
        {
            DialogResult = saved;
            Close();
        };
        Loaded += (_, _) => UserNameBox.Focus();
    }

    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext;

    /// <summary>Raised after "Изтрий всички мои данни" completed; the app then exits.</summary>
    public event EventHandler? AllDataDeleted;

    private void OnApiKeyChanged(object sender, RoutedEventArgs e) => ViewModel.PendingApiKey = ApiKeyBox.Password;

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        ViewModel.PendingApiKey = string.IsNullOrWhiteSpace(ApiKeyBox.Password) ? null : ApiKeyBox.Password;
        var ok = await ViewModel.SaveAsync();
        ApiKeyBox.Clear();
        if (ok)
        {
            DialogResult = true;
            Close();
        }
    }

    private void OnDeleteAll(object sender, RoutedEventArgs e)
    {
        var loc = Localizer.Current;
        var answer = MessageBox.Show(this, loc["settings.privacy.deleteAll.confirm"], loc["settings.privacy.deleteAll"].Replace("_", string.Empty),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var failures = ViewModel.DeleteAllData();
        ViewModel.StatusMessage = loc["settings.privacy.deletedAll"] + (failures.Count > 0 ? " (" + string.Join(", ", failures) + ")" : string.Empty);
        AllDataDeleted?.Invoke(this, EventArgs.Empty);
    }
}

using System.Windows;
using System.Windows.Threading;
using Vedar.Accessibility;
using Vedar.Desktop.ViewModels;

namespace Vedar.Desktop.Views;

public partial class FirstRunWizard : Window
{
    public FirstRunWizard()
    {
        InitializeComponent();
        AccessKeyAnnotator.Attach(this);
    }

    public FirstRunWizard(WizardViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.StepChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => StepText.Focus());
        viewModel.Finished += (_, _) =>
        {
            Completed = true;
            Close();
        };
        Loaded += (_, _) =>
        {
            StepText.Focus();
            viewModel.SpeakStep();
        };
    }

    public bool Completed { get; private set; }

    private void OnKeyChanged(object sender, RoutedEventArgs e) =>
        ((WizardViewModel)DataContext).Settings.PendingApiKey = string.IsNullOrWhiteSpace(WizardKeyBox.Password) ? null : WizardKeyBox.Password;
}

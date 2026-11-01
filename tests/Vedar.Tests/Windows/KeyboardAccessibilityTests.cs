using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Vedar.Accessibility;
using Vedar.Core.Localization;
using Vedar.Desktop.Hosting;
using Vedar.Desktop.ViewModels;
using Vedar.Desktop.Views;
using Vedar.Infrastructure.Configuration;
using Vedar.Tests.Core;

namespace Vedar.Tests.Windows;

/// <summary>
/// Loads the real WPF windows and checks keyboard accessibility: every interactive control has an accessible
/// name, is reachable with Tab, access keys are unique, and Tab cycles through all main actions.
/// </summary>
public class KeyboardAccessibilityTests
{
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var app = new Vedar.Desktop.App();
                    app.InitializeComponent();
                }

                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new Exception("UI test failed: " + error.Message, error);
        }
    }

    private static void ShowOffscreen(Window w)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -20000;
        w.Top = -20000;
        w.ShowActivated = false;
        w.Show();
        w.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static List<string> TabCycle(Window w, int max = 80)
    {
        var names = new List<string>();
        var first = AccessibilityAuditor.InteractiveControls(w).First(c => c.IsVisible);
        first.Focus();
        for (int i = 0; i < max; i++)
        {
            if (Keyboard.FocusedElement is FrameworkElement fe)
            {
                var name = string.IsNullOrEmpty(fe.Name) ? fe.GetType().Name : fe.Name;
                if (names.Count > 0 && name == names[0] && i > 1)
                {
                    break;
                }

                names.Add(name);
                fe.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Input);
            }
        }

        return names;
    }

    [Fact]
    public void Main_window_controls_are_named_reachable_and_have_unique_access_keys()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        RunSta(() =>
        {
            Localizer.Current = Localizer.Load("bg");
            var h = new OrchestratorHarness();
            var vm = new MainViewModel(h.Orchestrator, h.Settings, Localizer.Current, Dispatcher.CurrentDispatcher);
            var w = new MainWindow(vm) { AllowClose = true };
            ShowOffscreen(w);

            var issues = AccessibilityAuditor.Audit(w);
            Assert.True(issues.Count == 0, string.Join("; ", issues.Select(i => $"{i.Element}: {i.Problem}")));

            var cycle = TabCycle(w);
            foreach (var expected in new[] { "TalkButton", "StopButton", "MicButton", "SettingsButton", "ExitButton" })
            {
                Assert.Contains(expected, cycle);
            }

            Assert.True(cycle.IndexOf("TalkButton") < cycle.IndexOf("StopButton"), "Говори comes before Спри in the tab order");
            Assert.Equal("Говори", AccessibilityAuditor.AccessibleName(w.TalkButton));

            w.ShowConversation();
            w.UpdateLayout();
            var conversationCycle = TabCycle(w);
            foreach (var expected in new[] { "MessageBox", "SendButton", "RepeatButton", "InterruptButton", "TranscriptList" })
            {
                Assert.Contains(expected, conversationCycle);
            }

            w.Close();
        });
    }

    [Fact]
    public void Escape_stops_and_F1_talks_from_the_keyboard()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        RunSta(() =>
        {
            var h = new OrchestratorHarness();
            var vm = new MainViewModel(h.Orchestrator, h.Settings, Localizer.Load("bg"), Dispatcher.CurrentDispatcher);
            var w = new MainWindow(vm) { AllowClose = true };
            var bindings = w.InputBindings.OfType<KeyBinding>().ToList();
            Assert.Contains(bindings, b => b.Key == Key.Escape && b.Command == vm.StopCommand);
            Assert.Contains(bindings, b => b.Key == Key.F1 && b.Command == vm.TalkCommand);
            w.Close();
        });
    }

    [Fact]
    public void Settings_and_wizard_windows_are_fully_keyboard_accessible()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        RunSta(() =>
        {
            var paths = new AppPaths(Support.TestModels.NewTempDir());
            var services = AppServices.Create(paths, new DesktopUiHost(Dispatcher.CurrentDispatcher));
            try
            {
                var settingsWindow = new SettingsWindow(new SettingsViewModel(services, () => []));
                ShowOffscreen(settingsWindow);
                foreach (TabItem tab in settingsWindow.SettingsTabs.Items)
                {
                    settingsWindow.SettingsTabs.SelectedItem = tab;
                    settingsWindow.UpdateLayout();
                    var issues = AccessibilityAuditor.Audit(settingsWindow);
                    Assert.True(issues.Count == 0, $"{tab.Header}: " + string.Join("; ", issues.Select(i => $"{i.Element}: {i.Problem}")));
                }

                settingsWindow.Close();

                var wizard = new FirstRunWizard(new WizardViewModel(services, new SettingsViewModel(services, () => [])));
                ShowOffscreen(wizard);
                var wizardIssues = AccessibilityAuditor.Audit(wizard);
                Assert.True(wizardIssues.Count == 0, string.Join("; ", wizardIssues.Select(i => $"{i.Element}: {i.Problem}")));
                Assert.True(wizard.StepText.IsKeyboardFocused, "the step text receives focus so screen readers read the step");
                wizard.Close();
            }
            finally
            {
                services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }
}

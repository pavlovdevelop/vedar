using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Vedar.Accessibility;
using Vedar.Desktop.ViewModels;

namespace Vedar.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AccessKeyAnnotator.Attach(this);
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        DataContext = viewModel;
        // Keyboard shortcuts bound directly to the commands (deterministic, no binding resolution needed).
        InputBindings.Add(new KeyBinding(viewModel.StopCommand, Key.Escape, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(viewModel.TalkCommand, Key.F1, ModifierKeys.None));
        MessageBox.InputBindings.Add(new KeyBinding(viewModel.SendCommand, Key.Enter, ModifierKeys.None));
        viewModel.Transcript.CollectionChanged += (_, _) =>
        {
            if (TranscriptList.Items.Count > 0)
            {
                TranscriptList.ScrollIntoView(TranscriptList.Items[^1]);
            }
        };
    }

    /// <summary>When true, closing the window really exits; otherwise it hides to the notification area.</summary>
    public bool AllowClose { get; set; }

    public event EventHandler? HiddenToTray;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosing(e);
    }

    public void ShowConversation() => Tabs.SelectedItem = ConversationTab;

    /// <summary>Brings the window forward. Only called after an explicit user request (shortcut, tray, voice command).</summary>
    public void BringToFront()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        if (!IsKeyboardFocusWithin)
        {
            TalkButton.Focus();
        }
    }
}

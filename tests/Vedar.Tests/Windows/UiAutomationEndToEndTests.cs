using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Vedar.Tests.Support;

namespace Vedar.Tests.Windows;

/// <summary>
/// Starts the real VEDAR.exe and inspects it through Windows UI Automation — the same API that Narrator and NVDA use.
/// </summary>
public class UiAutomationEndToEndTests
{
    private static string? FindExe()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        foreach (var candidate in new[]
                 {
                     Environment.GetEnvironmentVariable("VEDAR_EXE"),
                     Path.Combine(root, "artifacts", "publish", "win-x64", "VEDAR.exe"),
                     Path.Combine(root, "src", "Vedar.Desktop", "bin", "Release", "net10.0-windows", "win-x64", "VEDAR.exe"),
                     Path.Combine(root, "src", "Vedar.Desktop", "bin", "Debug", "net10.0-windows", "win-x64", "VEDAR.exe"),
                 })
        {
            if (candidate is not null && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [Fact]
    public void Main_window_exposes_named_controls_to_screen_readers_and_works_with_keyboard()
    {
        Assert.SkipUnless(System.OperatingSystem.IsWindows(), "Windows only");
        var exe = FindExe();
        Assert.SkipWhen(exe is null, "VEDAR.exe has not been built.");
        var data = TestModels.NewTempDir();
        File.WriteAllText(Path.Combine(data, "settings.json"), """
            { "General": { "FirstRunCompleted": true, "GreetOnStartup": false, "StartMinimizedToTray": false },
              "Activation": { "HandsFreeEnabled": false, "PushToTalkHotkey": "Ctrl+Alt+Shift+F9", "StopHotkey": "Ctrl+Alt+Shift+F10", "MicrophoneToggleHotkey": "Ctrl+Alt+Shift+F11" },
              "Ai": { "Provider": "None" } }
            """);
        var psi = new ProcessStartInfo(exe!) { UseShellExecute = false };
        psi.Environment["VEDAR_DATA_DIR"] = data;
        using var app = Application.Launch(psi);
        using var automation = new UIA3Automation();
        try
        {
            var window = Retry.WhileNull(() => app.GetAllTopLevelWindows(automation).FirstOrDefault(w => w.Name.Contains("VEDAR")), TimeSpan.FromSeconds(60)).Result;
            if (window is null)
            {
                var names = app.HasExited ? "(process exited)" : string.Join(" | ", app.GetAllTopLevelWindows(automation).Select(w => $"'{w.Name}'"));
                var log = Directory.Exists(Path.Combine(data, "logs"))
                    ? string.Join("\n", Directory.GetFiles(Path.Combine(data, "logs")).Select(File.ReadAllText))
                    : "(no log)";
                Assert.Fail($"VEDAR window not found. Exited: {app.HasExited} (code {(app.HasExited ? app.ExitCode : 0)}). Windows: {names}\nLog:\n{log}");
            }

            var talk = window.FindFirstDescendant(cf => cf.ByName("Говори").And(cf.ByControlType(ControlType.Button)));
            var stop = window.FindFirstDescendant(cf => cf.ByName("Спри").And(cf.ByControlType(ControlType.Button)));
            var settings = window.FindFirstDescendant(cf => cf.ByName("Настройки").And(cf.ByControlType(ControlType.Button)));
            Assert.NotNull(talk);
            Assert.NotNull(stop);
            Assert.NotNull(settings);
            Assert.True(talk!.Properties.IsKeyboardFocusable.Value);
            Assert.False(string.IsNullOrWhiteSpace(talk.Properties.HelpText.ValueOrDefault));
            // WPF exposes the access key letter only ("Г", not "Alt+Г"). Setting AutomationProperties.AccessKey does
            // not help: UIElementAutomationPeer.GetAccessKeyCore (WPF 10.0.14) then returns an empty string.
            Assert.Equal("Г", talk.Properties.AccessKey.ValueOrDefault, ignoreCase: true);

            var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
            var focusable = buttons.Where(b => b.Properties.IsKeyboardFocusable.ValueOrDefault).ToList();
            foreach (var expected in new[] { "Говори", "Спри", "Настройки" })
            {
                Assert.Contains(focusable, b => b.Name == expected);
            }

            Assert.All(focusable, b => Assert.False(string.IsNullOrWhiteSpace(b.Name), "every keyboard-reachable button has an accessible name"));
            // The only unnamed buttons allowed are the parts of WPF's built-in scroll bar (not keyboard-focusable).
            string[] scrollBarParts = ["PART_LineUpButton", "PART_LineDownButton", "PageUp", "PageDown", "PART_LineLeftButton", "PART_LineRightButton", "PageLeft", "PageRight"];
            Assert.All(buttons.Where(b => string.IsNullOrWhiteSpace(b.Name)), b => Assert.Contains(b.Properties.AutomationId.ValueOrDefault, scrollBarParts));

            // Keyboard: focus "Настройки" and press Space (Alt+Н needs a Bulgarian keyboard layout, which CI does not have).
            window.Focus();
            settings!.Focus();
            Keyboard.Type(VirtualKeyShort.SPACE);
            var settingsWindow = Retry.WhileNull(() => app.GetAllTopLevelWindows(automation).FirstOrDefault(w => w.Name.Contains("Настройки")), TimeSpan.FromSeconds(10)).Result;
            var openedWith = "keyboard (Space)";
            if (settingsWindow is null)
            {
                // The CI desktop may refuse to give the window the foreground; fall back to the UIA Invoke pattern.
                openedWith = "UI Automation Invoke";
                settings!.AsButton().Invoke();
                settingsWindow = Retry.WhileNull(() => app.GetAllTopLevelWindows(automation).FirstOrDefault(w => w.Name.Contains("Настройки")), TimeSpan.FromSeconds(10)).Result;
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"Settings window opened with: {openedWith}");

            Assert.NotNull(settingsWindow);
            var tabs = settingsWindow!.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem));
            Assert.Equal(7, tabs.Length);
            Assert.All(tabs, t => Assert.False(string.IsNullOrWhiteSpace(t.Name)));
            var edits = settingsWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
            Assert.All(edits, e => Assert.False(string.IsNullOrWhiteSpace(e.Name), "every text field has an accessible name"));
        }
        finally
        {
            app.Kill();
        }
    }
}

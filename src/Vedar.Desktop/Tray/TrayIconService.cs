using System.Drawing;
using System.Windows.Forms;
using Vedar.Core.Localization;

namespace Vedar.Desktop.Tray;

/// <summary>
/// Icon in the Windows notification area. Reachable with the keyboard: Win+B, arrows, Enter (show window),
/// Shift+F10 or the Applications key (menu). Every menu item has a text label.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _micItem;

    public TrayIconService(Localizer loc, Action show, Action talk, Action stop, Action toggleMic, Action settings, Action exit)
    {
        using var stream = typeof(TrayIconService).Assembly.GetManifestResourceStream("Vedar.Desktop.vedar.ico");
        var menu = new ContextMenuStrip { AccessibleName = "VEDAR", ShowImageMargin = false };
        menu.Items.Add(new ToolStripMenuItem(loc["tray.show"], null, (_, _) => show()) { Font = new Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem(loc["tray.talk"], null, (_, _) => talk()));
        menu.Items.Add(new ToolStripMenuItem(loc["tray.stop"], null, (_, _) => stop()));
        _micItem = new ToolStripMenuItem(loc["tray.micMute"], null, (_, _) => toggleMic());
        menu.Items.Add(_micItem);
        menu.Items.Add(new ToolStripMenuItem(loc["tray.settings"], null, (_, _) => settings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(loc["tray.exit"], null, (_, _) => exit()));
        _icon = new NotifyIcon
        {
            Icon = stream is null ? SystemIcons.Application : new Icon(stream),
            Text = "VEDAR",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                show();
            }
        };
        _icon.DoubleClick += (_, _) => show();
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void SetMicrophoneMuted(bool muted, Localizer loc) => _micItem.Text = muted ? loc["tray.micUnmute"] : loc["tray.micMute"];

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}

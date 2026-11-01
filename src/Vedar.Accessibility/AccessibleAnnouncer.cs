using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Vedar.Accessibility;

/// <summary>
/// Sends short messages to screen readers through UI Automation notification events (supported by Narrator
/// and NVDA 2019.3+) and, as a fallback, through a polite live region. Must be called on the UI thread.
/// </summary>
public sealed class AccessibleAnnouncer
{
    private readonly FrameworkElement _host;
    private readonly TextBlock? _liveRegion;

    public AccessibleAnnouncer(FrameworkElement host, TextBlock? liveRegion)
    {
        _host = host;
        _liveRegion = liveRegion;
        if (_liveRegion is not null)
        {
            AutomationProperties.SetLiveSetting(_liveRegion, AutomationLiveSetting.Polite);
        }
    }

    public string? LastAnnouncement { get; private set; }

    public void Announce(string text, bool important)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        LastAnnouncement = text;
        var peer = UIElementAutomationPeer.FromElement(_host) ?? UIElementAutomationPeer.CreatePeerForElement(_host);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.Other,
            important ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
            text,
            "VEDAR");

        if (_liveRegion is not null)
        {
            _liveRegion.Text = text;
            var livePeer = UIElementAutomationPeer.FromElement(_liveRegion) ?? UIElementAutomationPeer.CreatePeerForElement(_liveRegion);
            livePeer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}

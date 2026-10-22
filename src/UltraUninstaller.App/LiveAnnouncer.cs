using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace UltraUninstaller.App;

/// <summary>
/// Puts a message into a status line AND tells screen readers about it.
///
/// In WPF, AutomationProperties.LiveSetting="Assertive" on its own does nothing - the
/// app has to raise the LiveRegionChanged event itself every time the text changes.
/// Earlier versions only changed the text, so JAWS/NVDA did not announce status
/// messages (results, errors, progress) unless the user went looking for them.
/// </summary>
internal static class LiveAnnouncer
{
    public static void Announce(TextBlock target, string message)
    {
        target.Text = message;

        try
        {
            var peer = UIElementAutomationPeer.FromElement(target)
                       ?? UIElementAutomationPeer.CreatePeerForElement(target);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch
        {
            // Announcing is best effort - the text is in the status line either way.
        }
    }
}

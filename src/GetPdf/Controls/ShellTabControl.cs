using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace GetPdf.Controls;

/// <summary>
/// Document tab host whose header also carries commands ("+", move, detach, help) in
/// <see cref="ShellHeaderSlots"/>. The stock TabControl peer exposes only tab items, so
/// this peer adds the header commands back for UI Automation and screen readers.
/// </summary>
public sealed class ShellTabControl : TabControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ShellTabControlAutomationPeer(this);

    private sealed class ShellTabControlAutomationPeer(ShellTabControl owner) : TabControlAutomationPeer(owner)
    {
        protected override List<AutomationPeer> GetChildrenCore()
        {
            var children = base.GetChildrenCore() ?? [];
            if (Owner is ShellTabControl { Tag: ShellHeaderSlots slots })
            {
                foreach (var slot in new[] { slots.Leading, slots.Trailing })
                    if (slot is UIElement element) AddPeers(element, children);
            }
            return children;
        }

        private static void AddPeers(UIElement element, List<AutomationPeer> into)
        {
            if (UIElementAutomationPeer.CreatePeerForElement(element) is { } peer)
            {
                into.Add(peer);
                return;
            }
            // Layout panels have no peer of their own: surface their children.
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                if (VisualTreeHelper.GetChild(element, i) is UIElement child) AddPeers(child, into);
        }
    }
}

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Pia.Behaviors;

/// <summary>
/// Re-reads an ItemsControl's automation subtree once its containers exist. Items that arrive while a UIA
/// client is attached make WPF build the item peers on the spot — before the panel has generated a single
/// container — and each peer then caches an empty subtree that nothing invalidates, so a screen reader or a
/// UI script sees rows with no content at all.
/// </summary>
public static class AutomationPeerRefreshBehavior
{
    public static readonly DependencyProperty RefreshOnContainersGeneratedProperty =
        DependencyProperty.RegisterAttached("RefreshOnContainersGenerated", typeof(bool),
            typeof(AutomationPeerRefreshBehavior),
            new PropertyMetadata(false, OnRefreshOnContainersGeneratedChanged));

    public static bool GetRefreshOnContainersGenerated(DependencyObject obj) =>
        (bool)obj.GetValue(RefreshOnContainersGeneratedProperty);

    public static void SetRefreshOnContainersGenerated(DependencyObject obj, bool value) =>
        obj.SetValue(RefreshOnContainersGeneratedProperty, value);

    private static void OnRefreshOnContainersGeneratedChanged(
        DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl items || e.NewValue is not true) return;

        var generator = items.ItemContainerGenerator;
        generator.StatusChanged += (_, _) =>
        {
            if (generator.Status != GeneratorStatus.ContainersGenerated) return;
            if (!GetRefreshOnContainersGenerated(items)) return;

            // Generation finishes inside the panel's measure, so the containers exist but their templates
            // have not been applied yet — reading the subtree here would cache a second empty one. Loaded
            // priority is the first point after the layout pass that produced them.
            items.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Refresh(items));
        };
    }

    private static void Refresh(ItemsControl items)
    {
        // FromElement, not CreatePeerForElement: with no client attached there is no stale peer to fix, and
        // walking the subtree of a long chat for nobody is pure cost.
        if (UIElementAutomationPeer.FromElement(items) is not { } peer) return;

        // One level down, not the whole subtree: the empty cache sits on the item peers, and nothing below
        // them was ever built to go stale. Recursing costs half a second on a 200-message chat, for nothing.
        peer.ResetChildrenCache();
        foreach (var child in peer.GetChildren() ?? [])
            child.ResetChildrenCache();

        peer.RaiseAutomationEvent(AutomationEvents.StructureChanged);
    }
}

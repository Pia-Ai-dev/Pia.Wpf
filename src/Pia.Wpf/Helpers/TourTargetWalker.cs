using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Pia.Models;

namespace Pia.Helpers;

/// <summary>Pure — the root is an argument, never <c>Application.Current</c>, so tests can drive it directly.</summary>
public static class TourTargetWalker
{
    public const int MaxTargets = 200;

    public const double MinimumSizeDip = 4;

    public static TourTargetScan Collect(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var rootView = root.GetType().Name;
        var found = new List<TourTarget>();
        var completed = Walk(root, root, new Rect(root.RenderSize), rootView, owningView: null, found);

        return new TourTargetScan(rootView, !completed, found);
    }

    private static bool Walk(
        DependencyObject node,
        FrameworkElement root,
        Rect clip,
        string rootView,
        string? owningView,
        List<TourTarget> found)
    {
        if (node is FrameworkElement element)
        {
            if (element.Visibility != Visibility.Visible || !element.IsHitTestVisible || element.Opacity <= 0)
                return true;

            var bounds = ReferenceEquals(element, root)
                ? new Rect(root.RenderSize)
                : element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

            if (Clips(element))
            {
                clip = Rect.Intersect(clip, bounds);
                if (clip.IsEmpty)
                    return true;
            }

            if (owningView is null && element is UserControl && !ReferenceEquals(element, root))
                owningView = element.GetType().Name;

            if (IsOffered(element, bounds, clip))
            {
                // Refused before it is added, so a scan that fills the cap exactly reports nothing was dropped.
                if (found.Count >= MaxTargets)
                    return false;

                found.Add(Describe(element, bounds, owningView ?? rootView));
            }
        }

        var childCount = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);

            // A 3D subtree cannot carry a 2D spotlight rect.
            if (child is Visual3D)
                continue;

            if (!Walk(child, root, clip, rootView, owningView, found))
                return false;
        }

        return true;
    }

    // A ScrollViewer clips its content through a layout clip rather than ClipToBounds, and every long
    // view in this app scrolls; its own rect is always a superset of the viewport, so it is safe to add.
    private static bool Clips(FrameworkElement element) =>
        element.ClipToBounds || element.Clip is not null || element is ScrollContentPresenter or ScrollViewer;

    private static bool IsOffered(FrameworkElement element, Rect bounds, Rect clip) =>
        !string.IsNullOrWhiteSpace(AutomationProperties.GetAutomationId(element))
        && bounds.Width >= MinimumSizeDip
        && bounds.Height >= MinimumSizeDip
        && bounds.IntersectsWith(clip);

    private static TourTarget Describe(FrameworkElement element, Rect bounds, string owningView)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(element);

        string? name = AutomationProperties.GetName(element);
        if (string.IsNullOrWhiteSpace(name))
            name = peer?.GetName();

        return new TourTarget(
            AutomationProperties.GetAutomationId(element),
            string.IsNullOrWhiteSpace(name) ? null : name,
            peer?.GetAutomationControlType().ToString() ?? element.GetType().Name,
            new TourTargetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            owningView);
    }
}

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Pia.Helpers;

public static class VisualTreeExtensions
{
    public static T? FindAncestor<T>(this DependencyObject? obj) where T : DependencyObject
    {
        while (obj is not null)
        {
            if (obj is T target) return target;
            obj = obj.GetVisualOrLogicalParent();
        }
        return null;
    }

    public static T? FindAncestorByName<T>(this DependencyObject? obj, string name) where T : FrameworkElement
    {
        while (obj is not null)
        {
            if (obj is T fe && fe.Name == name) return fe;
            obj = obj.GetVisualOrLogicalParent();
        }
        return null;
    }

    public static T? FindChild<T>(this DependencyObject parent, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name) return fe;
            var result = FindChild<T>(child, name);
            if (result is not null) return result;
        }
        return null;
    }

    // VisualTreeHelper.GetParent throws on ContentElement (Run, Hyperlink, etc.);
    // fall back to the logical tree until we re-enter the visual tree.
    public static DependencyObject? GetVisualOrLogicalParent(this DependencyObject obj) =>
        obj is Visual or Visual3D
            ? VisualTreeHelper.GetParent(obj)
            : LogicalTreeHelper.GetParent(obj);
}

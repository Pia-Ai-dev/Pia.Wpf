using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Pia.Behaviors;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The editor's folder lists and tool ticks hold the only buttons and checkboxes for their rows. Rows added while
/// a UIA client is attached would cache an empty automation subtree (see <see cref="MessageListPeerRefreshTests"/>),
/// leaving a screen reader and a UI script with rows they cannot remove or tick. The recovery only runs if the
/// markup asks for it.
/// </summary>
[Collection("WpfApplicationStatic")]
public class McpServersPeerRefreshTests
{
    [Fact]
    public void EveryEditableList_AsksForThePeerRefresh()
    {
        var lists = WpfStaHost.Run(() => RefreshFlagsByItemsSource(new Pia.Views.SettingsViews.McpServersView()));

        Assert.Equal(["EditReadonlyPaths", "EditReadwritePaths", "Tools"], lists.Keys.Order(StringComparer.Ordinal));
        Assert.All(lists, list => Assert.True(list.Value, $"{list.Key} does not ask for the peer refresh"));
    }

    private static Dictionary<string, bool> RefreshFlagsByItemsSource(DependencyObject root)
    {
        var found = new Dictionary<string, bool>();
        Walk(root, found);
        return found;
    }

    private static void Walk(DependencyObject element, Dictionary<string, bool> found)
    {
        if (element is ItemsControl items)
        {
            var source = BindingOperations.GetBinding(items, ItemsControl.ItemsSourceProperty)?.Path.Path;
            if (source is "EditReadwritePaths" or "EditReadonlyPaths" or "Tools")
                found[source] = AutomationPeerRefreshBehavior.GetRefreshOnContainersGenerated(items);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            Walk(child, found);
    }
}

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Pia.Behaviors;
using Pia.ViewModels.Models;
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

    /// <summary>A tool name is full of underscores, and a string Content reads the first one as an access key,
    /// which the checkbox peer then puts in its UIA name whatever AutomationProperties.Name says.</summary>
    [Theory]
    [InlineData("get_server_info")]
    [InlineData("delete_directory")]
    public void TheToolTick_IsNamedAfterTheRawToolName(string toolName)
    {
        var name = WpfStaHost.Run(() =>
        {
            var tools = ItemsControls(new Pia.Views.SettingsViews.McpServersView())
                .Single(items => SourcePath(items) == "Tools");

            // A presenter, not LoadContent: the bindings only attach once the template is applied to real content.
            var presenter = new ContentPresenter
            {
                Content = new McpToolRow(toolName, null, false, true),
                ContentTemplate = tools.ItemTemplate
            };
            presenter.Measure(new Size(400, 100));
            presenter.Arrange(new Rect(0, 0, 400, 100));
            presenter.UpdateLayout();

            var tick = VisualDescendants(presenter).OfType<CheckBox>().Single();
            return UIElementAutomationPeer.CreatePeerForElement(tick).GetName();
        });

        Assert.Equal(toolName, name);
    }

    private static Dictionary<string, bool> RefreshFlagsByItemsSource(DependencyObject root)
    {
        var found = new Dictionary<string, bool>();
        foreach (var items in ItemsControls(root))
        {
            var source = SourcePath(items);
            if (source is "EditReadwritePaths" or "EditReadonlyPaths" or "Tools")
                found[source] = AutomationPeerRefreshBehavior.GetRefreshOnContainersGenerated(items);
        }

        return found;
    }

    private static string? SourcePath(ItemsControl items) =>
        BindingOperations.GetBinding(items, ItemsControl.ItemsSourceProperty)?.Path.Path;

    private static IEnumerable<ItemsControl> ItemsControls(DependencyObject root) =>
        LogicalDescendants(root).OfType<ItemsControl>();

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject element)
    {
        yield return element;
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            foreach (var inner in LogicalDescendants(child))
                yield return inner;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject element)
    {
        yield return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            foreach (var inner in VisualDescendants(VisualTreeHelper.GetChild(element, i)))
                yield return inner;
    }
}

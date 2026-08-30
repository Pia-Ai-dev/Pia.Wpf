using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Pia.Behaviors;
using Pia.Helpers;
using Pia.Tests.Views;
using Xunit;

namespace Pia.Tests.Behaviors;

/// <summary>
/// Card text is rendered as inlines, so WPF hands the behavior a Run as the event's OriginalSource —
/// a ContentElement that VisualTreeHelper.GetParent refuses.
/// </summary>
[Collection("WpfApplicationStatic")]
public class KanbanDragDropBehaviorTests
{
    [Fact]
    public void PressingOnCardText_DoesNotThrowOnTheInlineRun()
    {
        var error = WpfStaHost.Run(() =>
        {
            var list = RenderedCardList();
            var run = CardTextRun(list);

            return Record.Exception(() => list.RaiseEvent(
                new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = run,
                }));
        });
        WpfStaHost.Pump();

        Assert.Null(error);
    }

    [Fact]
    public void WalkingUpFromCardText_ReachesTheItemContainer()
    {
        var index = WpfStaHost.Run(() =>
        {
            var list = RenderedCardList();
            var container = CardTextRun(list).FindAncestor<ContentPresenter>();

            return list.ItemContainerGenerator.IndexFromContainer(container);
        });

        Assert.Equal(0, index);
    }

    private static ItemsControl RenderedCardList()
    {
        var template = (DataTemplate)XamlReader.Parse(
            """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Border><TextBlock><Run Text="{Binding Mode=OneWay}"/></TextBlock></Border>
            </DataTemplate>
            """);

        // Without an Application-supplied default style the bare ItemsControl never templates itself,
        // so the item containers are never generated.
        var shell = (ControlTemplate)XamlReader.Parse(
            """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ItemsControl">
              <ItemsPresenter/>
            </ControlTemplate>
            """);

        var list = new ItemsControl { ItemsSource = new[] { "Buy milk" }, ItemTemplate = template, Template = shell };
        KanbanDragDropBehavior.SetColumnId(list, "column-1");
        KanbanDragDropBehavior.SetIsEnabled(list, true);

        list.Measure(new Size(400, 400));
        list.Arrange(new Rect(0, 0, 400, 400));
        list.UpdateLayout();
        return list;
    }

    private static Run CardTextRun(ItemsControl list) =>
        Descendants(list).OfType<TextBlock>().Single().Inlines.OfType<Run>().Single();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}

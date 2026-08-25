using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using NSubstitute;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The picker's checkbox lives two DataTemplates deep, so no parse test walks it: a mistyped path is a tick
/// that grants nothing. This realizes both templates and reads the declared paths.
/// </summary>
[Collection("WpfApplicationStatic")]
public class RoutineToolRowTemplateTests
{
    private static ILocalizationService Localizer()
    {
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        return loc;
    }

    private static RoutineToolRow Row(string toolName = "write_file", string? description = "Write a file") =>
        RoutineToolRow.FromCatalog(
            new ToolCatalogEntry(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "files", toolName, description,
                IsExternalRoute: false, ServerDeclaredDestructive: false),
            Localizer());

    /// <summary>Even a row for a tool this device cannot provide stays tickable — off is the only way to revoke
    /// a stale grant, so an IsEnabled binding here would strand it forever.</summary>
    [Fact]
    public void TheTick_BindsTheRowsOwnSelection_AndIsNeverDisabled()
    {
        var observed = WpfStaHost.Run(() =>
        {
            var realized = RealizeRow();
            realized.DataContext = Row("delete_file");

            return FindElements<CheckBox>(realized)
                .Select(b => (
                    Checked: BindingPathWalker.PathOf(b, ToggleButton.IsCheckedProperty),
                    Enabled: BindingPathWalker.PathOf(b, UIElement.IsEnabledProperty),
                    b.IsEnabled))
                .ToList();
        });

        var tick = Assert.Single(observed);
        Assert.Equal("IsSelected", tick.Checked);
        Assert.Null(tick.Enabled);
        Assert.True(tick.IsEnabled);
    }

    /// <summary>A bare list of names is the complaint this picker exists to answer, so the description renders.</summary>
    [Fact]
    public void TheRow_RendersTheToolName_AndItsDescription()
    {
        FrameworkElement? realized = null;
        WpfStaHost.Run(() =>
        {
            realized = RealizeRow();
            realized.DataContext = Row();
            return 0;
        });
        WpfStaHost.Pump();

        var texts = WpfStaHost.Run(() =>
            FindElements<TextBlock>(realized!).Select(tb => tb.Text).ToArray());

        Assert.Contains("write_file", texts);
        Assert.Contains("Write a file", texts);
        Assert.DoesNotContain(texts, t => t.StartsWith('[') && t.EndsWith(']'));
    }

    /// <summary>Advice on a choice already made: an untouched 40-row list stays scannable.</summary>
    [Fact]
    public void TheCautionLine_AppearsOnlyOnceTheToolIsTicked()
    {
        TextBlock? untouched = null;
        TextBlock? ticked = null;
        TextBlock? benign = null;
        WpfStaHost.Run(() =>
        {
            untouched = CautionLine(Row("delete_file"));
            ticked = CautionLine(Row("delete_file"), selected: true);
            benign = CautionLine(Row("write_file"), selected: true);
            return 0;
        });
        WpfStaHost.Pump();

        var observed = WpfStaHost.Run(() =>
            (untouched!.Visibility, ticked!.Visibility, benign!.Visibility));

        Assert.Equal(Visibility.Collapsed, observed.Item1);
        Assert.Equal(Visibility.Visible, observed.Item2);
        Assert.Equal(Visibility.Collapsed, observed.Item3);
    }

    /// <summary>Carried through a save, so the row has to say why it cannot fire here.</summary>
    [Fact]
    public void AnUnavailableRow_ExplainsItself()
    {
        FrameworkElement? realized = null;
        WpfStaHost.Run(() =>
        {
            realized = RealizeRow();
            realized.DataContext = RoutineToolRow.Unavailable("jira_create_issue", Localizer());
            return 0;
        });
        WpfStaHost.Pump();

        var visible = WpfStaHost.Run(() => FindElements<TextBlock>(realized!)
            .Single(tb => BindingPathWalker.PathOf(tb, UIElement.VisibilityProperty) == "IsUnavailable")
            .Visibility);

        Assert.Equal(Visibility.Visible, visible);
    }

    private static TextBlock CautionLine(RoutineToolRow row, bool selected = false)
    {
        row.IsSelected = selected;
        var realized = RealizeRow();
        realized.DataContext = row;
        return FindElements<TextBlock>(realized)
            .Single(tb => BindingPathWalker.PathOf(tb, UIElement.VisibilityProperty) == "HasCaution");
    }

    /// <summary>Group template first, then the tool template inside it. Located by declared path, not by index:
    /// RoutinesView's FIRST templated ItemsControl is the blueprint catalogue, not this one.</summary>
    private static FrameworkElement RealizeRow()
    {
        var outer = BindingPathWalker.FindLogical<ItemsControl>(new Pia.Views.RoutinesView())
            .Single(ic => (BindingOperations.GetBinding(ic, ItemsControl.ItemsSourceProperty) as Binding)
                ?.Path?.Path == "EditToolGroups");

        var group = (FrameworkElement)outer.ItemTemplate.LoadContent();
        group.DataContext = new RoutineToolGroup("files", false, [Row()]);

        var inner = FindElements<ItemsControl>(group)
            .Single(ic => (BindingOperations.GetBinding(ic, ItemsControl.ItemsSourceProperty) as Binding)
                ?.Path?.Path == "Tools");

        return (FrameworkElement)inner.ItemTemplate.LoadContent();
    }

    /// <summary>Logical AND visual, deduped, so a generated visual child added by a later template change is found.</summary>
    private static IEnumerable<T> FindElements<T>(DependencyObject root) where T : DependencyObject
    {
        var seen = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current)) continue;
            if (current is T hit) yield return hit;

            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                stack.Push(child);

            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                    stack.Push(VisualTreeHelper.GetChild(current, i));
        }
    }
}

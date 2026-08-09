using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Pia.Services.Interfaces;
using Pia.Models;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The catalogue's toggles live two DataTemplates deep, so no parse test walks them: a mistyped path is a
/// checkbox that silently grants nothing. This realizes both templates and reads the declared paths.
/// </summary>
[Collection("WpfApplicationStatic")]
public class ToolCatalogRowTemplateTests
{
    private static ToolCatalogRow Row(bool external = true) =>
        new(new ToolCatalogEntry(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "some-mcp-server", "send_email", "Send mail",
                IsExternalRoute: external, ServerDeclaredDestructive: false),
            external ? ToolClass.External : ToolClass.Files,
            isAllowlisted: false);

    [Fact]
    public void TheToggles_BindToTheRowsOwnGrantStateAndItsOfferability()
    {
        var observed = WpfStaHost.Run(() =>
        {
            var row = RealizeRow(new Pia.Views.SettingsViews.AssistantView());
            row.DataContext = Row();

            // Ordered by the declared path, because the tree walk's own order is an implementation detail.
            return FindElements<CheckBox>(row)
                .Select(b => (
                    Checked: BindingPathWalker.PathOf(b, ToggleButton.IsCheckedProperty),
                    Enabled: BindingPathWalker.PathOf(b, UIElement.IsEnabledProperty)))
                .OrderBy(pair => pair.Checked, StringComparer.Ordinal)
                .ToList();
        });

        Assert.Equal(
            [("AllowedAlways", "CanChangeAlways"), ("AllowedForSession", "CanChangeSession")],
            observed);
    }

    [Fact]
    public void TheRow_RendersItsToolNameAndItsReason()
    {
        FrameworkElement? row = null;
        WpfStaHost.Run(() =>
        {
            row = RealizeRow(new Pia.Views.SettingsViews.AssistantView());
            // A built-in route: offerable for the session only, so the reason line is populated.
            row.DataContext = Row(external: false);
            return 0;
        });
        WpfStaHost.Pump();

        var rendered = WpfStaHost.Run(() =>
            FindElements<TextBlock>(row!).Select(tb => tb.Text).ToArray());

        Assert.Contains("send_email", rendered);
        Assert.Contains(rendered, t => t == Row(external: false).Reason);
        // The reason came from the resx, not from an unresolved key placeholder.
        Assert.DoesNotContain(rendered, t => t.StartsWith('[') && t.EndsWith(']'));
    }

    /// <summary>Group template first, then the tool template inside it — the toggles are only in the inner one.</summary>
    private static FrameworkElement RealizeRow(Pia.Views.SettingsViews.AssistantView view)
    {
        var outer = BindingPathWalker.FindLogical<ItemsControl>(view)
            .Single(ic => (BindingOperations.GetBinding(ic, ItemsControl.ItemsSourceProperty) as Binding)
                ?.Path?.Path == "ToolCatalog");

        var group = (FrameworkElement)outer.ItemTemplate.LoadContent();
        group.DataContext = new ToolCatalogGroup("some-mcp-server", [Row()]);

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

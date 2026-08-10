using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Pia.Services.Interfaces;
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
    private static ToolCatalogRow Row(string toolName = "send_email") =>
        new(new ToolCatalogEntry(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "some-mcp-server", toolName, "Send mail",
            IsExternalRoute: true, ServerDeclaredDestructive: false));

    /// <summary>Both tiers are offered for every tool, so neither box may carry an IsEnabled binding — nor a
    /// literal False, which a path-only assertion would not see.</summary>
    [Fact]
    public void TheToggles_BindToTheRowsOwnGrantState_AndAreNeverDisabled()
    {
        var observed = WpfStaHost.Run(() =>
        {
            var row = RealizeRow(new Pia.Views.SettingsViews.AssistantView());
            row.DataContext = Row("delete_file");

            // Ordered by the declared path, because the tree walk's own order is an implementation detail.
            return FindElements<CheckBox>(row)
                .Select(b => (
                    Checked: BindingPathWalker.PathOf(b, ToggleButton.IsCheckedProperty),
                    Enabled: BindingPathWalker.PathOf(b, UIElement.IsEnabledProperty),
                    b.IsEnabled))
                .OrderBy(t => t.Checked, StringComparer.Ordinal)
                .ToList();
        });

        Assert.Equal(["AllowedAlways", "AllowedForSession"], observed.Select(t => t.Checked));
        Assert.All(observed, t =>
        {
            Assert.Null(t.Enabled);
            Assert.True(t.IsEnabled);
        });
    }

    [Fact]
    public void TheRow_RendersItsToolNameAndItsCaution()
    {
        FrameworkElement? row = null;
        WpfStaHost.Run(() =>
        {
            row = RealizeRow(new Pia.Views.SettingsViews.AssistantView());
            // Delete-like AND granted — the caution line is populated only for a tool the user has ticked.
            var granted = Row("delete_file");
            granted.AllowedAlways = true;
            row.DataContext = granted;
            return 0;
        });
        WpfStaHost.Pump();

        var rendered = WpfStaHost.Run(() =>
            FindElements<TextBlock>(row!).Select(tb => tb.Text).ToArray());

        var caution = Row("delete_file").CautionText;
        Assert.NotEmpty(caution);
        Assert.Contains("delete_file", rendered);
        Assert.Contains(rendered, t => t == caution);
        // The caution came from the resx, not from an unresolved key placeholder.
        Assert.DoesNotContain(rendered, t => t.StartsWith('[') && t.EndsWith(']'));
    }

    /// <summary>The whole point of the note: it appears when the user commits to a grant, not before. An
    /// untouched row shows nothing, and a tool with nothing to caution about shows nothing even when granted.</summary>
    [Fact]
    public void TheCautionLine_AppearsOnlyOnceAGrantIsTicked_AndOnlyForACautionedTool()
    {
        TextBlock? untouched = null;
        TextBlock? grantedForSession = null;
        TextBlock? grantedAlways = null;
        TextBlock? benignGranted = null;
        WpfStaHost.Run(() =>
        {
            untouched = CautionLine(Row("delete_file"));
            grantedForSession = CautionLine(Granted("delete_file", session: true));
            grantedAlways = CautionLine(Granted("delete_file", session: false));
            benignGranted = CautionLine(Granted("send_email", session: false));
            return 0;
        });
        WpfStaHost.Pump();

        var observed = WpfStaHost.Run(() => (
            untouched!.Visibility, grantedForSession!.Visibility,
            grantedAlways!.Visibility, benignGranted!.Visibility));

        Assert.Equal(Visibility.Collapsed, observed.Item1);
        Assert.Equal(Visibility.Visible, observed.Item2);
        // Either tier raises it — "Always" alone is the tick a user makes on a tool no session grant covers.
        Assert.Equal(Visibility.Visible, observed.Item3);
        Assert.Equal(Visibility.Collapsed, observed.Item4);
    }

    private static ToolCatalogRow Granted(string toolName, bool session)
    {
        var row = Row(toolName);
        if (session) row.AllowedForSession = true;
        else row.AllowedAlways = true;
        return row;
    }

    private static TextBlock CautionLine(ToolCatalogRow row)
    {
        var realized = RealizeRow(new Pia.Views.SettingsViews.AssistantView());
        realized.DataContext = row;
        return FindElements<TextBlock>(realized)
            .Single(tb => BindingPathWalker.PathOf(tb, UIElement.VisibilityProperty) == "HasCaution");
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

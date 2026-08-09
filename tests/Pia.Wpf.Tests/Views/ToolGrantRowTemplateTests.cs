using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The standing and session rows share one details template and differ only in their trailing button, so the
/// command each reaches through <c>AncestorType=ItemsControl</c> is what a copy-paste can silently get wrong —
/// nothing else in the suite realizes these templates.
/// </summary>
[Collection("WpfApplicationStatic")]
public class ToolGrantRowTemplateTests
{
    private const string SharedDetailsTemplateKey = "ToolGrantRowDetailsTemplate";

    private static readonly Guid PluginA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ToolGrantRow Row() =>
        new(PluginA, "Files", "write_file", new DateTimeOffset(2026, 8, 9, 10, 30, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("Grants", "DataContext.RevokeCommand")]
    [InlineData("SessionGrants", "DataContext.ForgetSessionCommand")]
    public void EachGrantRow_ReachesItsOwnCommand_AndReusesTheSharedDetailsTemplate(
        string itemsSourcePath, string expectedCommandPath)
    {
        ToolPermissionsSettingsViewModel? vm = null;
        FrameworkElement? row = null;
        Pia.Views.SettingsViews.AssistantView? view = null;
        var theRow = Row();

        WpfStaHost.Run(() =>
        {
            view = new Pia.Views.SettingsViews.AssistantView();

            // By declared ItemsSource path, never by index: the view holds several ItemsControls (ComboBox is
            // one too) and a sibling uses the same ancestor-command shape.
            var declared = BindingPathWalker.FindLogical<ItemsControl>(view!)
                .Single(ic => (BindingOperations.GetBinding(ic, ItemsControl.ItemsSourceProperty) as Binding)
                    ?.Path?.Path == itemsSourcePath);

            vm = BuildVm();
            var host = new ItemsControl { ItemTemplate = declared.ItemTemplate, DataContext = vm };
            row = (FrameworkElement)declared.ItemTemplate.LoadContent();
            row.DataContext = theRow;
            host.Items.Add(row);
            return 0;
        });
        WpfStaHost.Pump();

        var observed = WpfStaHost.Run(() =>
        {
            var button = FindElements<ButtonBase>(row!).Single();
            var presenter = FindElements<ContentPresenter>(row!).Single();
            var shared = (DataTemplate)view!.FindResource(SharedDetailsTemplateKey);
            var expected = expectedCommandPath == "DataContext.RevokeCommand"
                ? (object)vm!.RevokeCommand
                : vm!.ForgetSessionCommand;

            return (
                CommandPath: BindingPathWalker.PathOf(button, ButtonBase.CommandProperty),
                // Identity, not non-null: a non-null check cannot tell a resolved-but-wrong command from the
                // right one, and the declared path alone is blind to the AncestorType the binding walks.
                ResolvesToTheVmCommand: ReferenceEquals(button.Command, expected),
                Status: BindingOperations.GetBindingExpression(button, ButtonBase.CommandProperty) is { } be
                    ? be.Status.ToString()
                    : "<no expr>",
                // Not evidence for the ancestor technique — {Binding} needs no ancestor — but the button must
                // still deliver the clicked grant rather than the whole VM.
                PassesTheRow: ReferenceEquals(button.CommandParameter, theRow),
                UsesSharedDetails: ReferenceEquals(presenter.ContentTemplate, shared));
        });

        Assert.Equal(expectedCommandPath, observed.CommandPath);
        Assert.True(observed.ResolvesToTheVmCommand,
            $"{expectedCommandPath} did not resolve to the SAME command instance on " +
            $"ToolPermissionsSettingsViewModel (BindingExpression.Status={observed.Status}).");
        Assert.True(observed.PassesTheRow);
        Assert.True(observed.UsesSharedDetails,
            $"the {itemsSourcePath} row no longer renders the shared {SharedDetailsTemplateKey}, so the two " +
            "tiers can drift apart on everything but the button.");
    }

    [Fact]
    public void SharedDetailsTemplate_RendersTheRowsToolNameAndPlugin()
    {
        FrameworkElement? details = null;
        WpfStaHost.Run(() =>
        {
            var view = new Pia.Views.SettingsViews.AssistantView();
            var shared = (DataTemplate)view.FindResource(SharedDetailsTemplateKey);
            details = (FrameworkElement)shared.LoadContent();
            details.DataContext = Row();
            return 0;
        });
        WpfStaHost.Pump();

        // The plugin and granted-at lines are built from Runs, so TextBlock.Text is empty for those two.
        var rendered = WpfStaHost.Run(() =>
            FindElements<TextBlock>(details!).Select(tb => tb.Text)
                .Concat(FindElements<System.Windows.Documents.Run>(details!).Select(r => r.Text))
                .ToArray());

        Assert.Contains("write_file", rendered);
        Assert.Contains("Files", rendered);
        Assert.Contains(rendered, t => t.Contains("2026", StringComparison.Ordinal));
    }

    private static ToolPermissionsSettingsViewModel BuildVm() =>
        new(Substitute.For<IToolPermissionService>(),
            Substitute.For<IPluginService>(),
            NullLogger<SettingsViewModel>.Instance);

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

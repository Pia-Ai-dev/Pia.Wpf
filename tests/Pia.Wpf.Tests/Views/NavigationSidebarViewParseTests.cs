using Pia.ViewModels;
using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Wpf.Ui's <see cref="NavigationView"/> keeps its item collections off the logical tree, so the items are
/// walked by name; the sidebar's inherited <see cref="MainWindowViewModel"/> host is read, not asserted.
/// </summary>
[Collection("WpfApplicationStatic")]
public class NavigationSidebarViewParseTests
{
    /// <summary>Floors, not counts: measured 8 menu items + 2 footer items and 25 bound paths.</summary>
    private const int MinimumItems = 8;
    private const int MinimumBoundPaths = 16;

    [Fact]
    public void EveryNavigationItemsBindingPath_ResolvesOnMainWindowViewModel()
    {
        var (items, bindings) = WpfStaHost.Run(() =>
        {
            var sidebar = new Pia.Views.NavigationSidebarView();
            if (sidebar.FindName("SidebarNavigationView") is not NavigationView nav)
                return (-1, new[] { "<SidebarNavigationView not found by name>" });

            var all = (nav.MenuItems?.Cast<object>() ?? [])
                .Concat(nav.FooterMenuItems?.Cast<object>() ?? [])
                .OfType<System.Windows.DependencyObject>()
                .ToArray();

            return (all.Length,
                all.SelectMany(item => BindingPathWalker.Describe(item, typeof(MainWindowViewModel))).ToArray());
        });

        // Two non-vacuity guards: the name could stop resolving, or the collections could stop being populated.
        Assert.True(items >= MinimumItems,
            $"expected at least {MinimumItems} navigation items across MenuItems + FooterMenuItems but found " +
            $"{items}. The items are read off the NavigationView BY NAME because they are not logical " +
            "children; suspect the name, the collections, or a move to an ItemsSource.");

        Assert.True(bindings.Length >= MinimumBoundPaths,
            $"only {bindings.Length} bound paths were found across the sidebar's navigation items, which is " +
            $"below the non-vacuity floor of {MinimumBoundPaths}.");

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Views/NavigationSidebarView.xaml do not resolve to a public property on " +
            "MainWindowViewModel, so they bind to nothing and fail silently at runtime: " +
            string.Join(", ", unresolved));
    }

    /// <summary>The footer is the only place a script can reach these two, and the help item sits between the
    /// new-window and theme items on purpose — that ORDER is what users were asked about.</summary>
    [Fact]
    public void TheFooterOffersNewWindowThenHelpThenTheThemeSwitch()
    {
        var ids = WpfStaHost.Run(() =>
        {
            var sidebar = new Pia.Views.NavigationSidebarView();
            if (sidebar.FindName("SidebarNavigationView") is not NavigationView nav)
                return new[] { "<SidebarNavigationView not found by name>" };

            return (nav.FooterMenuItems?.Cast<object>() ?? [])
                .OfType<NavigationViewItem>()
                .Select(item => System.Windows.Automation.AutomationProperties.GetAutomationId(
                    (System.Windows.DependencyObject)item.Content))
                .ToArray();
        });

        Assert.Equal(["NavItem_NewWindow", "NavItem_Help", "NavItem_ThemeToggle"], ids);
    }

    [Fact]
    public void TheViewRootItself_HasNoLogicallyReachableBindings_WhichIsWhyTheFactAboveReadsTheCollections()
    {
        // Pins the premise above: a red here means Wpf.Ui started putting its items on the logical tree.
        var direct = WpfStaHost.Run(() =>
            BindingPathWalker.Describe(new Pia.Views.NavigationSidebarView(), typeof(MainWindowViewModel)));

        Assert.Empty(direct);
    }
}

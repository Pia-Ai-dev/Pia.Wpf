using Pia.ViewModels;
using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// <c>NavigationSidebarView</c>, and it is the one view in this batch a plain logical walk cannot see at all.
/// <para>
/// <b>Measured before it was designed around</b> (2026-08-02): the parsed sidebar has exactly TWO logical
/// descendants — itself and the <see cref="NavigationView"/> — and the walk finds ZERO bound paths, against
/// 34 <c>Binding</c>s in the markup. Wpf.Ui's <c>NavigationView</c> keeps its <c>MenuItems</c> and
/// <c>FooterMenuItems</c> off the logical tree, so a walk of the view root is VACUOUS rather than clean. That
/// distinction is the reason this file exists in this shape: a zero-path walk asserting "no UNRESOLVED" would
/// have shipped as green coverage of nothing.
/// </para>
/// <para>
/// So the items are reached through the collections themselves, by name, and each is walked as its own root.
/// That is sound because none of them re-roots: every item inherits the sidebar's DataContext, which is
/// <see cref="MainWindowViewModel"/>.
/// </para>
/// <para>
/// <b>The host relationship here is asserted by READING, not by execution, and this file must not imply
/// otherwise.</b> <c>MainWindow.xaml:40</c> hosts the sidebar with no <c>DataContext</c> binding, so it
/// inherits the window's, which <c>MainWindow.xaml.cs:29</c> assigns as <see cref="MainWindowViewModel"/> —
/// a code assignment inside a ctor that also takes an <c>IServiceProvider</c> and calls
/// <c>GetRequiredService</c> on it, so constructing a real <c>MainWindow</c> is out of scope for a parse
/// test. This is therefore weaker than the guard the other views in this batch carry, and saying so is the
/// honest form: a re-host of the sidebar would not red here.
/// </para>
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

        // TWO non-vacuity guards, not one, because this walk has two independent ways to see nothing: the
        // named NavigationView could stop being found, or its item collections could stop being populated at
        // parse time. Either would leave a zero-length path list that satisfies "no UNRESOLVED" trivially.
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

    [Fact]
    public void TheViewRootItself_HasNoLogicallyReachableBindings_WhichIsWhyTheFactAboveReadsTheCollections()
    {
        // Pins the PREMISE the fact above is built on, so that if Wpf.Ui ever starts putting its items on the
        // logical tree this file says so instead of quietly measuring the same thing twice. A red here is not
        // a defect — it is an invitation to simplify the fact above back to a plain Describe(sidebar, …).
        var direct = WpfStaHost.Run(() =>
            BindingPathWalker.Describe(new Pia.Views.NavigationSidebarView(), typeof(MainWindowViewModel)));

        Assert.Empty(direct);
    }
}

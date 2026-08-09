using System.Reflection;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

// Item-scoped DataTemplate/ItemTemplate content and Style DataTriggers are out of reach for this logical walk.
[Collection("WpfApplicationStatic")]
public class OptimizeViewParseTests
{
    // A non-vacuity floor, not a count: measured 8, kept well under so ordinary view edits never touch this file.
    private const int MinimumBoundPaths = 5;

    [Fact]
    public void EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt()
    {
        // Only the root TYPE is checked here; nothing opens SettingsView.xaml, so a re-host of this view is
        // invisible to this fact — ViewHostDataContextTests is the guard that reads the host site.
        var root = typeof(SettingsViewModel)
            .GetProperty(nameof(SettingsViewModel.OptimizeVm), BindingFlags.Public | BindingFlags.Instance)!
            .PropertyType;
        Assert.Equal(typeof(OptimizeSettingsViewModel), root);

        var bindings = WpfStaHost.Run(() =>
            BindingPathWalker.Describe(new Pia.Views.SettingsViews.OptimizeView(), root));

        Assert.True(bindings.Length >= MinimumBoundPaths,
            $"only {bindings.Length} bound paths were found in the parsed OptimizeView, which is below the " +
            $"non-vacuity floor of {MinimumBoundPaths}. The walk is logical, so suspect a container that no " +
            "longer reports logical children rather than a genuine removal.");

        // A cross-VM hop: ProvidersVm exposes the shared ProvidersSettingsViewModel, so the walk crosses types.
        Assert.Contains(bindings, b => b.Contains("=ProvidersVm.GoToProvidersTabCommand [OptimizeSettingsViewModel]"));
        // Templates is the templates ItemsControl's ItemsSource itself — proves the walk reached the
        // boundary right before the excluded, item-scoped DataTemplate content.
        Assert.Contains(bindings, b => b.Contains("=Templates [OptimizeSettingsViewModel]"));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Views/SettingsViews/OptimizeView.xaml do not resolve to a public " +
            "property on the ViewModel the markup roots them at, so they bind to nothing and fail silently " +
            $"at runtime: {string.Join(", ", unresolved)}");
    }
}

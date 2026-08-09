using System.Reflection;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>Out of reach for this logical walk: the provider-card <c>DataTemplate</c>'s item-scoped content and
/// the <c>RelativeSource</c> bindings inside it, all filtered out by design.</summary>
[Collection("WpfApplicationStatic")]
public class ProvidersViewParseTests
{
    /// <summary>A floor, not a count: 12 were measured, and the slack keeps ordinary view edits out of this
    /// file while still catching a container that stops reporting logical children.</summary>
    private const int MinimumBoundPaths = 8;

    [Fact]
    public void EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt()
    {
        // Only the root TYPE: nothing here opens SettingsView.xaml, so a re-host of the view is invisible to
        // this fact and ViewHostDataContextTests is the guard that reads the host site.
        var root = typeof(SettingsViewModel)
            .GetProperty(nameof(SettingsViewModel.ProvidersVm), BindingFlags.Public | BindingFlags.Instance)!
            .PropertyType;
        Assert.Equal(typeof(ProvidersSettingsViewModel), root);

        var bindings = WpfStaHost.Run(() =>
            BindingPathWalker.Describe(new Pia.Views.SettingsViews.ProvidersView(), root));

        Assert.True(bindings.Length >= MinimumBoundPaths,
            $"only {bindings.Length} bound paths were found in the parsed ProvidersView, which is below the " +
            $"non-vacuity floor of {MinimumBoundPaths}. The walk is logical, so suspect a container that no " +
            "longer reports logical children rather than a genuine removal.");

        // A cross-tab hop: it switches the parent SettingsViewModel's tab index.
        Assert.Contains(bindings, b => b.Contains("=GoToCloudSyncCommand [ProvidersSettingsViewModel]"));
        // The ItemsSource itself, so the walk reached the boundary right before the excluded template content.
        Assert.Contains(bindings, b => b.Contains("=ProviderDisplayItems [ProvidersSettingsViewModel]"));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Views/SettingsViews/ProvidersView.xaml do not resolve to a public " +
            "property on the ViewModel the markup roots them at, so they bind to nothing and fail silently " +
            $"at runtime: {string.Join(", ", unresolved)}");
    }
}

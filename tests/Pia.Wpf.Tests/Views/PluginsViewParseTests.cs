using System.Reflection;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>Most of this view's bindings sit inside the plugin-row <c>ItemTemplate</c>, item-scoped and invisible to a logical walk.</summary>
[Collection("WpfApplicationStatic")]
public class PluginsViewParseTests
{
    /// <summary>A floor, not a count: deliberately under the 6 measured, so ordinary view edits never touch this file.</summary>
    private const int MinimumBoundPaths = 4;

    [Fact]
    public void EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt()
    {
        var root = typeof(SettingsViewModel)
            .GetProperty(nameof(SettingsViewModel.PluginsVm), BindingFlags.Public | BindingFlags.Instance)!
            .PropertyType;
        Assert.Equal(typeof(PluginsSettingsViewModel), root);

        var bindings = WpfStaHost.Run(() =>
            BindingPathWalker.Describe(new Pia.Views.SettingsViews.PluginsView(), root));

        Assert.True(bindings.Length >= MinimumBoundPaths,
            $"only {bindings.Length} bound paths were found in the parsed PluginsView, which is below the " +
            $"non-vacuity floor of {MinimumBoundPaths}. The walk is logical, so suspect a container that no " +
            "longer reports logical children rather than a genuine removal.");

        Assert.Contains(bindings, b => b.Contains("=GoToAccountCommand [PluginsSettingsViewModel]"));
        Assert.Contains(bindings, b => b.Contains("=Plugins [PluginsSettingsViewModel]"));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Views/SettingsViews/PluginsView.xaml do not resolve to a public property " +
            "on the ViewModel the markup roots them at, so they bind to nothing and fail silently at " +
            $"runtime: {string.Join(", ", unresolved)}");
    }
}

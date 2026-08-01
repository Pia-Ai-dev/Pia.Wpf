using System.Reflection;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// <c>Pia.Views.SettingsViews.PluginsView</c> — hosted at <c>SettingsView.xaml:149</c> with
/// <c>DataContext="{Binding PluginsVm}"</c> — had no test parsing it before Batch 14 G4 (fifth of five,
/// impl spec §8).
/// <para>
/// The weakest floor in the batch — say so plainly rather than let a small number read as meaningful. 13 of
/// this file's 19 <c>Binding</c>s live inside the plugin-row <c>ItemsControl.ItemTemplate</c>
/// (<c>:56</c>–<c>:135</c>), item-scoped and therefore invisible to a logical walk:
/// <c>Image.Source=IconImage</c> (<c>:76</c>), <c>Image.Visibility=HasIcon</c> (<c>:79</c>),
/// <c>SymbolIcon.Symbol=FallbackIcon</c> (<c>:80</c>), <c>SymbolIcon.Visibility=HasIcon</c> (<c>:83</c>),
/// <c>TextBlock.Text=Name</c>/<c>KindBadge</c>/<c>Version</c>/<c>Description</c>/<c>StatusText</c>,
/// <c>ProgressRing.Visibility=IsActivating</c>, <c>ToggleSwitch.IsChecked=IsEnabled</c> (<c>:129</c>), and the
/// row's own <c>Command="{Binding DataContext.TogglePluginCommand, RelativeSource={RelativeSource
/// AncestorType=ItemsControl}}"</c> (<c>:130</c>) plus its <c>CommandParameter="{Binding}"</c> sibling
/// (<c>:131</c>) — the first filtered by <see cref="BindingPathWalker.TargetsDataContext"/> (RelativeSource),
/// the second because an empty path is not a path at all. (The impl spec's table cites this range as
/// <c>:76</c>–<c>:127</c> with the RelativeSource at <c>:127</c>; the real file has it one Border-close later,
/// at <c>:76</c>–<c>:131</c> with the RelativeSource <c>Command</c> at <c>:130</c> and
/// <c>CommandParameter</c> at <c>:131</c> — the same class of off-by-a-few the OptimizeView commit recorded
/// for its own template range.)
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class PluginsViewParseTests
{
    /// <summary>
    /// A floor, not a count: measured 6 at authoring time (matches the impl spec's static expectation for
    /// this view exactly — every one of the 6 resolved "ok", so this is D3's "regression protection, not
    /// bug-finder" outcome, no defect found). The impl spec calls this the weakest floor in the batch;
    /// deliberately well under the measured count so ordinary edits to the view never touch this file, but a
    /// genuine collapse (a container that stops reporting logical children) is still caught long before this
    /// line is reached.
    /// </summary>
    private const int MinimumBoundPaths = 4;

    [Fact]
    public void EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt()
    {
        // The root DataContext is CHECKED, not assumed: SettingsView.xaml:149 hosts this view with
        // DataContext="{Binding PluginsVm}", so the walk below is only sound while that property still has
        // this type.
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

        // GoToAccountCommand (the not-connected overlay's button) is a single-occurrence anchor and the
        // impl spec's own named plausible first red for this file.
        Assert.Contains(bindings, b => b.Contains("=GoToAccountCommand [PluginsSettingsViewModel]"));
        // Plugins is the plugin-list ItemsControl's ItemsSource itself — proves the walk reached the
        // boundary right before the excluded, item-scoped DataTemplate content.
        Assert.Contains(bindings, b => b.Contains("=Plugins [PluginsSettingsViewModel]"));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Views/SettingsViews/PluginsView.xaml do not resolve to a public property " +
            "on the ViewModel the markup roots them at, so they bind to nothing and fail silently at " +
            $"runtime: {string.Join(", ", unresolved)}");
    }
}

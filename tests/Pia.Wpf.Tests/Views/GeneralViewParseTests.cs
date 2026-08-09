using System.Reflection;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

// The Privacy tab re-roots its DataContext onto PrivacyVm, and a null re-root reports every descendant as
// UNRESOLVED — so if PrivacyVm stops resolving, one defect shows up as seven lines. Fix the re-root first.
[Collection("WpfApplicationStatic")]
public class GeneralViewParseTests
{
    // A floor, not a count, set well under the measured total so ordinary edits to the view never touch this file.
    private const int MinimumBoundPaths = 26;

    [Fact]
    public void EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt()
    {
        // Only the root TYPE is checked here: nothing opens SettingsView.xaml, so a RE-HOST onto another property
        // is invisible to this fact and covered by ViewHostDataContextTests instead.
        var root = typeof(SettingsViewModel)
            .GetProperty(nameof(SettingsViewModel.GeneralVm), BindingFlags.Public | BindingFlags.Instance)!
            .PropertyType;
        Assert.Equal(typeof(GeneralSettingsViewModel), root);

        var bindings = WpfStaHost.Run(() =>
            BindingPathWalker.Describe(new Pia.Views.SettingsViews.GeneralView(), root));

        Assert.True(bindings.Length >= MinimumBoundPaths,
            $"only {bindings.Length} bound paths were found in the parsed GeneralView, which is below the " +
            $"non-vacuity floor of {MinimumBoundPaths}. The walk is logical, so suspect a container that no " +
            "longer reports logical children rather than a genuine removal.");

        // Two halves: the re-root binding itself, and a PATH tagged [PrivacySettingsViewModel] — without the
        // second, a re-root that silently stopped being walked would still satisfy the first.
        Assert.Contains(bindings, b => b.Contains("=PrivacyVm "));
        Assert.Contains(bindings, b => b.Contains("=TokenizationEnabled [PrivacySettingsViewModel]"));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Views/SettingsViews/GeneralView.xaml do not resolve to a public property " +
            "on the ViewModel the markup roots them at, so they bind to nothing and fail silently at " +
            $"runtime: {string.Join(", ", unresolved)}");
    }
}

using System.Reflection;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// <c>Pia.Views.SettingsViews.GeneralView</c> — hosted at <c>SettingsView.xaml:123</c> with
/// <c>DataContext="{Binding GeneralVm}"</c> — had no test parsing it before Batch 14 G4. Everything in the
/// view sits inside a <c>TabControl</c> (<c>GeneralView.xaml:17</c>) across four <c>TabItem</c>s (<c>:19</c>,
/// <c>:97</c>, <c>:230</c>, <c>:451</c>); <c>TabItem</c> reachability by a LOGICAL walk is proven, not
/// assumed — <see cref="SettingsAssistantViewParseTests"/>'s green fact already asserts two paths that live
/// inside <c>TabItem</c>s.
/// <para>
/// <b>This is the batch's only view with an internal re-root</b>: the Privacy tab's <c>ScrollViewer</c>
/// (<c>:452</c>) sets <c>DataContext="{Binding PrivacyVm}"</c>, switching the effective ViewModel from
/// <see cref="GeneralSettingsViewModel"/> to <c>PrivacySettingsViewModel</c> for its six descendant paths.
/// The two-halves assertion below is mandatory for that reason: asserting only the re-root path
/// (<c>=PrivacyVm</c>) would still pass if the walk stopped following it, so a path actually tagged
/// <c>[PrivacySettingsViewModel]</c> is asserted too. <b>Read a future failure here with the walker's known
/// false comment in mind</b> (<c>BindingPathWalker.Walk</c>'s doc, and W2 in the Batch 14 impl spec): a null
/// re-root reports descendants as <c>UNRESOLVED</c>, not "unknown", so if <c>PrivacyVm</c> itself ever
/// stopped resolving this file would report ONE defect as seven <c>UNRESOLVED</c> lines (the re-root plus
/// all six paths under it) — fix the re-root first, then re-run before touching any of the six.
/// </para>
/// <para>
/// Out of reach for this logical walk, named so a future reader does not go looking: the two
/// <c>ItemsControl</c>s' (<c>TtsVoices</c>, <c>PiiKeywords</c>) <c>DataTemplate</c> content; four
/// <c>RelativeSource</c> command/collection bindings (<c>:392</c>, <c>:411</c>, <c>:530</c>, <c>:542</c>);
/// three <c>DataTrigger</c>s (<c>:399</c>, <c>:418</c>, <c>:431</c>). The local
/// <c>UserControl.Resources</c> converters (<c>EnumToLocalizedStringConverter</c>,
/// <c>CategoryDisplayConverter</c> among them) are load-bearing for the parse itself and must stay in this
/// file rather than move to a shared one.
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class GeneralViewParseTests
{
    /// <summary>
    /// A floor, not a count: measured 40 (34 own + 6 under the <c>PrivacyVm</c> re-root) at authoring time.
    /// Deliberately well under that so ordinary edits to the view never touch this file; a genuine collapse
    /// (a container that stops reporting logical children) is still caught long before this line is reached.
    /// </summary>
    private const int MinimumBoundPaths = 26;

    [Fact]
    public void EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt()
    {
        // The root DataContext is CHECKED, not assumed: SettingsView.xaml:123 hosts this view with
        // DataContext="{Binding GeneralVm}", so the walk below is only sound while that property still has
        // this type.
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

        // The two-halves assertion (impl spec §8, GeneralView row): the first is the PrivacyVm re-root
        // itself, the second is a PATH tagged [PrivacySettingsViewModel] (not just the tag alone, which any
        // of the six re-rooted paths would satisfy even the broken one) — it proves the walk followed the
        // re-root onto PrivacySettingsViewModel. Without the second, a re-root that silently stopped being
        // walked would still satisfy the first.
        Assert.Contains(bindings, b => b.Contains("=PrivacyVm "));
        Assert.Contains(bindings, b => b.Contains("=TokenizationEnabled [PrivacySettingsViewModel]"));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Views/SettingsViews/GeneralView.xaml do not resolve to a public property " +
            "on the ViewModel the markup roots them at, so they bind to nothing and fail silently at " +
            $"runtime: {string.Join(", ", unresolved)}");
    }
}

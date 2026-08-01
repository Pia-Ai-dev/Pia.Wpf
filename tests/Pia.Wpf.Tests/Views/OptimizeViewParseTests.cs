using System.Reflection;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// <c>Pia.Views.SettingsViews.OptimizeView</c> — hosted at <c>SettingsView.xaml:97</c> with
/// <c>DataContext="{Binding OptimizeVm}"</c> — had no test parsing it before Batch 14 G4 (fourth of five,
/// impl spec §8).
/// <para>
/// No internal re-root (contrast <c>GeneralView</c>'s <c>PrivacyVm</c>) and no nested view (contrast
/// <c>AccountView</c>'s <c>E2EEOnboardingView</c>). Its one distinctive path is a cross-VM hop:
/// <c>OptimizeView.xaml:21</c> <c>Command="{Binding ProvidersVm.GoToProvidersTabCommand}"</c> resolves
/// through <see cref="OptimizeSettingsViewModel.ProvidersVm"/> (expression-bodied, <c>=&gt; _providersVm</c>)
/// onto <see cref="ProvidersSettingsViewModel"/> — the walker follows the dotted path across both types
/// without constructing either.
/// </para>
/// <para>
/// Out of reach for this logical walk, named so a future reader does not go looking: the templates
/// <c>ItemsControl</c>'s <c>DataTemplate</c> content (<c>:112</c>–<c>:198</c>, item-scoped against
/// <c>OptimizationTemplate</c>, not <see cref="OptimizeSettingsViewModel"/>) — four
/// <c>Command="{Binding DataContext.…, RelativeSource={RelativeSource AncestorType=UserControl}}"</c>
/// bindings inside it (<c>ViewTemplatePromptCommand</c> <c>:129</c>, <c>SetDefaultTemplateCommand</c>
/// <c>:141</c>, <c>EditTemplateCommand</c> <c>:153</c>, <c>DeleteTemplateCommand</c> <c>:166</c>) plus their
/// <c>CommandParameter="{Binding}"</c> identity siblings and a <c>MultiBinding</c> gating the default badge
/// (<c>:188</c>–<c>:191</c>) — all filtered by <see cref="BindingPathWalker.TargetsDataContext"/> by design;
/// the <c>ComboBox.ItemTemplate</c> content at <c>:44</c>, unreachable for the same item-scoping reason; and
/// one <c>DataTrigger Binding="{Binding OutputAction}"</c> inside a <c>Style</c> at <c>:56</c>, invisible
/// because it is never read off the element via <c>GetLocalValueEnumerator</c>.
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class OptimizeViewParseTests
{
    /// <summary>
    /// A floor, not a count: measured 8 at authoring time (matches the impl spec's static expectation for
    /// this view exactly — every one of the 8 resolved "ok", so this is D3's "regression protection, not
    /// bug-finder" outcome, no defect found). Deliberately well under that so ordinary edits to the view
    /// never touch this file; a genuine collapse (a container that stops reporting logical children) is
    /// still caught long before this line is reached.
    /// </summary>
    private const int MinimumBoundPaths = 5;

    [Fact]
    public void EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt()
    {
        // The root TYPE is checked, not assumed — but ONLY the type: nameof makes a RENAME of
        // SettingsViewModel.OptimizeVm a compile error and the Assert.Equal below catches a RETYPE. Nothing
        // here opens SettingsView.xaml, so the host SITE (:97, DataContext="{Binding OptimizeVm}") is not
        // observed by this fact; ViewHostDataContextTests is the guard that reads it (Batch 14 review, D1,
        // where a re-host of a sibling view left 16/16 Views facts green). This walk needs both.
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

        // ProvidersVm.GoToProvidersTabCommand is a cross-VM hop (OptimizeSettingsViewModel.ProvidersVm
        // exposes the shared ProvidersSettingsViewModel) — the impl spec names it the most interesting path
        // in the file and a plausible first red (D3).
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

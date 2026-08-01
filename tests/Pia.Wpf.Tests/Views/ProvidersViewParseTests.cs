using System.Reflection;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// <c>Pia.Views.SettingsViews.ProvidersView</c> — hosted at <c>SettingsView.xaml:84</c> with
/// <c>DataContext="{Binding ProvidersVm}"</c> — had no test parsing it before Batch 14 G4 (third of five,
/// impl spec §8).
/// <para>
/// No internal re-root and no nested view here (contrast <c>GeneralView</c>'s <c>PrivacyVm</c> re-root and
/// <c>AccountView</c>'s nested <c>E2EEOnboardingView</c>) — this is the plainest of the five.
/// </para>
/// <para>
/// Out of reach for this logical walk, named so a future reader does not go looking: the provider-card
/// <c>ItemsControl</c>'s <c>DataTemplate</c> content (<c>ProvidersView.xaml:122</c>–<c>:246</c>, item-scoped
/// against <c>ProviderDisplayItem</c>, not <see cref="ProvidersSettingsViewModel"/>); three
/// <c>Command="{Binding DataContext.…, RelativeSource={RelativeSource AncestorType=UserControl}}"</c>
/// bindings inside that same template (<c>TestConnectionCommand</c> <c>:188</c>, <c>EditProviderCommand</c>
/// <c>:218</c>, <c>DeleteProviderCommand</c> <c>:230</c>) plus the sibling <c>RelativeSource</c>/
/// <c>MultiBinding</c> bindings that gate the same three actions (<c>:156</c>, <c>:166</c>, <c>:190</c>,
/// <c>:201</c>, <c>:209</c>) — all filtered by <see cref="BindingPathWalker.TargetsDataContext"/> by design,
/// the same filter that keeps <c>loc:Str</c> out of scope.
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class ProvidersViewParseTests
{
    /// <summary>
    /// A floor, not a count: measured 12 at authoring time (matches the impl spec's static expectation for
    /// this view exactly — every one of the 12 resolved "ok", so this is D3's "regression protection, not
    /// bug-finder" outcome, no defect found). Deliberately well under that so ordinary edits to the view
    /// never touch this file; a genuine collapse (a container that stops reporting logical children) is
    /// still caught long before this line is reached.
    /// </summary>
    private const int MinimumBoundPaths = 8;

    [Fact]
    public void EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt()
    {
        // The root DataContext is CHECKED, not assumed: SettingsView.xaml:84 hosts this view with
        // DataContext="{Binding ProvidersVm}", so the walk below is only sound while that property still has
        // this type.
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

        // GoToCloudSyncCommand is a cross-tab hop (ProvidersSettingsViewModel.GoToCloudSync switches the
        // parent SettingsViewModel's SelectedTabIndex) — the impl spec names it a plausible first red (D3).
        Assert.Contains(bindings, b => b.Contains("=GoToCloudSyncCommand [ProvidersSettingsViewModel]"));
        // ProviderDisplayItems is the ItemsControl's ItemsSource itself — proves the walk reached the
        // boundary right before the excluded, item-scoped DataTemplate content.
        Assert.Contains(bindings, b => b.Contains("=ProviderDisplayItems [ProvidersSettingsViewModel]"));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Views/SettingsViews/ProvidersView.xaml do not resolve to a public " +
            "property on the ViewModel the markup roots them at, so they bind to nothing and fail silently " +
            $"at runtime: {string.Join(", ", unresolved)}");
    }
}

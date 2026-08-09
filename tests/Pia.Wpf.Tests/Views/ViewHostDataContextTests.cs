using System.Windows;
using System.Windows.Controls;
using Pia.Controls.Assistant;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>A per-view parse test cannot see a RE-HOST, so this compares the <c>DataContext</c> path declared at
/// each host site with the property that test reflects its root type off.</summary>
[Collection("WpfApplicationStatic")]
public class ViewHostDataContextTests
{
    /// <summary>What the observed string carries when a host site declares no <c>DataContext</c> binding at
    /// all — distinct from a wrong path, and a red either way.</summary>
    private const string NoBinding = "<no DataContext binding>";

    /// <summary><c>nameof</c> ties every expectation to the real property, so a rename cannot leave this table
    /// stale; the host line is carried only so a failure message can point at the markup.</summary>
    private static readonly (Type View, string Path, int HostLine, string ParseTest)[] SettingsHosts =
    [
        (typeof(Pia.Views.SettingsViews.ProvidersView), nameof(SettingsViewModel.ProvidersVm), 84,
            nameof(ProvidersViewParseTests)),
        (typeof(Pia.Views.SettingsViews.OptimizeView), nameof(SettingsViewModel.OptimizeVm), 97,
            nameof(OptimizeViewParseTests)),
        (typeof(Pia.Views.SettingsViews.AssistantView), nameof(SettingsViewModel.AssistantVm), 110,
            nameof(SettingsAssistantViewParseTests)),
        (typeof(Pia.Views.SettingsViews.GeneralView), nameof(SettingsViewModel.GeneralVm), 123,
            nameof(GeneralViewParseTests)),
        (typeof(Pia.Views.SettingsViews.AccountView), nameof(SettingsViewModel.AccountVm), 136,
            nameof(AccountViewParseTests)),
        (typeof(Pia.Views.SettingsViews.PluginsView), nameof(SettingsViewModel.PluginsVm), 149,
            nameof(PluginsViewParseTests)),
    ];

    [Fact]
    public void EveryParsedView_IsHostedWithTheDataContextPathItsParseTestReflectsItsRootOff()
    {
        // SettingsView's StaticResource styles live in a dictionary App.xaml merges, so the host's Application is
        // what makes the parse resolve; AutoWireViewModel no-ops here, deferring to a Loaded that never fires.
        var observed = WpfStaHost.Run(() =>
            BindingPathWalker.FindLogical<UserControl>(new Pia.Views.SettingsView())
                .Where(child => SettingsHosts.Any(host => host.View == child.GetType()))
                .Select(child => $"{child.GetType().FullName}={PathAt(child)}")
                .ToArray());

        // NON-VACUITY: if the six children ever stop being reachable by a LOGICAL walk, every per-site check
        // below passes over nothing.
        Assert.True(observed.Length == SettingsHosts.Length,
            $"expected {SettingsHosts.Length} settings-view host sites in the parsed SettingsView but found " +
            $"{observed.Length}: {string.Join(" | ", observed)}. The walk is LOGICAL, so suspect a container " +
            "that no longer reports logical children, or a view that was removed from SettingsView.xaml, " +
            "rather than a broken assertion.");

        var rehosted = SettingsHosts
            .Where(host => Actual(observed, host.View) != host.Path)
            .Select(host =>
                $"SettingsView.xaml:{host.HostLine} hosts {host.View.Name} with " +
                $"DataContext=\"{{Binding {Actual(observed, host.View)}}}\", but {host.ParseTest} walks its " +
                $"binding paths against the type of SettingsViewModel.{host.Path}")
            .ToArray();

        Assert.True(rehosted.Length == 0,
            "these settings views are hosted on a different DataContext than the parse test that walks them " +
            "reflects its root type off, so every binding path in those views is dead at runtime while the " +
            $"walk still resolves against the old type (Batch 14 review, D1): {string.Join("; ", rehosted)}");

        // The run panel's host is the CHAT AssistantView — a different type from the settings one above that
        // shares a file name. Folding the count into the compared string keeps one message for every failure.
        var panelPath = WpfStaHost.Run(() =>
        {
            var panels = BindingPathWalker.FindLogical<RunProgressPanel>(new Pia.Views.AssistantView()).ToArray();
            return panels.Length == 1
                ? PathAt(panels[0])
                : $"<{panels.Length} RunProgressPanel(s) in the logical tree, expected exactly 1>";
        });

        Assert.True(panelPath == nameof(AssistantViewModel.ActiveRunProgress),
            $"AssistantView.xaml:51 hosts RunProgressPanel with DataContext=\"{{Binding {panelPath}}}\", but " +
            $"{nameof(RunProgressPanelParseTests)} walks the panel's binding paths against the type of " +
            $"AssistantViewModel.{nameof(AssistantViewModel.ActiveRunProgress)} — so EVERY ONE of the panel's " +
            "bound paths would be dead at runtime while that walk stayed green (Batch 14 review, D1). " +
            "(Batch 08 F21: the count is deliberately not spelled out here — it was written as 28 when this " +
            "fact landed and the batch has since raised the walk past that, so a literal drifts silently " +
            "while the claim it decorates stays true.)");
    }

    /// <summary>The DataContext path declared at one host site, or <see cref="NoBinding"/>.</summary>
    private static string PathAt(DependencyObject child) =>
        BindingPathWalker.BoundPath(child, FrameworkElement.DataContextProperty) ?? NoBinding;

    /// <summary>The observed path for one view type, read by TYPE rather than by position in the walk —
    /// logical-walk order is a measured property of the markup, never something to assert against.</summary>
    private static string Actual(string[] observed, Type view)
    {
        var prefix = $"{view.FullName}=";
        var hit = observed.FirstOrDefault(o => o.StartsWith(prefix, StringComparison.Ordinal));
        return hit is null ? "<not hosted>" : hit[prefix.Length..];
    }
}

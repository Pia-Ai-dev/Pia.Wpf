using System.Windows;
using System.Windows.Controls;
using Pia.Controls.Assistant;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The one fact in this repo that opens a HOST view's markup — and the guard every binding-path walk in this
/// folder silently depends on.
/// <para>
/// Each per-view parse fact reflects its root ViewModel type off the property the host site binds
/// <c>DataContext</c> to (<c>typeof(SettingsViewModel).GetProperty(nameof(SettingsViewModel.GeneralVm))
/// .PropertyType</c>, and so on for the rest). That catches a RENAME of the property — <c>nameof</c> stops
/// compiling — and a RETYPE, via the <c>Assert.Equal</c> next to it. It was also long documented as catching
/// a RE-HOST. <b>It does not, and the Batch 14 review proved that by execution</b> (D1, 2026-08-01): change
/// <c>SettingsView.xaml:123</c> from <c>DataContext="{Binding GeneralVm}"</c> to <c>{Binding AccountVm}</c>
/// and all 40 of GeneralView's binding paths are dead at runtime — the tab renders empty controls — while
/// every one of the Views facts stays GREEN at 0 warnings, because nothing in the suite ever read the host
/// site.
/// </para>
/// <para>
/// This fact closes that hole for all seven walks: it constructs the real hosts, reads the
/// <c>DataContext</c> binding declared at each host site out of the parsed tree, and compares its PATH to
/// the property name the corresponding parse test reflects off. <b>The two count checks are the non-vacuity
/// guard</b>, because this is the same trap in a new place: a logical walk that reached none of the six
/// children would satisfy a per-site loop vacuously, so the six host sites and the one panel must be FOUND,
/// not merely left uncontradicted.
/// </para>
/// <para>
/// No ViewModel, no <c>DataContext</c>, no <c>Pump()</c>, no frame: a <c>{Binding}</c> written in XAML is a
/// <c>BindingExpression</c> local value the moment the parse completes, which is the same property the seven
/// walks already rely on. What this does NOT check is the reverse direction — that a view is hosted at all,
/// or hosted only once; it checks that wherever these views ARE hosted, the DataContext path is the one
/// their parse tests assume.
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class ViewHostDataContextTests
{
    /// <summary>What the observed string carries when a host site declares no <c>DataContext</c> binding at
    /// all — distinct from a wrong path, and a red either way.</summary>
    private const string NoBinding = "<no DataContext binding>";

    /// <summary>
    /// The six settings views <c>SettingsView.xaml</c> instantiates, each paired with the
    /// <see cref="SettingsViewModel"/> property its host site must bind <c>DataContext</c> to — which is
    /// exactly the property that view's parse test reflects its root type off. <c>nameof</c> ties every
    /// expectation to the real property, so a rename cannot leave this table quietly stale; the host line is
    /// carried only so a failure message can point at the markup.
    /// </summary>
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
        // SettingsView has never been constructed by any test before this one. Its code-behind is a bare
        // InitializeComponent(); its two StaticResource styles (PiaSettingsPageTitleStyle,
        // PiaSettingsSidebarItemStyle) live in Resources/Theme/PiaStyles.xaml, which App.xaml merges, so the
        // host's Application is what makes the parse resolve; and nav:ViewModelLocator.AutoWireViewModel
        // no-ops here (no Window, no service provider, so it only defers to a Loaded that never fires).
        var observed = WpfStaHost.Run(() =>
            BindingPathWalker.FindLogical<UserControl>(new Pia.Views.SettingsView())
                .Where(child => SettingsHosts.Any(host => host.View == child.GetType()))
                .Select(child => $"{child.GetType().FullName}={PathAt(child)}")
                .ToArray());

        // NON-VACUITY, and the whole reason this is not a per-site loop on its own: if the six children ever
        // stop being reachable by a LOGICAL walk (a TabControl with a templated ContentTemplate would do it),
        // every per-site check below passes over nothing.
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

        // The run panel's host is the CHAT AssistantView (Pia.Views.AssistantView) — a different type from
        // the settings one above that shares a file name. Folding the count into the compared string keeps
        // one message for "not found", "found twice" and "wrong path" alike.
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

using System.Reflection;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// <c>Pia.Views.SettingsViews.AccountView</c> — hosted at <c>SettingsView.xaml:136</c> with
/// <c>DataContext="{Binding AccountVm}"</c> — had no test parsing it before Batch 14 G4.
/// <para>
/// <c>E2EEOnboardingView</c> is instantiated at <c>AccountView.xaml:218</c> as a plain logical child with
/// <b>no <c>DataContext</c> of its own</b>, so its 15 <c>OnboardingViewModel.</c>-prefixed paths are walked
/// under <see cref="AccountSettingsViewModel"/> as the effective context — which is correct, because
/// <c>AccountSettingsViewModel.OnboardingViewModel</c> really is an <c>E2EEOnboardingViewModel</c>. The
/// anchor below is what proves the nested view was actually reached rather than skipped.
/// </para>
/// <para>
/// Known gaps, named so a future reader does not go looking: four <c>&lt;Condition Binding="…"&gt;</c>
/// inside two <c>MultiDataTrigger</c>s (<c>:86</c>, <c>:87</c>, <c>:230</c>, <c>:231</c>) live inside a
/// <c>Style.Triggers</c> resource, not on the element itself, so a logical walk never sees them.
/// <c>x:Name="LoginPasswordBox"</c> (<c>:51</c>) is driven entirely from code-behind
/// (<c>AccountView.xaml.cs</c>'s <c>PasswordChanged</c> handler writes
/// <see cref="AccountSettingsViewModel.LoginPassword"/> directly) — no <c>Binding</c> touches it at all, so
/// it is permanently invisible to this technique, not merely out of reach today.
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class AccountViewParseTests
{
    /// <summary>
    /// A floor, not a count: measured 61 (46 own + 15 under the nested, DataContext-less
    /// <c>E2EEOnboardingView</c>) at authoring time. Deliberately well under that so ordinary edits to the
    /// view never touch this file; a genuine collapse (a container that stops reporting logical children) is
    /// still caught long before this line is reached.
    /// </summary>
    private const int MinimumBoundPaths = 40;

    [Fact]
    public void EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt()
    {
        // The root TYPE is checked, not assumed — but ONLY the type: nameof makes a RENAME of
        // SettingsViewModel.AccountVm a compile error and the Assert.Equal below catches a RETYPE. Nothing
        // here opens SettingsView.xaml, so the host SITE (:136, DataContext="{Binding AccountVm}") is not
        // observed by this fact; ViewHostDataContextTests is the guard that reads it (Batch 14 review, D1,
        // where a re-host of a sibling view left 16/16 Views facts green). This walk needs both.
        var root = typeof(SettingsViewModel)
            .GetProperty(nameof(SettingsViewModel.AccountVm), BindingFlags.Public | BindingFlags.Instance)!
            .PropertyType;
        Assert.Equal(typeof(AccountSettingsViewModel), root);

        var bindings = WpfStaHost.Run(() =>
            BindingPathWalker.Describe(new Pia.Views.SettingsViews.AccountView(), root));

        Assert.True(bindings.Length >= MinimumBoundPaths,
            $"only {bindings.Length} bound paths were found in the parsed AccountView, which is below the " +
            $"non-vacuity floor of {MinimumBoundPaths}. The walk is logical, so suspect a container that no " +
            "longer reports logical children rather than a genuine removal.");

        // Proves the nested E2EEOnboardingView (no DataContext of its own, AccountView.xaml:218) was
        // actually walked, not skipped: this path only resolves against AccountSettingsViewModel if the
        // walker correctly treated the child's inherited context as the parent's.
        Assert.Contains(bindings,
            b => b.Contains("=OnboardingViewModel.RecoveryCodeInput [AccountSettingsViewModel]"));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Views/SettingsViews/AccountView.xaml (including its nested " +
            "E2EEOnboardingView) do not resolve to a public property on the ViewModel the markup roots " +
            $"them at, so they bind to nothing and fail silently at runtime: {string.Join(", ", unresolved)}");
    }

    /// <summary>
    /// D5: <c>E2EEOnboardingView.xaml</c> is instantiated at <c>AccountView.xaml:218</c> and
    /// <c>WizardSteps/AccountSetupStep.xaml:269</c> with NO <c>DataContext</c> at either site, and every one
    /// of its 15 bindings is prefixed <c>"OnboardingViewModel."</c>. It is written against whatever host
    /// DataContext happens to expose a member of that name — nothing, no interface, no base class, enforces
    /// it. Renaming <see cref="AccountSettingsViewModel"/>'s property would break the settings page while
    /// <see cref="FirstRunWizardViewModel"/>'s wizard kept working, silently, because they are two
    /// independent declarations of the same duck-typed contract.
    /// <para>
    /// Batch 14 review D4: <c>GetProperty(..., BindingFlags.Public | BindingFlags.Instance)</c> only returns
    /// <c>null</c> if the member stopped being an instance property under this exact name (a property→field
    /// or instance→static conversion) — a rename is already a compile error via <c>nameof</c>, one step
    /// earlier than this fact could ever run. So the two assertions below read <c>?.PropertyType</c> rather
    /// than asserting <c>NotNull</c> first: that failure mode now fails these two directly (with a message
    /// naming the expected type), instead of a separate near-tautological pair that could only ever fire on
    /// that one narrow conversion.
    /// </para>
    /// <para>
    /// RED DEMO (Batch 14 D4-fix, reverted): retyped <c>FirstRunWizardViewModel.OnboardingViewModel</c>
    /// (<c>:115</c>) from <c>E2EEOnboardingViewModel</c> to <c>object</c>, cast at its one in-file usage
    /// (<c>:269</c>, <c>((E2EEOnboardingViewModel)OnboardingViewModel).OnboardingCompleted += …</c>) so the
    /// solution still compiles, full <c>-t:Rebuild</c> (0 Warning(s)/0 Error(s)). Failed with
    /// <c>Assert.Equal() Failure: Expected: E2EEOnboardingViewModel / Actual: Object</c> at the second
    /// assertion below — the first (pinned to <see cref="E2EEOnboardingViewModel"/> via the settings host,
    /// which was untouched) stayed green, proving the concrete-type pin and the cross-host comparison are two
    /// independent checks, not one duplicated. Both lines reverted; <c>git diff --stat -- src/</c> came back
    /// empty afterward.
    /// </para>
    /// </summary>
    [Fact]
    public void E2EEOnboardingHosts_AllExposeAnOnboardingViewModelOfTheSameType()
    {
        var settingsHostProperty = typeof(AccountSettingsViewModel)
            .GetProperty(nameof(AccountSettingsViewModel.OnboardingViewModel), BindingFlags.Public | BindingFlags.Instance);
        var wizardHostProperty = typeof(FirstRunWizardViewModel)
            .GetProperty(nameof(FirstRunWizardViewModel.OnboardingViewModel), BindingFlags.Public | BindingFlags.Instance);

        // Anchored to a CONCRETE type, not just to each other: comparing the two reflected types only to
        // one another would pass even if both were retyped to something degenerate (e.g. object) while the
        // 15 OnboardingViewModel.-prefixed paths in E2EEOnboardingView.xaml stopped resolving on either
        // host — the same "assertion satisfied by a degenerate state" hazard the impl spec calls out
        // elsewhere. Pin one side to the real type, then assert the two agree. `?.PropertyType` (not `!.`)
        // so a null property fails THIS comparison directly rather than needing a separate NotNull first.
        Assert.Equal(typeof(E2EEOnboardingViewModel), settingsHostProperty?.PropertyType);
        Assert.Equal(settingsHostProperty?.PropertyType, wizardHostProperty?.PropertyType);
    }
}

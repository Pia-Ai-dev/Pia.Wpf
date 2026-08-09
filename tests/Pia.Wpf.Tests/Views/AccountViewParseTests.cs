using System.Reflection;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>A logical walk, so Style.Triggers conditions and the code-behind-driven LoginPasswordBox stay invisible to it.</summary>
[Collection("WpfApplicationStatic")]
public class AccountViewParseTests
{
    /// <summary>A floor, not a count: measured 61, set well below it so ordinary view edits never touch this file.</summary>
    private const int MinimumBoundPaths = 40;

    [Fact]
    public void EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt()
    {
        // Only the root TYPE is checked. Nothing here opens SettingsView.xaml, so the host site goes
        // unobserved by this fact — ViewHostDataContextTests is the guard that reads it.
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

        // Proves the nested, DataContext-less E2EEOnboardingView was walked: this path only resolves if the
        // walker treated the child's inherited context as the parent's.
        Assert.Contains(bindings,
            b => b.Contains("=OnboardingViewModel.RecoveryCodeInput [AccountSettingsViewModel]"));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Views/SettingsViews/AccountView.xaml (including its nested " +
            "E2EEOnboardingView) do not resolve to a public property on the ViewModel the markup roots " +
            $"them at, so they bind to nothing and fail silently at runtime: {string.Join(", ", unresolved)}");
    }

    /// <summary>Two hosts declare the same duck-typed <c>OnboardingViewModel</c> contract, and nothing but this keeps them in step.</summary>
    [Fact]
    public void E2EEOnboardingHosts_AllExposeAnOnboardingViewModelOfTheSameType()
    {
        var settingsHostProperty = typeof(AccountSettingsViewModel)
            .GetProperty(nameof(AccountSettingsViewModel.OnboardingViewModel), BindingFlags.Public | BindingFlags.Instance);
        var wizardHostProperty = typeof(FirstRunWizardViewModel)
            .GetProperty(nameof(FirstRunWizardViewModel.OnboardingViewModel), BindingFlags.Public | BindingFlags.Instance);

        // One side is pinned to the concrete type first: comparing the two reflected types only to each
        // other would still pass with both retyped to object.
        Assert.Equal(typeof(E2EEOnboardingViewModel), settingsHostProperty?.PropertyType);
        Assert.Equal(settingsHostProperty?.PropertyType, wizardHostProperty?.PropertyType);
    }
}

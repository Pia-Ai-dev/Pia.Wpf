using System.Windows;
using System.Windows.Controls;
using Pia.Views.WizardSteps;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>Sign-in feedback has to render whether or not the business-profile panel is showing, and only the
/// nesting says so — every Visibility involved defaults to what a collapsed panel would report too.</summary>
[Collection("WpfApplicationStatic")]
public class AccountSetupStepNestingTests
{
    private const string BusinessProfileGate = "RequiresBusinessProfile";

    [Fact]
    public void TheLoginErrorText_SitsOutsideTheBusinessProfilePanel()
    {
        var gating = WpfStaHost.Run(() => GatingAncestors(FindLoginErrorText));

        Assert.True(gating.Length == 0,
            $"the TextBlock bound to LoginError is nested inside {string.Join(", ", gating)}, whose Visibility " +
            $"binds to {BusinessProfileGate}. A failed sign-in then renders nothing at all unless the server " +
            "happens to be asking for a business profile.");
    }

    [Fact]
    public void TheSignInSpinner_SitsOutsideTheBusinessProfilePanel()
    {
        var gating = WpfStaHost.Run(() => GatingAncestors(FindSignInSpinner));

        Assert.True(gating.Length == 0,
            $"the sign-in ProgressRing is nested inside {string.Join(", ", gating)}, whose Visibility binds to " +
            $"{BusinessProfileGate}. Signing in then shows no progress at all unless the server happens to be " +
            "asking for a business profile.");
    }

    private static string[] GatingAncestors(Func<AccountSetupStep, FrameworkElement> locate)
    {
        // No DataContext and no pump: the bindings are read, never evaluated, so this cannot pass by observing
        // the Visibility default that the broken nesting reports just as happily as the correct one.
        var step = new AccountSetupStep();

        var gates = OwnMarkup(step).Where(IsBusinessProfileGate).ToArray();
        Assert.True(gates.Length == 1,
            $"expected exactly one element in AccountSetupStep whose Visibility binds to {BusinessProfileGate}, " +
            $"found {gates.Length}. Until that panel is locatable by its binding again, this test proves nothing " +
            "about where the sign-in feedback sits.");

        return [.. Ancestors(locate(step))
            .OfType<FrameworkElement>()
            .Where(IsBusinessProfileGate)
            .Select(e => e.GetType().Name)];
    }

    private static FrameworkElement FindLoginErrorText(AccountSetupStep step)
    {
        var found = OwnMarkup(step).OfType<TextBlock>()
            .Where(t => BindingPathWalker.PathOf(t, TextBlock.TextProperty) == "LoginError")
            .ToArray();

        Assert.True(found.Length == 1,
            $"expected exactly one TextBlock in AccountSetupStep bound to LoginError, found {found.Length}. " +
            "Without it there is nothing whose ancestors can be checked.");

        return found[0];
    }

    private static FrameworkElement FindSignInSpinner(AccountSetupStep step)
    {
        var found = OwnMarkup(step).OfType<Wpf.Ui.Controls.ProgressRing>().ToArray();

        Assert.True(found.Length == 1,
            $"expected exactly one ProgressRing in AccountSetupStep's own markup, found {found.Length}. " +
            "Without it there is nothing whose ancestors can be checked.");

        return found[0];
    }

    private static bool IsBusinessProfileGate(FrameworkElement element) =>
        BindingPathWalker.PathOf(element, UIElement.VisibilityProperty) == BusinessProfileGate;

    /// <summary>Nested views are excluded because they declare spinners of their own.</summary>
    private static FrameworkElement[] OwnMarkup(AccountSetupStep step) =>
        [.. BindingPathWalker.FindLogical<FrameworkElement>(step)
            .Where(e => !Ancestors(e).Any(a => a is UserControl && !ReferenceEquals(a, step)))];

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var parent = LogicalTreeHelper.GetParent(element); parent is not null;
             parent = LogicalTreeHelper.GetParent(parent))
            yield return parent;
    }
}

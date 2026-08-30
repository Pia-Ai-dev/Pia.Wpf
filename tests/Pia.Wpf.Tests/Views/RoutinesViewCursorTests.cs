using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// WPF-UI ships its buttons on the arrow cursor, so the routine actions read as unclickable next to the
/// Hand-cursored rows. Only a realized tree can show the implicit style actually reached them.
/// </summary>
[Collection("WpfApplicationStatic")]
public class RoutinesViewCursorTests
{
    [Theory]
    [InlineData("Routines_NewJob")]
    [InlineData("Routines_BrowseCatalog")]
    [InlineData("Routines_StartBlank")]
    [InlineData("Routines_CatalogClose")]
    [InlineData("Routines_Edit")]
    [InlineData("Routines_Toggle")]
    [InlineData("Routines_RunNow")]
    [InlineData("Routines_Delete")]
    [InlineData("Routines_Save")]
    [InlineData("Routines_Cancel")]
    public void EveryActionButton_ShowsTheHandCursor(string automationId) =>
        Assert.Equal(Cursors.Hand, WpfStaHost.Run(() => ActionButton(automationId).Cursor));

    /// <summary>Run now is the one that greys out, and a Hand over a dead button is the same lie in reverse.</summary>
    [Fact]
    public void ADisabledActionButton_FallsBackToTheArrow() =>
        Assert.Equal(Cursors.Arrow, WpfStaHost.Run(() =>
        {
            var button = ActionButton("Routines_RunNow");
            button.IsEnabled = false;
            return button.Cursor;
        }));

    private static Button ActionButton(string automationId) =>
        Find(new Pia.Views.RoutinesView(), automationId) as Button
        ?? throw new InvalidOperationException($"RoutinesView declares no ui:Button {automationId} any more");

    private static FrameworkElement? Find(DependencyObject element, string automationId)
    {
        if (element is FrameworkElement candidate
            && (string?)candidate.GetValue(AutomationProperties.AutomationIdProperty) == automationId)
            return candidate;

        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
        {
            if (Find(child, automationId) is { } found)
                return found;
        }

        return null;
    }
}

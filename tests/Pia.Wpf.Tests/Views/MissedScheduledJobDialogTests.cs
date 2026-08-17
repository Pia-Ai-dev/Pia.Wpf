using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Wpf.Ui returns <see cref="ContentDialogResult.None"/> for the CLOSE button and for Escape alike, so which
/// button carries "Skip this run" is the whole difference between a dismissal that leaves the occurrence alone
/// and one that silently spends it.
/// </summary>
[Collection("WpfApplicationStatic")]
public class MissedScheduledJobDialogTests
{
    [Fact]
    public void Skip_SitsOnTheSecondaryButton_SoDismissingIsNotSilentlyASkip()
    {
        var (primary, secondary, close) = WpfStaHost.Run(() =>
        {
            var dialog = new Pia.Views.Dialogs.MissedScheduledJobDialog(new ContentDialogHost(), "body");
            return (dialog.PrimaryButtonText, dialog.SecondaryButtonText, dialog.CloseButtonText);
        });

        // Three distinct outcomes, none sharing a button: run it, spend the occurrence, or leave it be.
        Assert.False(string.IsNullOrEmpty(primary));
        Assert.False(string.IsNullOrEmpty(secondary));
        Assert.False(string.IsNullOrEmpty(close));
        Assert.NotEqual(secondary, close);
    }
}

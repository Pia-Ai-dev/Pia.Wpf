using System.Windows.Controls;
using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>The dialog binds to a private nested view model, and a binding that stops resolving fails
/// silently — an empty title and a tick that never reaches the caller.</summary>
[Collection("WpfApplicationStatic")]
public class OptOutConfirmContentDialogTests
{
    [Fact]
    public void TheCallersStrings_ReachTheDialog()
    {
        var (title, primary, close, message) = WpfStaHost.Run(() =>
        {
            var dialog = new Pia.Views.Dialogs.OptOutConfirmContentDialog(
                new ContentDialogHost(), "Start it?", "It runs on its own.", "Start");
            var body = ((StackPanel)dialog.Content).Children.OfType<System.Windows.Controls.TextBlock>().First();
            return (dialog.Title, dialog.PrimaryButtonText, dialog.CloseButtonText, body.Text);
        });

        Assert.Equal("Start it?", title);
        Assert.Equal("Start", primary);
        Assert.False(string.IsNullOrEmpty(close));
        Assert.Equal("It runs on its own.", message);
    }

    [Fact]
    public void TickingTheBox_IsWhatTheCallerReadsBack()
    {
        var (before, after) = WpfStaHost.Run(() =>
        {
            var dialog = new Pia.Views.Dialogs.OptOutConfirmContentDialog(
                new ContentDialogHost(), "Start it?", "It runs on its own.", "Start");
            var box = ((StackPanel)dialog.Content).Children.OfType<CheckBox>().Single();
            var wasTicked = dialog.DontAskAgain;
            box.IsChecked = true;
            return (wasTicked, dialog.DontAskAgain);
        });

        Assert.False(before);
        Assert.True(after);
    }
}

using System.Windows;
using System.Windows.Controls;
using Pia.Views.Dialogs;
using Xunit;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;

namespace Pia.Tests.Views;

/// <summary>Runs against Wpf.Ui's real template, which renders an empty close button unless it is collapsed.</summary>
[Collection("WpfApplicationStatic")]
public class ContentDialogCloseButtonTests
{
    private static (Visibility Close, double CloseWidth, double PrimaryWidth, double SecondaryWidth) Render(bool hide)
    {
        return WpfStaHost.Run(() =>
        {
            var dialog = new ContentDialog
            {
                Title = "Title",
                Content = "Body",
                PrimaryButtonText = "Clear",
                SecondaryButtonText = "Keep",
                CloseButtonText = "",
            };
            if (hide)
            {
                ContentDialogCloseButton.HideOnLoad(dialog);
            }

            var window = new Window
            {
                Content = dialog,
                Width = 800,
                Height = 600,
                Left = -10000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };
            try
            {
                window.Show();
                dialog.UpdateLayout();
                var close = (Button)dialog.Template.FindName("CloseButton", dialog);
                var primary = (Button)dialog.Template.FindName("PrimaryButton", dialog);
                var secondary = (Button)dialog.Template.FindName("SecondaryButton", dialog);
                return (close.Visibility, close.ActualWidth, primary.ActualWidth, secondary.ActualWidth);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EmptyText_AloneStillRendersTheCloseButton()
    {
        var (close, closeWidth, _, _) = Render(hide: false);

        Assert.Equal(Visibility.Visible, close);
        Assert.True(closeWidth > 0);
    }

    [Fact]
    public void Hidden_CollapsesTheButton_AndGivesItsSpaceToTheOtherTwo()
    {
        var shown = Render(hide: false);
        var hidden = Render(hide: true);

        Assert.Equal(Visibility.Collapsed, hidden.Close);
        Assert.True(hidden.PrimaryWidth > shown.PrimaryWidth);
        Assert.Equal(hidden.PrimaryWidth, hidden.SecondaryWidth, 1);
    }
}

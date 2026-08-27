using System.Windows;
using System.Windows.Controls;
using NSubstitute;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>The notice is the only place a user learns the key will not reach their other devices, and a
/// binding that stops resolving hides it silently.</summary>
[Collection("WpfApplicationStatic")]
public class ProviderEditApiKeyNoticeTests
{
    [Theory]
    [InlineData(true, nameof(Visibility.Visible))]
    [InlineData(false, nameof(Visibility.Collapsed))]
    public void TheNotice_ShowsExactlyWhenTheKeyStaysOnThisDevice(bool isApiKeyDeviceLocal, string expected)
    {
        Pia.Views.Dialogs.ProviderEditContentDialog? dialog = null;
        WpfStaHost.Run(() => dialog = Dialog(isApiKeyDeviceLocal));
        WpfStaHost.Pump();

        var actual = WpfStaHost.Run(() => Notice(dialog!).Visibility.ToString());

        Assert.Equal(expected, actual);
    }

    private static Pia.Views.Dialogs.ProviderEditContentDialog Dialog(bool isApiKeyDeviceLocal) =>
        new(new ContentDialogHost(),
            new ProviderEditModel { IsApiKeyDeviceLocal = isApiKeyDeviceLocal },
            Substitute.For<IProviderService>());

    // By binding path, not by position: the dialog holds two soft-block Borders and an index would swap
    // them the next time a field is added above.
    private static Border Notice(DependencyObject dialog) =>
        BindingPathWalker.FindLogical<Border>(dialog).Single(b =>
            BindingPathWalker.PathOf(b, UIElement.VisibilityProperty)
                == nameof(ProviderEditModel.IsApiKeyDeviceLocal));
}

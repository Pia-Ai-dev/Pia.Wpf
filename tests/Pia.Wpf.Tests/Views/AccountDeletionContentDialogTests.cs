using NSubstitute;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.Views;

[Collection("WpfApplicationStatic")]
public class AccountDeletionContentDialogTests
{
    [Fact]
    public void TheDeleteButton_IsStyledAsDestructive()
    {
        var appearance = WpfStaHost.Run(() => Dialog().PrimaryButtonAppearance);

        Assert.Equal(ControlAppearance.Danger, appearance);
    }

    internal static Pia.Views.Dialogs.AccountDeletionContentDialog Dialog() =>
        new(new ContentDialogHost(),
            new AccountDeletionViewModel(
                Substitute.For<IAccountDataService>(),
                Substitute.For<IFileDialogService>(),
                Substitute.For<ILocalizationService>(),
                requiresPassword: true));
}

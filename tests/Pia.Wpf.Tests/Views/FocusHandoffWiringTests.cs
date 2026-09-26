using System.Windows;
using System.Windows.Automation;
using NSubstitute;
using Pia.Behaviors;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>Buttons that disable themselves while their work runs, so without the handoff they drop focus onto their container.</summary>
[Collection("WpfApplicationStatic")]
public class FocusHandoffWiringTests
{
    [Fact]
    public void TheAccountDeletionExportButton_PassesFocusOn() =>
        Assert.True(HandsOff(AccountDeletionContentDialogTests.Dialog, "AccountDeletion_Export"));

    [Fact]
    public void TheAccountSettingsExportButton_PassesFocusOn() =>
        Assert.True(HandsOff(() => new Pia.Views.SettingsViews.AccountView(), "Settings_Account_ExportData"));

    [Fact]
    public void TheProviderFetchModelsButton_PassesFocusOn() =>
        Assert.True(HandsOff(
            () => new Pia.Views.Dialogs.ProviderEditContentDialog(
                new ContentDialogHost(), new ProviderEditModel(), Substitute.For<IProviderService>()),
            "ProviderEdit_FetchModels"));

    private static bool HandsOff(Func<DependencyObject> view, string automationId) => WpfStaHost.Run(() =>
    {
        var button = BindingPathWalker.FindLogical<System.Windows.Controls.Button>(view())
            .Single(b => AutomationProperties.GetAutomationId(b) == automationId);
        return FocusHandoffBehavior.GetMoveFocusWhenDisabled(button);
    });
}

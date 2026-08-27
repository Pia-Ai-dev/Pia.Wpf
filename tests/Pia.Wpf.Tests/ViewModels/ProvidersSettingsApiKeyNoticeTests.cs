using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

// The flag the provider dialog's device-local notice binds to. Only cloud-without-E2EE earns it: with E2EE
// the key syncs inside the encrypted payload, and signed out there is no server in the picture at all.
public class ProvidersSettingsApiKeyNoticeTests
{
    [Theory]
    [InlineData(true, false, true)]   // signed in, E2EE off — the server never gets the key
    [InlineData(true, true, false)]   // signed in, E2EE on  — the key syncs encrypted
    [InlineData(false, false, false)] // signed out          — nothing leaves the device anyway
    public async Task Add_TellsTheDialogWhetherTheKeyStaysOnThisDevice(
        bool isLoggedIn, bool isE2EEEnabled, bool expected)
    {
        var (sut, captured) = Sut(isLoggedIn, isE2EEEnabled);

        await sut.AddProviderCommand.ExecuteAsync(null);

        Assert.Equal(expected, captured().IsApiKeyDeviceLocal);
    }

    // A second call site, and it builds the model through FromProvider rather than a fresh one.
    [Fact]
    public async Task Edit_TellsTheDialogTheKeyStaysOnThisDevice()
    {
        var (sut, captured) = Sut(isLoggedIn: true, isE2EEEnabled: false);

        await sut.EditProviderCommand.ExecuteAsync(new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "OpenAI",
            ProviderType = AiProviderType.OpenAI,
            Endpoint = "https://api.openai.com/v1",
        });

        Assert.True(captured().IsApiKeyDeviceLocal);
    }

    // Returns false from the dialog: this asserts what the dialog is HANDED, so the save path is noise.
    private static (ProvidersSettingsViewModel Sut, Func<ProviderEditModel> Captured) Sut(
        bool isLoggedIn, bool isE2EEEnabled)
    {
        ProviderEditModel? captured = null;

        var dialogService = Substitute.For<IDialogService>();
        dialogService
            .ShowProviderEditDialogAsync(Arg.Do<ProviderEditModel>(m => captured = m), Arg.Any<IProviderService>())
            .Returns(false);

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(_ =>
            Task.FromResult(new AppSettings { SyncEnabled = isLoggedIn, IsE2EEEnabled = isE2EEEnabled }));

        var providerService = Substitute.For<IProviderService>();
        providerService.GetProvidersAsync().Returns(_ => Task.FromResult<IReadOnlyList<AiProvider>>([]));

        var authService = Substitute.For<IAuthService>();
        authService.IsLoggedIn.Returns(isLoggedIn);

        var sut = new ProvidersSettingsViewModel(
            null!, NullLogger<SettingsViewModel>.Instance, providerService, settingsService,
            dialogService, Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            authService, Substitute.For<ILocalizationService>(), Substitute.For<IPolicyService>());

        return (sut, () => captured ?? throw new InvalidOperationException(
            "the command never opened the provider dialog, so there is no model to assert on"));
    }
}

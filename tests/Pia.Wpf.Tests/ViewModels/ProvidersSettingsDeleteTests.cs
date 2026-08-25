using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>Deleting a provider asks first. Asserted on the command, since the button only invokes it.</summary>
public class ProvidersSettingsDeleteTests
{
    private static AiProvider UserProvider() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Local",
        ProviderType = AiProviderType.Ollama,
        Endpoint = "http://localhost:11434",
    };

    private static ProvidersSettingsViewModel Sut(
        IProviderService providerService, IDialogService dialogService, ILocalizationService localization)
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(new AppSettings()));

        providerService.GetProvidersAsync().Returns(_ => Task.FromResult<IReadOnlyList<AiProvider>>([]));

        return new ProvidersSettingsViewModel(
            null!, NullLogger<SettingsViewModel>.Instance, providerService, settingsService,
            dialogService, Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            Substitute.For<IAuthService>(), localization, Substitute.For<IPolicyService>());
    }

    private static ILocalizationService EchoingLocalization()
    {
        var localization = Substitute.For<ILocalizationService>();
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(call => string.Join("|", call.ArgAt<object[]>(1)));
        return localization;
    }

    [Fact]
    public async Task Delete_WhenConfirmed_DeletesTheProvider()
    {
        var provider = UserProvider();
        var providerService = Substitute.For<IProviderService>();
        var dialogService = Substitute.For<IDialogService>();
        dialogService.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var sut = Sut(providerService, dialogService, EchoingLocalization());

        await sut.DeleteProviderCommand.ExecuteAsync(provider);

        await providerService.Received(1).DeleteProviderAsync(provider.Id);
    }

    [Fact]
    public async Task Delete_WhenDeclined_KeepsTheProvider()
    {
        var provider = UserProvider();
        var providerService = Substitute.For<IProviderService>();
        var dialogService = Substitute.For<IDialogService>();
        dialogService.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        var sut = Sut(providerService, dialogService, EchoingLocalization());

        await sut.DeleteProviderCommand.ExecuteAsync(provider);

        await dialogService.Received(1).ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>());
        await providerService.DidNotReceive().DeleteProviderAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task Delete_NamesTheProviderInThePrompt()
    {
        var provider = UserProvider();
        var providerService = Substitute.For<IProviderService>();
        var dialogService = Substitute.For<IDialogService>();
        dialogService.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        var sut = Sut(providerService, dialogService, EchoingLocalization());

        await sut.DeleteProviderCommand.ExecuteAsync(provider);

        await dialogService.Received(1).ShowConfirmationDialogAsync(
            Arg.Any<string>(), Arg.Is<string>(message => message.Contains(provider.Name)));
    }

    [Fact]
    public async Task Delete_WhenAssignedToAMode_RefusesWithoutAsking()
    {
        var provider = UserProvider();
        var providerService = Substitute.For<IProviderService>();
        var dialogService = Substitute.For<IDialogService>();
        dialogService.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var sut = Sut(providerService, dialogService, EchoingLocalization());
        sut.AssistantProviderId = provider.Id;

        await sut.DeleteProviderCommand.ExecuteAsync(provider);

        await dialogService.DidNotReceive().ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>());
        await providerService.DidNotReceive().DeleteProviderAsync(Arg.Any<Guid>());
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The provider/persona management locks. Asserted on the commands, not the buttons: the XAML
/// only hides them, so a command reached by any other route must still refuse.</summary>
public class ManagementLockTests
{
    private static ProvidersSettingsViewModel Providers(AppSettings stored)
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(stored));

        var providerService = Substitute.For<IProviderService>();
        providerService.GetProvidersAsync().Returns(_ => Task.FromResult<IReadOnlyList<AiProvider>>([]));

        return new ProvidersSettingsViewModel(
            null!, NullLogger<SettingsViewModel>.Instance, providerService, settingsService,
            Substitute.For<IDialogService>(), Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            Substitute.For<IAuthService>(), Substitute.For<ILocalizationService>(),
            Substitute.For<IPolicyService>());
    }

    private static PersonaSettingsViewModel Personas(AppSettings stored)
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(stored));

        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(_ => Task.FromResult<IReadOnlyList<Persona>>([]));

        var providerService = Substitute.For<IProviderService>();
        providerService.GetProvidersAsync().Returns(_ => Task.FromResult<IReadOnlyList<AiProvider>>([]));

        return new PersonaSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, personaService, providerService,
            Substitute.For<ITextOptimizationService>(),
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), Substitute.For<ILocalizationService>(),
            Substitute.For<IAuthService>(), settingsService, Substitute.For<IPolicyService>());
    }

    private static Persona UserPersona() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Mine",
        SystemPrompt = "prompt",
    };

    private static AiProvider UserProvider() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Local",
        ProviderType = AiProviderType.Ollama,
        Endpoint = "http://localhost:11434",
    };

    [Fact]
    public async Task Providers_AllowedByDefault()
    {
        var sut = Providers(new AppSettings());

        await sut.InitializeAsync();

        Assert.True(sut.CanManageProviders);
        Assert.True(sut.AddProviderCommand.CanExecute(null));
        Assert.True(sut.EditProviderCommand.CanExecute(UserProvider()));
        Assert.True(sut.DeleteProviderCommand.CanExecute(UserProvider()));
    }

    [Fact]
    public async Task Providers_LockedByPolicy_RefusesAddEditAndDelete()
    {
        var sut = Providers(new AppSettings { AllowProviderManagement = false });

        await sut.InitializeAsync();

        Assert.False(sut.CanManageProviders);
        Assert.False(sut.AddProviderCommand.CanExecute(null));
        Assert.False(sut.EditProviderCommand.CanExecute(UserProvider()));
        Assert.False(sut.DeleteProviderCommand.CanExecute(UserProvider()));
    }

    [Fact]
    public async Task Personas_AllowedByDefault()
    {
        var sut = Personas(new AppSettings());

        await sut.InitializeAsync();

        Assert.True(sut.CanManagePersonas);
        Assert.True(sut.AddPersonaCommand.CanExecute(null));
        Assert.True(sut.DuplicatePersonaCommand.CanExecute(UserPersona()));
        Assert.True(sut.DeletePersonaCommand.CanExecute(UserPersona()));
    }

    [Fact]
    public async Task Personas_LockedByPolicy_RefusesAddDuplicateEditAndDelete()
    {
        var sut = Personas(new AppSettings { AllowPersonaManagement = false });

        await sut.InitializeAsync();

        Assert.False(sut.CanManagePersonas);
        Assert.False(sut.AddPersonaCommand.CanExecute(null));
        Assert.False(sut.DuplicatePersonaCommand.CanExecute(UserPersona()));
        Assert.False(sut.EditPersonaCommand.CanExecute(UserPersona()));
        Assert.False(sut.DeletePersonaCommand.CanExecute(UserPersona()));
    }

    [Fact]
    public async Task Personas_LockedByPolicy_AddOpensNoEditorAndReachesNoService()
    {
        var stored = new AppSettings { AllowPersonaManagement = false };
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(stored));

        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(_ => Task.FromResult<IReadOnlyList<Persona>>([]));

        var providerService = Substitute.For<IProviderService>();
        providerService.GetProvidersAsync().Returns(_ => Task.FromResult<IReadOnlyList<AiProvider>>([]));

        var sut = new PersonaSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, personaService, providerService,
            Substitute.For<ITextOptimizationService>(),
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), Substitute.For<ILocalizationService>(),
            Substitute.For<IAuthService>(), settingsService, Substitute.For<IPolicyService>());

        await sut.InitializeAsync();
        await sut.AddPersonaCommand.ExecuteAsync(null);

        await personaService.DidNotReceive().AddPersonaAsync(Arg.Any<Persona>());
        Assert.False(sut.IsEditorOpen);
        Assert.Null(sut.Editor);
    }

    /// <summary>The lock arrives on a policy pull, which can land while the editor is already open — the
    /// only thing standing between that and a write is Save's own re-check.</summary>
    [Fact]
    public async Task Personas_LockedWhileTheEditorIsOpen_SaveDoesNotReachTheService()
    {
        var stored = new AppSettings { AllowPersonaManagement = true };
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(stored));

        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(_ => Task.FromResult<IReadOnlyList<Persona>>([]));

        var providerService = Substitute.For<IProviderService>();
        providerService.GetProvidersAsync().Returns(_ => Task.FromResult<IReadOnlyList<AiProvider>>([]));

        var sut = new PersonaSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, personaService, providerService,
            Substitute.For<ITextOptimizationService>(),
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), Substitute.For<ILocalizationService>(),
            Substitute.For<IAuthService>(), settingsService, Substitute.For<IPolicyService>());

        await sut.InitializeAsync();
        await sut.AddPersonaCommand.ExecuteAsync(null);
        sut.Editor!.Name = "Drafted";
        sut.Editor.SystemPrompt = "prompt";
        Assert.True(sut.Editor.CanSave, "non-vacuity: the save must be blocked by the lock, not by validation");

        sut.CanManagePersonas = false;
        await sut.SaveCommand.ExecuteAsync(null);

        await personaService.DidNotReceive().AddPersonaAsync(Arg.Any<Persona>());
    }

    [Fact]
    public async Task Providers_LockedByPolicy_AddDoesNotReachTheDialogOrTheService()
    {
        var stored = new AppSettings { AllowProviderManagement = false };
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(stored));

        var providerService = Substitute.For<IProviderService>();
        providerService.GetProvidersAsync().Returns(_ => Task.FromResult<IReadOnlyList<AiProvider>>([]));

        var dialogs = Substitute.For<IDialogService>();
        dialogs.ShowProviderEditDialogAsync(Arg.Any<ProviderEditModel>(), Arg.Any<IProviderService>()).Returns(true);

        var sut = new ProvidersSettingsViewModel(
            null!, NullLogger<SettingsViewModel>.Instance, providerService, settingsService, dialogs,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), Substitute.For<IAuthService>(),
            Substitute.For<ILocalizationService>(), Substitute.For<IPolicyService>());

        await sut.InitializeAsync();
        await sut.AddProviderCommand.ExecuteAsync(null);
        await sut.DeleteProviderCommand.ExecuteAsync(UserProvider());

        await providerService.DidNotReceive().AddProviderAsync(Arg.Any<AiProvider>(), Arg.Any<string?>());
        await providerService.DidNotReceive().DeleteProviderAsync(Arg.Any<Guid>());
        await dialogs.DidNotReceive().ShowProviderEditDialogAsync(Arg.Any<ProviderEditModel>(), Arg.Any<IProviderService>());
    }

    [Fact]
    public async Task Personas_LockedByPolicy_DuplicateAndDeleteDoNotReachTheService()
    {
        var stored = new AppSettings { AllowPersonaManagement = false };
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(stored));

        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(_ => Task.FromResult<IReadOnlyList<Persona>>([]));

        var sut = new PersonaSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, personaService, Substitute.For<IProviderService>(),
            Substitute.For<ITextOptimizationService>(),
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), Substitute.For<ILocalizationService>(),
            Substitute.For<IAuthService>(), settingsService, Substitute.For<IPolicyService>());

        await sut.InitializeAsync();
        var persona = UserPersona();
        await sut.DuplicatePersonaCommand.ExecuteAsync(persona);
        await sut.DeletePersonaCommand.ExecuteAsync(persona);
        await sut.EditPersonaCommand.ExecuteAsync(persona);

        await personaService.DidNotReceive().AddPersonaAsync(Arg.Any<Persona>());
        await personaService.DidNotReceive().DeletePersonaAsync(Arg.Any<Guid>());
        await personaService.DidNotReceive().UpdatePersonaAsync(Arg.Any<Persona>());
    }

    [Fact]
    public void PolicyLock_ReportsEditableWhenNotEnforced()
    {
        var policy = Substitute.For<IPolicyService>();
        policy.IsEnforced(nameof(AppSettings.Theme)).Returns(true);

        var sut = new PolicyLock(policy);

        Assert.False(sut[nameof(AppSettings.Theme)]);
        Assert.True(sut[nameof(AppSettings.StartMinimized)]);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

public class AssistantSettingsRosterTests
{
    private static Persona MakePersona(string name, string? emoji = null, string? accentColor = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        SystemPrompt = "prompt",
        Emoji = emoji,
        AccentColor = accentColor,
    };

    // Safe as mutable fixture state: xunit builds one instance per fact, so nothing leaks between them.

    private (AssistantSettingsViewModel sut, ISettingsService settings, AppSettings stored) Create(
        AppSettings? initial, IPersonaService? personaService)
    {
        var stored = initial ?? new AppSettings();

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(stored));
        settingsService.SaveSettingsAsync(Arg.Any<AppSettings>()).Returns(Task.CompletedTask);

        var localization = Substitute.For<ILocalizationService>();
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns("display");
        localization[Arg.Any<string>()].Returns("display");

        var workingDirectoryService = Substitute.For<IWorkingDirectoryService>();
        workingDirectoryService.ListSubfolders(Arg.Any<string>()).Returns(Array.Empty<string>());

        var dialogService = Substitute.For<IDialogService>();

        // ProvidersSettingsViewModel only stores its `parent`, never dereferences it in the ctor, so `null!` holds.
        var providersVm = new ProvidersSettingsViewModel(
            null!, NullLogger<SettingsViewModel>.Instance,
            Substitute.For<IProviderService>(), settingsService, dialogService,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), Substitute.For<IAuthService>(),
            localization, Substitute.For<IPolicyService>());

        var personasVm = new PersonaSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, Substitute.For<IPersonaService>(),
            Substitute.For<IProviderService>(), Substitute.For<ITextOptimizationService>(),
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), localization,
            Substitute.For<IAuthService>(), settingsService, Substitute.For<IPolicyService>());

        var toolPermissionsVm = new ToolPermissionsSettingsViewModel(
            Substitute.For<IToolPermissionService>(), Substitute.For<IPluginService>(),
            NullLogger<SettingsViewModel>.Instance);

        var meetingVm = new MeetingSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, settingsService, localization, Substitute.For<IPolicyService>());

        var sut = new AssistantSettingsViewModel(
            providersVm, personasVm, toolPermissionsVm, meetingVm,
            NullLogger<SettingsViewModel>.Instance, settingsService, Substitute.For<IAssistantChatService>(),
            dialogService, localization, Substitute.For<IAssistantFolderRelocationService>(),
            workingDirectoryService, personaService: personaService, policyService: Substitute.For<IPolicyService>());

        return (sut, settingsService, stored);
    }

    [Fact]
    public async Task Initialize_NoPersonaService_LeavesRosterEmpty()
    {
        var (sut, _, _) = Create(null, personaService: null);

        await sut.InitializeAsync();

        Assert.Empty(sut.AgentRosterOptions);
        Assert.False(sut.HasSelectedRoster);
    }

    [Fact]
    public async Task Initialize_ChecksOnlyTheRowsAlreadyInTheStoredRoster()
    {
        var alice = MakePersona("Alice", "star", "#FF0000");
        var bob = MakePersona("Bob");
        var stored = new AppSettings();
        stored.SetAgentPersonaRoster(UserOperatingMode.Personal, [alice.Id]);

        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(Task.FromResult<IReadOnlyList<Persona>>([alice, bob]));

        var (sut, _, _) = Create(stored, personaService);
        await sut.InitializeAsync();

        Assert.Equal(2, sut.AgentRosterOptions.Count);
        Assert.True(sut.AgentRosterOptions.Single(o => o.Id == alice.Id).IsSelected);
        Assert.False(sut.AgentRosterOptions.Single(o => o.Id == bob.Id).IsSelected);
        Assert.True(sut.HasSelectedRoster);
    }

    [Fact]
    public async Task TogglingARowOn_PersistsThroughSetAgentPersonaRoster()
    {
        var alice = MakePersona("Alice");
        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(Task.FromResult<IReadOnlyList<Persona>>([alice]));

        var (sut, settingsService, stored) = Create(null, personaService);
        await sut.InitializeAsync();

        sut.AgentRosterOptions.Single().IsSelected = true;

        // The toggle's save is fire-and-forget; the substitute completes it synchronously, so nothing is awaited.
        await settingsService.Received().SaveSettingsAsync(Arg.Any<AppSettings>());
        Assert.Contains(alice.Id, stored.GetAgentPersonaRoster(UserOperatingMode.Personal));
        Assert.True(sut.HasSelectedRoster);
    }

    [Fact]
    public async Task TogglingTheLastRowOff_RemovesTheModeKey()
    {
        var alice = MakePersona("Alice");
        var stored = new AppSettings();
        stored.SetAgentPersonaRoster(UserOperatingMode.Personal, [alice.Id]);

        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(Task.FromResult<IReadOnlyList<Persona>>([alice]));

        var (sut, _, resultStored) = Create(stored, personaService);
        await sut.InitializeAsync();

        sut.AgentRosterOptions.Single().IsSelected = false;

        Assert.False(resultStored.AgentPersonaRoster.ContainsKey(UserOperatingMode.Personal));
        Assert.False(sut.HasSelectedRoster);
    }

    [Fact]
    public async Task AnUnrelatedSaveAfterAFaultedPersonaLoad_DoesNotEraseTheConfiguredRoster()
    {
        // A faulted persona load leaves the surface empty but the service non-null, so a roster write gated on
        // the service alone would persist that emptiness over a still-configured roster.
        var alice = MakePersona("Alice");
        var stored = new AppSettings();
        stored.SetAgentPersonaRoster(UserOperatingMode.Personal, [alice.Id]);

        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(Task.FromException<IReadOnlyList<Persona>>(new InvalidOperationException("boom")));

        var (sut, _, resultStored) = Create(stored, personaService);
        await sut.InitializeAsync();
        Assert.Empty(sut.AgentRosterOptions);

        sut.AgentMaxSteps += 1;

        Assert.Contains(alice.Id, resultStored.GetAgentPersonaRoster(UserOperatingMode.Personal));
    }

    [Fact]
    public async Task SelectingA7thPersona_IsRefusedSilently_AndDoesNotStick()
    {
        var personas = Enumerable.Range(0, AppSettings.MaxAgentPersonaRoster + 1)
            .Select(i => MakePersona($"P{i}"))
            .ToArray();
        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(Task.FromResult<IReadOnlyList<Persona>>(personas));

        var (sut, _, stored) = Create(null, personaService);
        await sut.InitializeAsync();

        foreach (var row in sut.AgentRosterOptions.Take(AppSettings.MaxAgentPersonaRoster))
            row.IsSelected = true;

        var seventh = sut.AgentRosterOptions.Last();
        seventh.IsSelected = true;

        Assert.False(seventh.IsSelected);
        Assert.Equal(AppSettings.MaxAgentPersonaRoster, sut.AgentRosterOptions.Count(o => o.IsSelected));
        Assert.Equal(AppSettings.MaxAgentPersonaRoster,
            stored.GetAgentPersonaRoster(UserOperatingMode.Personal).Count);
    }

    [Fact]
    public async Task AnUnrelatedSave_KeepsARosterEntryThatPolicyHasHidden()
    {
        // A policy-blocked built-in never reaches AgentRosterOptions, so rebuilding the roster from the
        // surface alone would prune it — and unblocking later would not bring it back.
        var alice = MakePersona("Alice");
        var stored = new AppSettings { BlockedBuiltInPersonas = ["ExperiencedCoder"] };
        stored.SetAgentPersonaRoster(UserOperatingMode.Personal, [alice.Id, BuiltInPersonas.ExperiencedCoderId]);

        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(Task.FromResult<IReadOnlyList<Persona>>([alice]));

        var (sut, _, resultStored) = Create(stored, personaService);
        await sut.InitializeAsync();

        sut.AgentMaxSteps += 1;

        var roster = resultStored.GetAgentPersonaRoster(UserOperatingMode.Personal);
        Assert.Contains(alice.Id, roster);
        Assert.Contains(BuiltInPersonas.ExperiencedCoderId, roster);
    }

    [Fact]
    public async Task SettingsChanged_MirrorsTheRosterWithoutSavingItBack()
    {
        // The mirror flips IsSelected on already-realized rows, which is the same setter a user's click
        // uses to trigger a save — this pins that the _isLoading bracket around ApplySettings covers it too.
        var alice = MakePersona("Alice");
        var bob = MakePersona("Bob");
        var stored = new AppSettings();
        stored.SetAgentPersonaRoster(UserOperatingMode.Personal, [alice.Id]);

        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(Task.FromResult<IReadOnlyList<Persona>>([alice, bob]));

        var (sut, settingsService, resultStored) = Create(stored, personaService);
        await sut.InitializeAsync();
        settingsService.ClearReceivedCalls();

        resultStored.SetAgentPersonaRoster(UserOperatingMode.Personal, [bob.Id]);
        settingsService.SettingsChanged += Raise.Event<EventHandler<AppSettings>>(settingsService, resultStored);

        Assert.False(sut.AgentRosterOptions.Single(o => o.Id == alice.Id).IsSelected);
        Assert.True(sut.AgentRosterOptions.Single(o => o.Id == bob.Id).IsSelected);
        await settingsService.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
    }
}

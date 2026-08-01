using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Batch 07 G7 - the "step specialists" roster surface (<see cref="AssistantSettingsViewModel.AgentRosterOptions"/>):
/// loads one row per known persona checked against <see cref="AppSettings.AgentPersonaRoster"/> keyed by
/// <see cref="UserOperatingMode"/>, persists a toggle back through the same <c>SaveSettingsAsync</c> path
/// every other Agent* knob uses (mirrors <c>MeetingSettingsViewModelTests</c>), enforces the
/// <see cref="AppSettings.MaxAgentPersonaRoster"/> cap by reverting the checkbox rather than throwing, and
/// degrades to an empty (not broken) surface when no <see cref="IPersonaService"/> is supplied - the
/// null-service arm every trailing-defaulted ctor param added in this batch relies on (07 spec S4.4).
/// <para>
/// The camelCase JSON round-trip of the underlying <see cref="AppSettings"/> members is already covered by
/// <c>AppSettingsAgentRosterTests</c> (shipped with G6); this file covers only what G7 adds - the VM/UI
/// projection over that model.
/// </para>
/// </summary>
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

    /// <summary>The scheduled-jobs service handed to the last <see cref="Create"/> call, so the Batch 09
    /// wiring fact below can assert the section was loaded. Instance field: xunit builds one instance per
    /// fact, so there is nothing to leak between them.</summary>
    private IScheduledJobService _scheduledJobs = null!;

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

        // The four nested settings VMs AssistantSettingsViewModel is handed positionally (mirrors the real
        // wiring in SettingsViewModel.cs) - none of their own behaviour is under test here, so every
        // dependency is a bare NSubstitute. ProvidersSettingsViewModel's `parent` is never dereferenced in
        // its own constructor (only stored), so `null!` is safe for a test that never touches ProvidersVm.
        var providersVm = new ProvidersSettingsViewModel(
            null!, NullLogger<SettingsViewModel>.Instance,
            Substitute.For<IProviderService>(), settingsService, dialogService,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), Substitute.For<IAuthService>(),
            localization, Substitute.For<IPolicyService>());

        var personasVm = new PersonaSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, Substitute.For<IPersonaService>(),
            Substitute.For<IProviderService>(), Substitute.For<ITextOptimizationService>(),
            dialogService, Substitute.For<global::Wpf.Ui.ISnackbarService>(), localization,
            Substitute.For<IAuthService>());

        var toolPermissionsVm = new ToolPermissionsSettingsViewModel(
            Substitute.For<IToolPermissionService>(), Substitute.For<IPluginService>(),
            NullLogger<SettingsViewModel>.Instance);

        var meetingVm = new MeetingSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, settingsService, localization);

        // Batch 09's sub-VM. Its service is kept on the fixture so the wiring fact below can assert that
        // InitializeAsync loads the section — the one thing about it that neither a parse test nor its own
        // ViewModel tests can see.
        _scheduledJobs = Substitute.For<IScheduledJobService>();
        _scheduledJobs.GetAllAsync().Returns(Array.Empty<ScheduledJob>());
        var scheduledProviders = Substitute.For<IProviderService>();
        scheduledProviders.GetProvidersAsync().Returns(Array.Empty<AiProvider>());
        var scheduledJobsVm = new ScheduledJobsSettingsViewModel(
            _scheduledJobs, Substitute.For<IScheduledJobRunner>(),
            scheduledProviders, localization, NullLogger<SettingsViewModel>.Instance);

        var sut = new AssistantSettingsViewModel(
            providersVm, personasVm, toolPermissionsVm, meetingVm, scheduledJobsVm,
            NullLogger<SettingsViewModel>.Instance, settingsService, Substitute.For<IAssistantChatService>(),
            dialogService, localization, Substitute.For<IAssistantFolderRelocationService>(),
            workingDirectoryService, personaService);

        return (sut, settingsService, stored);
    }

    [Fact]
    public async Task Initialize_LoadsTheScheduledJobsSection()
    {
        // Batch 09's wiring, and it is a fact because the hole it closes was invisible to everything else:
        // Jobs and ProviderChoices are populated ONLY by RefreshAsync, the section's bindings are correct
        // either way, the parse test reads declared paths without evaluating them, and the section's own
        // ViewModel tests call RefreshAsync themselves. A correct binding with no data behind it renders an
        // empty list that looks exactly like having no scheduled jobs.
        var (sut, _, _) = Create(null, null);

        await sut.InitializeAsync();

        await _scheduledJobs.Received(1).GetAllAsync();
    }

    [Fact]
    public async Task Initialize_NoPersonaService_LeavesRosterEmpty()
    {
        // GUARD, not a regression: the null-service arm every trailing-defaulted ctor param in this batch
        // relies on. A null IPersonaService must degrade to an empty, non-throwing surface (D1 still holds
        // with nothing configured).
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

        // Non-vacuity: both known personas surfaced as rows before asserting which is checked.
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

        // OnRosterOptionToggled fires SaveSettingsAsync fire-and-forget; the substitute completes
        // synchronously (same pattern as MeetingSettingsViewModelTests.TogglingDiarization_PersistsToAppSettings).
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

        // Mirrors AppSettings.SetAgentPersonaRoster's own "empty removes the key" contract (T-SET-3
        // precedent in AppSettingsAgentRosterTests) - a cleared roster must leave no residue.
        Assert.False(resultStored.AgentPersonaRoster.ContainsKey(UserOperatingMode.Personal));
        Assert.False(sut.HasSelectedRoster);
    }

    [Fact]
    public async Task AnUnrelatedSaveAfterAFaultedPersonaLoad_DoesNotEraseTheConfiguredRoster()
    {
        // REGRESSION guard: a faulted GetPersonasAsync leaves AgentRosterOptions empty but
        // _personaService non-null. Gating the roster write on the SERVICE being non-null (rather than
        // on the load having actually SETTLED) would let an unrelated save on this tab overwrite a
        // still-configured roster with that empty surface.
        var alice = MakePersona("Alice");
        var stored = new AppSettings();
        stored.SetAgentPersonaRoster(UserOperatingMode.Personal, [alice.Id]);

        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(Task.FromException<IReadOnlyList<Persona>>(new InvalidOperationException("boom")));

        var (sut, _, resultStored) = Create(stored, personaService);
        await sut.InitializeAsync();
        Assert.Empty(sut.AgentRosterOptions); // the faulted load left the surface empty...

        // ...but an unrelated knob's save must not persist that emptiness over AppSettings.
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

        // Refused SILENTLY-BUT-VISIBLY: the checkbox reverts rather than an error dialog (07 spec S4.2).
        Assert.False(seventh.IsSelected);
        Assert.Equal(AppSettings.MaxAgentPersonaRoster, sut.AgentRosterOptions.Count(o => o.IsSelected));
        Assert.Equal(AppSettings.MaxAgentPersonaRoster,
            stored.GetAgentPersonaRoster(UserOperatingMode.Personal).Count);
    }
}

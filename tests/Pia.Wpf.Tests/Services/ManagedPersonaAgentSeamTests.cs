using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

// The REAL PersonaService and a real database: the sibling suites substitute IPersonaService, so a substitute would
// pin the test's own assumption rather than PersonaService's merge order and its ManagedPersonas lookup.
public sealed class ManagedPersonaAgentSeamTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SqliteContext _ctx;
    private readonly SyncDeleteTrackerService _deleteTracker;
    private readonly TestSettingsService _settings = new();
    private readonly PersonaService _personas;

    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IAssistantPromptComposer _composer = Substitute.For<IAssistantPromptComposer>();

    public ManagedPersonaAgentSeamTests()
    {
        // An EXPLICIT temp database: ReplaceManagedPersonasAsync opens with `DELETE FROM ManagedPersonas`, which
        // against a real profile wipes every admin-published persona the developer is signed in to.
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaManagedPersonaSeam_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _deleteTracker = new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance);
        _personas = new PersonaService(_ctx, NullLogger<PersonaService>.Instance, _deleteTracker, _settings);

        _composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(ci => new AssistantTurnSetup($"system for {ci.ArgAt<Persona>(0).Name}", null, true, false));
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(Provider("mode-default"));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        TempPath.Remove(_tmpDir);
    }

    private StepPersonaResolver BuildResolver() => new(
        _personas, _providers, _composer, _settings, NullLogger<StepPersonaResolver>.Instance);

    private static AiProvider Provider(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Endpoint = "https://x",
        ProviderType = AiProviderType.OpenAI,
    };

    private static Persona ManagedPersona(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        SystemPrompt = "You are an admin-published persona.",
        ToolScope = PersonaToolScope.Full,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static StepPersonaSetup RunDefault() =>
        new(new Persona { Id = Guid.NewGuid(), Name = "Pia", SystemPrompt = "run system" },
            Provider("run-provider"),
            new AssistantTurnSetup("run system", null, false, false));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AManagedPersona_ReachesTheAgentRoster_AndRunsAStepOnItsOwnPrompt()
    {
        var managed = ManagedPersona("TEST_ManagedSpecialist");
        await _personas.ReplaceManagedPersonasAsync([managed]);
        _settings.Settings.SetAgentPersonaRoster(UserOperatingMode.Personal, [managed.Id]);
        var resolver = BuildResolver();

        var roster = await resolver.GetRosterAsync(Ct);
        var resolved = await resolver.ResolveAsync(managed.Id, RunDefault(), tokenizationEnabled: false, Ct);

        // Both halves: the roster read filters configured ids through GetPersonasAsync, so the first asks whether a
        // MANAGED row survives that filter and the second is what the user would notice.
        Assert.Equal([managed.Id], roster.Select(p => p.Id));
        Assert.True(roster[0].IsManaged);
        Assert.Equal(managed.Id, resolved.Persona.Id);
        Assert.Equal("system for TEST_ManagedSpecialist", resolved.TurnSetup.SystemPrompt);
    }

    [Fact]
    public async Task AManagedPersonaId_ResolvesThroughGetPersonaAsync_TheLaunchersSecondNarrowing()
    {
        // The launcher narrows a child's persona twice, on the roster and on GetPersonaAsync. A managed row lives in
        // ManagedPersonas, not Personas, so it is that lookup this pins — against the real store, not a substitute.
        var managed = ManagedPersona("TEST_ManagedForLauncher");
        await _personas.ReplaceManagedPersonasAsync([managed]);
        _settings.Settings.SetAgentPersonaRoster(UserOperatingMode.Personal, [managed.Id]);

        var byId = await _personas.GetPersonaAsync(managed.Id);

        Assert.NotNull(byId);
        Assert.True(byId!.IsManaged);
        Assert.True(byId.IsReadOnly);
        Assert.Contains(managed.Id, _settings.Settings.GetAgentPersonaRoster(UserOperatingMode.Personal));
    }

    [Fact]
    public async Task AWithdrawnManagedPersona_DropsOffTheRoster_AndTheStepFallsBackWithoutThrowing()
    {
        var managed = ManagedPersona("TEST_ManagedWithdrawn");
        await _personas.ReplaceManagedPersonasAsync([managed]);
        _settings.Settings.SetAgentPersonaRoster(UserOperatingMode.Personal, [managed.Id]);

        // Replace-all with an empty catalog is how an admin unassigns a persona; there is no tombstone. The resolver
        // is built AFTER it because the roster is memoized per run — a run in flight keeps its specialist.
        await _personas.ReplaceManagedPersonasAsync([]);
        var resolver = BuildResolver();
        var runDefault = RunDefault();

        var roster = await resolver.GetRosterAsync(Ct);
        var resolved = await resolver.ResolveAsync(managed.Id, runDefault, tokenizationEnabled: false, Ct);

        Assert.Empty(roster);
        Assert.Same(runDefault, resolved);
    }

    [Fact]
    public async Task AWithdrawnManagedPersonaId_LeavesTheAgentRoster_WhileTheStillPublishedOneStays()
    {
        // Three parts of one claim: the chat selection IS cleared, so a red reads as "the whole latch broke"; the
        // withdrawn id is gone; the still-published id stays, because clearing wholesale would satisfy the middle.
        var withdrawn = ManagedPersona("TEST_ManagedRosterWithdrawn");
        var republished = ManagedPersona("TEST_ManagedRosterRepublished");
        await _personas.ReplaceManagedPersonasAsync([withdrawn, republished]);
        _settings.Settings.SetAgentPersonaRoster(UserOperatingMode.Personal, [withdrawn.Id, republished.Id]);
        _settings.Settings.SetPersonaForMode(WindowMode.Assistant, withdrawn.Id);

        // The withdrawal: one of the two personas is missing from the new catalog. Publishing a NON-empty
        // catalog is what makes the survivor meaningful — a replace-all with [] would withdraw both.
        await _personas.ReplaceManagedPersonasAsync([republished]);

        var roster = _settings.Settings.GetAgentPersonaRoster(UserOperatingMode.Personal);
        Assert.Null(_settings.Settings.GetPersonaForMode(WindowMode.Assistant));
        Assert.DoesNotContain(withdrawn.Id, roster);
        Assert.Contains(republished.Id, roster);
    }

    // A real AppSettings: the withdrawal latch reads, mutates and saves the settings, so a substitute returning a
    // fresh object per call would lose the mutation these facts assert on.
    private sealed class TestSettingsService : ISettingsService
    {
#pragma warning disable CS0067
        public event EventHandler<AppSettings>? SettingsChanged;
#pragma warning restore CS0067
        public AppSettings Settings { get; } = new();
        public Task<AppSettings> GetSettingsAsync() => Task.FromResult(Settings);
        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public Task SaveDraftAsync(string? draftText) => Task.CompletedTask;
        public Task<string?> GetDraftAsync() => Task.FromResult<string?>(null);
    }
}

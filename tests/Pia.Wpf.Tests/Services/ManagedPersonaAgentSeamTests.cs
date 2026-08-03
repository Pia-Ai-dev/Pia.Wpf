using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The seam between managed personas (merged at <c>cf571e51</c> from <c>feature/managed-personas-client</c>)
/// and Batch 07's per-step persona machinery. <b>Neither branch's suite could have covered this</b>, because
/// neither branch had both halves: the agent roster shipped before managed personas existed, and the managed
/// persona work was authored against the chat picker, where the roster does not appear.
/// <para>
/// <b>Why these facts use the REAL <see cref="PersonaService"/> and a real database.</b>
/// <see cref="StepPersonaResolverTests"/> and <see cref="HeadlessRunLauncherTests"/> both substitute
/// <see cref="IPersonaService"/>, so a substitute answers whatever the test told it to — including for a
/// managed persona, which is exactly the question here. The whole content of "does a managed row reach the
/// agent roster" lives in <c>PersonaService</c>'s merge order and its <c>ManagedPersonas</c> lookup, so a
/// substitute would pin the test's own assumption rather than the code's behaviour.
/// </para>
/// <para>
/// <b>What these facts do NOT reach.</b> No run is launched and no orchestrator runs: the launcher's own
/// ladder is covered by <c>HeadlessRunLauncherTests</c>, and the fact below that stands in for it says so at
/// the assertion rather than implying more. Nothing here renders — whether the roster CheckBox list shows a
/// Managed badge is manual-smoke debt like every other locale/render item on this branch.
/// </para>
/// </summary>
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
        // An EXPLICIT temp database for the reason PersonaServiceTests documents: ReplaceManagedPersonasAsync
        // opens with `DELETE FROM ManagedPersonas`, and against a real profile that wipes every
        // admin-published persona the developer is signed in to.
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaManagedPersonaSeam_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _deleteTracker = new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance);
        _personas = new PersonaService(_ctx, NullLogger<PersonaService>.Instance, _deleteTracker, _settings);

        _composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
                Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(ci => new AssistantTurnSetup($"system for {ci.ArgAt<Persona>(0).Name}", null, true, false));
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(Provider("mode-default"));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, recursive: true); } catch (IOException) { /* best effort */ }
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

        // Both halves, because either alone is satisfiable while the seam is broken. GetRosterAsync filters
        // its configured ids through PersonaService.GetPersonasAsync, so the first half is the actual
        // question: does a MANAGED row survive that filter, or is a managed id silently dropped as "no longer
        // resolves to a persona"? The second half is what the user would notice — the step running on the
        // admin-published prompt rather than degrading to the run persona.
        Assert.Equal([managed.Id], roster.Select(p => p.Id));
        Assert.True(roster[0].IsManaged);
        Assert.Equal(managed.Id, resolved.Persona.Id);
        Assert.Equal("system for TEST_ManagedSpecialist", resolved.TurnSetup.SystemPrompt);
    }

    [Fact]
    public async Task AManagedPersonaId_ResolvesThroughGetPersonaAsync_TheLaunchersSecondNarrowing()
    {
        // HeadlessRunLauncher.ResolveRunPersonaAsync (:782/:788) narrows a delegated child's persona twice:
        // the id must be on settings' roster, AND GetPersonaAsync(id) must return something. This pins the
        // SECOND narrowing against the real store, which is the half managed personas changed — the launcher
        // reads its own single-row lookup, and a managed row lives in ManagedPersonas, not Personas.
        // <b>Deliberately not routed through the launcher itself</b>: HeadlessRunLauncherTests substitutes
        // IPersonaService, so building a launcher here would re-pin the substitute, not this lookup.
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

        // The withdrawal: replace-all with an empty catalog is how an admin unassigns a persona (there is no
        // tombstone). A resolver is built AFTER it, because the roster is memoized per run — a run already in
        // flight keeps the specialist it started with, which is the intended shape, not a gap.
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
        // This fact was written INVERTED, pinning the merge's one residue: ReplaceManagedPersonasAsync's
        // withdrawal latch walked settings.ModePersonaDefaults only, so a withdrawn id sat on the agent roster
        // until the settings page was next saved. It carried the instruction to flip rather than delete it when
        // the latch learned to walk the roster too, and that is what this now is — the roster walk's red.
        //
        // The three assertions are one claim in three parts, and none of them alone is worth anything:
        //  · the chat selection IS cleared — the contrast that proves the latch ran at all, so a red here is
        //    read as "the whole latch broke" rather than "the roster half broke";
        //  · the withdrawn id is GONE from the roster — the residue, closed;
        //  · the still-published id STAYS — the non-vacuity guard, because clearing the roster wholesale (or
        //    on any id at all, rather than on the withdrawn ones) would satisfy the middle assertion too.
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

    /// <summary>
    /// A real <see cref="AppSettings"/> behind <see cref="ISettingsService"/>: <c>PersonaService</c>'s
    /// withdrawal latch reads the settings, mutates them and saves, so a substitute returning a fresh object
    /// per call would lose the mutation this file asserts on.
    /// </summary>
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

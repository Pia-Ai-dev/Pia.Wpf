using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Batch 07 G7 panel attribution (spec S0.7/S4.3/S4.4): before this batch every step row drew an
/// always-empty 20x20 shadowed box, because <c>StepRowViewModel.AssignedPersonaId</c> is <c>Guid?</c> while
/// <c>PiaPersonaAvatar.PersonaIdProperty</c> is a non-nullable <c>Guid</c> DP, and <c>Emoji</c> was never
/// bound at all. This covers the VM-level half of the fix (resolving <c>PersonaId</c>/<c>PersonaEmoji</c>/
/// <c>PersonaAccent</c>/<c>HasPersona</c> from the persona map) — the XAML binding half is manual-smoke
/// debt per this group's brief (no frame-pushing View test).
/// </summary>
public sealed class RunProgressViewModelPersonaAttributionTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SqliteContext _ctx;
    private readonly AssistantChatService _chats;
    private readonly AgentRunService _runs;
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();

    public RunProgressViewModelPersonaAttributionTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
    }

    private async Task<AgentRun> NewPlannedRunAsync()
    {
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = chatId,
            SchemaVersion = 1,
            Title = "t",
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(),
            Messages = [],
        });
        return await _runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "g"));
    }

    private RunProgressViewModel CreateVm(Guid runId, IPersonaService? personaService = null)
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, runId, _loc, _resume, NullLogger.Instance,
            timelineService: null, workspaces: null, personaService: personaService);
    }

    [Fact]
    public async Task StepWithNoAssignedPersona_HasPersonaIsFalse()
    {
        // GUARD: the common case (single-persona run, D1's default) must render exactly as before —
        // no avatar, not an empty box.
        var run = await NewPlannedRunAsync();
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "Step", Status = AgentStepStatus.Pending };
        await _runs.ReplaceStepsAsync(run.Id, [step], TestContext.Current.CancellationToken);

        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(Task.FromResult<IReadOnlyList<Persona>>([]));

        var vm = CreateVm(run.Id, personaService);
        await vm.RefreshAsync();

        var row = Assert.Single(vm.Steps);
        Assert.False(row.HasPersona);
        Assert.Equal(Guid.Empty, row.PersonaId);
    }

    [Fact]
    public async Task StepWithResolvableAssignedPersona_ProjectsPersonaIdEmojiAndAccent()
    {
        var run = await NewPlannedRunAsync();
        var persona = new Persona
        {
            Id = Guid.NewGuid(),
            Name = "Reviewer",
            SystemPrompt = "prompt",
            Emoji = "R",
            AccentColor = "#00FF00",
        };
        var step = new AgentStep
        {
            Id = Guid.NewGuid(), Ordinal = 0, Title = "Step", Status = AgentStepStatus.Pending,
            AssignedPersonaId = persona.Id,
        };
        await _runs.ReplaceStepsAsync(run.Id, [step], TestContext.Current.CancellationToken);

        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(Task.FromResult<IReadOnlyList<Persona>>([persona]));

        var vm = CreateVm(run.Id, personaService);
        await vm.RefreshAsync();

        var row = Assert.Single(vm.Steps);
        Assert.True(row.HasPersona);
        Assert.Equal(persona.Id, row.PersonaId);
        Assert.Equal(persona.Emoji, row.PersonaEmoji);
        Assert.Equal(persona.AccentColor, row.PersonaAccent);
        Assert.Equal(persona.Id, row.AssignedPersonaId); // the raw fact is preserved alongside the projection
    }

    [Fact]
    public async Task StepWithAssignedPersonaThatNoLongerResolves_FallsBackToNoAvatar()
    {
        // D3 arm 2: the persona was deleted between plan and execute. Must never throw and must never
        // show a stale/blank avatar for an id nothing can resolve.
        var run = await NewPlannedRunAsync();
        var deletedPersonaId = Guid.NewGuid();
        var step = new AgentStep
        {
            Id = Guid.NewGuid(), Ordinal = 0, Title = "Step", Status = AgentStepStatus.Pending,
            AssignedPersonaId = deletedPersonaId,
        };
        await _runs.ReplaceStepsAsync(run.Id, [step], TestContext.Current.CancellationToken);

        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(Task.FromResult<IReadOnlyList<Persona>>([]));

        var vm = CreateVm(run.Id, personaService);
        await vm.RefreshAsync();

        var row = Assert.Single(vm.Steps);
        Assert.False(row.HasPersona);
        Assert.Equal(deletedPersonaId, row.AssignedPersonaId); // raw fact untouched
    }

    [Fact]
    public async Task NullPersonaService_NeverResolvesAnyAvatar()
    {
        // GUARD: a null IPersonaService (the trailing-defaulted arm) must degrade to today's rendering
        // minus the always-empty box — never throw, never show an avatar.
        var run = await NewPlannedRunAsync();
        var step = new AgentStep
        {
            Id = Guid.NewGuid(), Ordinal = 0, Title = "Step", Status = AgentStepStatus.Pending,
            AssignedPersonaId = Guid.NewGuid(),
        };
        await _runs.ReplaceStepsAsync(run.Id, [step], TestContext.Current.CancellationToken);

        var vm = CreateVm(run.Id, personaService: null);
        await vm.RefreshAsync();

        var row = Assert.Single(vm.Steps);
        Assert.False(row.HasPersona);
    }

    /// <summary>
    /// T-VM-4, <b>REGRESSION</b> (rewritten by the Phase 3 fix pass — the version G7 shipped stubbed
    /// <c>GetPersonasAsync</c> with an already-completed task, so pass 1 already resolved the avatar and the
    /// assertion held whether or not the existing-row re-application had run at all).
    /// <para>
    /// The property under test is the ONE production path that needs
    /// <c>ApplyPersonaAttribution(existing)</c>: within a single <c>RefreshAsync</c> the map load is awaited
    /// BEFORE the projection, so a new row always has the map in hand. The correction only matters when the
    /// FIRST map read FAULTS — the SQLite-busy case the try/catch exists for — leaving rows minted persona-less,
    /// and a later <c>RunChanged</c> re-reads it successfully. Rows are replaced only when step IDS change, so
    /// without the re-application those rows would never be corrected and a genuinely delegated step would show
    /// no avatar for the rest of the run's life.
    /// </para>
    /// <para>
    /// It also pins the deliberate "<c>_personas</c> stays null so the NEXT event RETRIES rather than latching an
    /// empty map" decision recorded at the field: latch an empty map in the catch and pass 2 no longer corrects.
    /// <c>Assert.Same</c> is the load-bearing assertion — it is what makes this about the SAME row instance being
    /// mutated rather than about a replacement row that happens to be right. Neutralization: delete
    /// <c>ApplyPersonaAttribution(existing)</c> from <c>SyncSteps</c>' else-branch → red.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFaultedFirstPersonaLoad_IsRetried_AndTheSameRowIsCorrectedInPlace()
    {
        var run = await NewPlannedRunAsync();
        var persona = new Persona { Id = Guid.NewGuid(), Name = "Reviewer", SystemPrompt = "prompt" };
        var step = new AgentStep
        {
            Id = Guid.NewGuid(), Ordinal = 0, Title = "Step", Status = AgentStepStatus.Pending,
            AssignedPersonaId = persona.Id,
        };
        await _runs.ReplaceStepsAsync(run.Id, [step], TestContext.Current.CancellationToken);

        var personaService = Substitute.For<IPersonaService>();
        // Call 1 faults (a faulted task, not a synchronous throw — that is what a busy SQLite read looks like
        // from an async method); every later call succeeds.
        personaService.GetPersonasAsync().Returns(
            _ => Task.FromException<IReadOnlyList<Persona>>(new InvalidOperationException("the persona store is busy")),
            _ => Task.FromResult<IReadOnlyList<Persona>>([persona]));

        // The ctor's own RefreshAsync consumes call 1 and, under InlineSyncContext, completes before the ctor
        // returns — so this Single() is also the check that the first projection really happened.
        var vm = CreateVm(run.Id, personaService);
        var row = Assert.Single(vm.Steps);
        Assert.False(row.HasPersona);          // minted persona-less: the map was never loaded
        Assert.Equal(Guid.Empty, row.PersonaId);

        await vm.RefreshAsync();               // the retry succeeds

        Assert.Same(row, Assert.Single(vm.Steps));   // the SAME instance, corrected in place
        Assert.True(row.HasPersona);
        Assert.Equal(persona.Id, row.PersonaId);
    }

    /// <summary>
    /// T-VM-5, <b>REGRESSION</b> (not built by G7; added by the Phase 3 fix pass). A persona-store fault must not
    /// break the panel. <see cref="RunProgressViewModel.RefreshAsync"/> is invoked from the CONSTRUCTOR and from
    /// the off-thread <c>RunChanged</c> handler, so an unguarded fault is either an unobserved task fault on the
    /// event path or a panel that never projects the run at all on the ctor path — for a run whose only defect is
    /// that a persona read was momentarily unavailable.
    /// <para>
    /// Neutralization: remove the try/catch around the map load → this fact reds (the fault escapes
    /// <c>RefreshAsync</c>). The step assertions are the non-vacuity half: the panel is not merely
    /// exception-free, it still shows the run.
    /// </para>
    /// </summary>
    [Fact]
    public async Task APersonaLookupFault_DoesNotBreakThePanel()
    {
        var run = await NewPlannedRunAsync();
        var assigned = Guid.NewGuid();
        var steps = new List<AgentStep>
        {
            new() { Id = Guid.NewGuid(), Ordinal = 0, Title = "One", Status = AgentStepStatus.Done, AssignedPersonaId = assigned },
            new() { Id = Guid.NewGuid(), Ordinal = 1, Title = "Two", Status = AgentStepStatus.Pending },
        };
        await _runs.ReplaceStepsAsync(run.Id, steps, TestContext.Current.CancellationToken);

        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(
            _ => Task.FromException<IReadOnlyList<Persona>>(new InvalidOperationException("the persona store is busy")));

        var vm = CreateVm(run.Id, personaService);
        await vm.RefreshAsync();   // must not throw

        Assert.Equal(2, vm.Steps.Count);
        Assert.Equal(AgentStepStatus.Done, vm.Steps[0].Status);
        Assert.Equal(AgentStepStatus.Pending, vm.Steps[1].Status);
        Assert.All(vm.Steps, r => Assert.False(r.HasPersona));
    }

    public void Dispose()
    {
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, true); } catch { /* best effort cleanup */ }
    }
}

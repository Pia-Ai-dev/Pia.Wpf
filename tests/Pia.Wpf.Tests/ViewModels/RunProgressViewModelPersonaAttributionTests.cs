using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels;
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

    private sealed class InlineSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
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

    [Fact]
    public async Task FirstProjectionBeforePersonaMapLoads_IsCorrectedOnTheNextRefresh()
    {
        // R21/R22: RefreshAsync runs from the constructor, so the very first projection can, in
        // principle, race the persona map load. This VM's own RefreshAsync is awaited synchronously
        // here (InlineSyncContext), so we assert the settled state after two explicit refreshes rather
        // than the ctor's fire-and-forget one — the load-bearing property under test is that PersonaId
        // is SETTABLE and gets corrected on a later pass, not a particular race outcome.
        var run = await NewPlannedRunAsync();
        var persona = new Persona { Id = Guid.NewGuid(), Name = "Reviewer", SystemPrompt = "prompt" };
        var step = new AgentStep
        {
            Id = Guid.NewGuid(), Ordinal = 0, Title = "Step", Status = AgentStepStatus.Pending,
            AssignedPersonaId = persona.Id,
        };
        await _runs.ReplaceStepsAsync(run.Id, [step], TestContext.Current.CancellationToken);

        var personaService = Substitute.For<IPersonaService>();
        personaService.GetPersonasAsync().Returns(Task.FromResult<IReadOnlyList<Persona>>([persona]));

        var vm = CreateVm(run.Id, personaService);
        await vm.RefreshAsync(); // first explicit pass: loads the persona map and applies it
        await vm.RefreshAsync(); // second pass: same row, must still resolve (not a load-once latch)

        var row = Assert.Single(vm.Steps);
        Assert.True(row.HasPersona);
        Assert.Equal(persona.Id, row.PersonaId);
    }

    public void Dispose()
    {
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, true); } catch { /* best effort cleanup */ }
    }
}

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

/// <summary>Covers the VM half of panel persona attribution; the XAML binding half is manual-smoke debt.</summary>
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
        // GUARD: the common single-persona run must render no avatar rather than an empty box.
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
        // The persona was deleted between plan and execute: never throw, never show a blank avatar for an
        // id nothing can resolve.
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

    /// <summary>Rows are replaced only when step IDs change, so a row minted while the map read faulted must be corrected in place.</summary>
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

    /// <summary>RefreshAsync runs from the ctor and from the off-thread RunChanged handler, so an unguarded persona-store fault loses the panel.</summary>
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

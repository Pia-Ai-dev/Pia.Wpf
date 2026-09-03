using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

public sealed class AgentRunServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _dbPath;
    private readonly SqliteContext _ctx;
    private readonly AssistantChatService _chats;
    private readonly AgentRunService _service;

    public AgentRunServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _dbPath = Path.Combine(_tmpDir, "history.db");
        _ctx = new SqliteContext(_dbPath);
        _service = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _service);
    }

    [Fact]
    public void Schema_CreatesAgentTables_AndIsIdempotentOnReopen()
    {
        Assert.True(TableExists(_ctx.GetConnection(), "AgentRuns"));
        Assert.True(TableExists(_ctx.GetConnection(), "AgentSteps"));

        using var reopened = new SqliteContext(_dbPath);
        var conn = reopened.GetConnection();
        Assert.True(TableExists(conn, "AgentRuns"));
        Assert.True(TableExists(conn, "AgentSteps"));
    }

    [Fact]
    public async Task CreateAsync_SingleTurn_StartsRunning_WithStartedAt()
    {
        var chatId = await MakeChatAsync();

        var run = await _service.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.SingleTurn, AgentRunTrigger.User, Goal: "do the thing"), TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunState.Running, run.State);
        Assert.NotNull(run.StartedAt);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(AgentRunState.Running, fetched!.State);
        Assert.Equal(chatId, fetched.ChatId);
        Assert.NotNull(fetched.StartedAt);
    }

    [Fact]
    public async Task CreateAsync_Planned_StartsPlanning()
    {
        var chatId = await MakeChatAsync();

        var run = await _service.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunState.Planning, run.State);
    }

    [Fact]
    public async Task CreateAsync_PersistsTriggerProvenance()
    {
        var chatId = await MakeChatAsync();
        var jobId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        var run = await _service.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.SingleTurn, AgentRunTrigger.Schedule, jobId, deviceId, "goal"), TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunTrigger.Schedule, fetched!.TriggerKind);
        Assert.Equal(jobId, fetched.TriggerRef);
        Assert.Equal(deviceId, fetched.OwnerDeviceId);
    }

    [Fact]
    public async Task AddUsageAsync_AccruesRunLevelLedger()
    {
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        await _service.AddUsageAsync(run.Id, null, new UsageDetails { InputTokenCount = 10, OutputTokenCount = 4 }, TestContext.Current.CancellationToken);
        await _service.AddUsageAsync(run.Id, null, new UsageDetails { InputTokenCount = 5, OutputTokenCount = 1 }, TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(fetched!.LedgerJson!);
        Assert.Equal(15, doc.RootElement.GetProperty("inputTokens").GetInt64());
        Assert.Equal(5, doc.RootElement.GetProperty("outputTokens").GetInt64());
    }

    [Fact]
    public async Task AddUsageAsync_WithStepId_AccruesPerStepAndGrandTotal()
    {
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "A", Status = AgentStepStatus.Pending };
        await _service.ReplaceStepsAsync(run.Id, new[] { step }, TestContext.Current.CancellationToken);

        await _service.AddUsageAsync(run.Id, step.Id, new UsageDetails { InputTokenCount = 10, OutputTokenCount = 4 }, TestContext.Current.CancellationToken);
        await _service.AddUsageAsync(run.Id, step.Id, new UsageDetails { InputTokenCount = 5, OutputTokenCount = 1 }, TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(fetched!.LedgerJson!);

        Assert.Equal(15, doc.RootElement.GetProperty("inputTokens").GetInt64());
        Assert.Equal(5, doc.RootElement.GetProperty("outputTokens").GetInt64());

        var perStep = doc.RootElement.GetProperty("perStep");
        Assert.Equal(1, perStep.GetArrayLength());
        Assert.Equal(step.Id.ToString(), perStep[0].GetProperty("stepId").GetString());
        Assert.Equal(15, perStep[0].GetProperty("inputTokens").GetInt64());
        Assert.Equal(5, perStep[0].GetProperty("outputTokens").GetInt64());
    }

    [Fact]
    public async Task CompleteAsync_TransitionsToCompleted_WithCompletedAt()
    {
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        await _service.CompleteAsync(run.Id, ct: TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, fetched!.State);
        Assert.NotNull(fetched.CompletedAt);
    }

    [Fact]
    public async Task CompleteAsync_Truncated_WritesTruncatedMarker()
    {
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        await _service.CompleteAsync(run.Id, truncated: true, truncationReason: "budget", ct: TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, fetched!.State);
        using var doc = JsonDocument.Parse(fetched.ExtraJson!);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("budget", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task FailAsync_TransitionsToFailed()
    {
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        await _service.FailAsync(run.Id, "boom", ct: TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Failed, fetched!.State);
        Assert.NotNull(fetched.CompletedAt);
    }

    [Fact]
    public async Task FailAsync_Cancelled_TransitionsToCancelled()
    {
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        await _service.FailAsync(run.Id, null, cancelled: true, ct: TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Cancelled, fetched!.State);
    }

    [Fact]
    public async Task FailInterruptedRunsAsync_SettlesNonTerminalRuns_LeavesTerminalUntouched()
    {
        var ct = TestContext.Current.CancellationToken;

        var planning = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        var running = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.SetStateAsync(running.Id, AgentRunState.Running, ct);
        var verifying = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.SetStateAsync(verifying.Id, AgentRunState.Verifying, ct);

        var waiting = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.SetStateAsync(waiting.Id, AgentRunState.WaitingForInput, ct);
        var paused = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.SetStateAsync(paused.Id, AgentRunState.Paused, ct);

        var completed = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.CompleteAsync(completed.Id, ct: ct);
        var failed = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.SingleTurn, AgentRunTrigger.User), ct);
        await _service.FailAsync(failed.Id, "boom", ct: ct);

        var settled = await _service.FailInterruptedRunsAsync(ct);

        Assert.Equal(3, settled); // only Planning/Running/Verifying swept — parked runs excluded
        Assert.Equal(AgentRunState.Cancelled, (await _service.GetAsync(planning.Id, ct))!.State);
        Assert.Equal(AgentRunState.Cancelled, (await _service.GetAsync(running.Id, ct))!.State);
        Assert.Equal(AgentRunState.Cancelled, (await _service.GetAsync(verifying.Id, ct))!.State);
        Assert.NotNull((await _service.GetAsync(planning.Id, ct))!.CompletedAt);
        // A budget park is deliberate, so it survives the sweep resumable.
        Assert.Equal(AgentRunState.WaitingForInput, (await _service.GetAsync(waiting.Id, ct))!.State);
        Assert.Equal(AgentRunState.Paused, (await _service.GetAsync(paused.Id, ct))!.State);
        Assert.Equal(AgentRunState.Completed, (await _service.GetAsync(completed.Id, ct))!.State);
        Assert.Equal(AgentRunState.Failed, (await _service.GetAsync(failed.Id, ct))!.State);

        Assert.Equal(0, await _service.FailInterruptedRunsAsync(ct));
    }

    [Fact]
    public async Task PauseAsync_WritesMarker_NoCompletedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.SetStateAsync(run.Id, AgentRunState.Running, ct);

        await _service.PauseAsync(run.Id, "step-cap", ct);

        var fetched = await _service.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.WaitingForInput, fetched!.State);
        Assert.Null(fetched.CompletedAt); // pause is NOT terminal
        Assert.Contains("paused", fetched.ExtraJson ?? string.Empty);
        Assert.Contains("step-cap", fetched.ExtraJson ?? string.Empty);
    }

    [Fact]
    public async Task TryBeginResume_OnlyOneRacerWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.PauseAsync(run.Id, "step-cap", ct);

        // Two racers CAS-claim the same parked run; exactly one wins — never two loops.
        var a = _service.TryBeginResumeAsync(run.Id, ct);
        var b = _service.TryBeginResumeAsync(run.Id, ct);
        var results = await Task.WhenAll(a, b);

        Assert.Single(results, r => r);
        Assert.Equal(AgentRunState.Running, (await _service.GetAsync(run.Id, ct))!.State);
    }

    [Fact]
    public async Task TryBeginResume_NonWaitingRun_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.SetStateAsync(run.Id, AgentRunState.Running, ct);

        Assert.False(await _service.TryBeginResumeAsync(run.Id, ct));
        Assert.Equal(AgentRunState.Running, (await _service.GetAsync(run.Id, ct))!.State);
    }

    [Fact]
    public async Task CreateAsync_BeforeChatRow_ThrowsFkConstraint()
    {
        // FK enforcement is ON — a run row cannot precede its AssistantChats parent.
        var orphanChatId = Guid.NewGuid();

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await _service.CreateAsync(new AgentRunCreateRequest(orphanChatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletingChat_CascadesRunsAndSteps()
    {
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await _service.ReplaceStepsAsync(run.Id, new[]
        {
            new AgentStep { Ordinal = 0, Title = "a", Status = AgentStepStatus.Pending },
            new AgentStep { Ordinal = 1, Title = "b", Status = AgentStepStatus.Pending },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, RawCount("AgentRuns", "ChatId", chatId));
        Assert.Equal(2, RawCount("AgentSteps", "RunId", run.Id));

        await _chats.DeleteAsync(chatId, TestContext.Current.CancellationToken);

        Assert.Equal(0, RawCount("AgentRuns", "ChatId", chatId));
        Assert.Equal(0, RawCount("AgentSteps", "RunId", run.Id));
        Assert.Empty(await _service.GetByChatAsync(chatId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChatHasPlannedRunAsync_TrueOnlyForPlanned()
    {
        var chatId = await MakeChatAsync();
        await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken);
        Assert.False(await _service.ChatHasPlannedRunAsync(chatId, TestContext.Current.CancellationToken));

        await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);
        Assert.True(await _service.ChatHasPlannedRunAsync(chatId, TestContext.Current.CancellationToken));
    }

    // WaitingForChildren sorts ABOVE the terminal band, so a range predicate reds that leg alone. Parked runs are
    // excluded on purpose: a park needs a human, so counting one as live would silence a recurring job forever.
    [Fact]
    public async Task AnyExecutingRunForTriggerAsync_TrueForEveryExecutingState_FalseForParkedAndTerminal()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatId = await MakeChatAsync();
        var jobId = Guid.NewGuid();

        var run = await _service.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, AgentRunTrigger.Schedule, jobId, null, "goal"), ct);

        // Planning is the state a launch has already reached when LaunchAsync returns, before any step ran.
        Assert.Equal(AgentRunState.Planning, run.State);
        Assert.True(await _service.AnyExecutingRunForTriggerAsync(jobId, ct));

        foreach (var executing in new[]
                 { AgentRunState.Running, AgentRunState.Verifying, AgentRunState.WaitingForChildren })
        {
            await _service.SetStateAsync(run.Id, executing, ct);
            Assert.True(await _service.AnyExecutingRunForTriggerAsync(jobId, ct), $"{executing} is executing");
        }

        foreach (var settledOrParked in new[]
                 {
                     AgentRunState.WaitingForInput, AgentRunState.Paused,
                     AgentRunState.Completed, AgentRunState.Failed, AgentRunState.Cancelled,
                 })
        {
            await _service.SetStateAsync(run.Id, settledOrParked, ct);
            Assert.False(await _service.AnyExecutingRunForTriggerAsync(jobId, ct), $"{settledOrParked} is not executing");
        }

        // Scoped to the trigger: a live run answers for ITS job and no other. Child runs carry a null
        // TriggerRef for the same reason, so a fan-out's descendants never answer for their parent's job.
        await _service.SetStateAsync(run.Id, AgentRunState.Running, ct);
        Assert.True(await _service.AnyExecutingRunForTriggerAsync(jobId, ct));
        Assert.False(await _service.AnyExecutingRunForTriggerAsync(Guid.NewGuid(), ct));

        var noTrigger = await _service.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), ct);
        Assert.Null((await _service.GetAsync(noTrigger.Id, ct))!.TriggerRef);
    }

    [Fact]
    public async Task GetByChatAsync_ReturnsAllRunsForChat_InCreationOrder()
    {
        var chatId = await MakeChatAsync();
        var r1 = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken);
        var r2 = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        var runs = await _service.GetByChatAsync(chatId, TestContext.Current.CancellationToken);
        Assert.Equal(2, runs.Count);
        Assert.Equal(r1.Id, runs[0].Id);
        Assert.Equal(r2.Id, runs[1].Id);
    }

    [Fact]
    public async Task NextPendingStepAsync_ReQueriesPersistedSteps_NotASnapshot()
    {
        // The loop must pick up steps written by a later ReplaceStepsAsync (replan), not iterate a stale snapshot.
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        var stepA = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "A", Status = AgentStepStatus.Pending };
        var stepB = new AgentStep { Id = Guid.NewGuid(), Ordinal = 1, Title = "B", Status = AgentStepStatus.Pending };
        await _service.ReplaceStepsAsync(run.Id, new[] { stepA, stepB }, TestContext.Current.CancellationToken);

        var next = await _service.NextPendingStepAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal("A", next!.Title);

        await _service.SetStepStatusAsync(stepA.Id, AgentStepStatus.Done, TestContext.Current.CancellationToken);
        next = await _service.NextPendingStepAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal("B", next!.Title);

        var stepC = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "C", Status = AgentStepStatus.Pending };
        await _service.ReplaceStepsAsync(run.Id, new[] { stepC }, TestContext.Current.CancellationToken);

        next = await _service.NextPendingStepAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal("C", next!.Title);
    }

    [Fact]
    public async Task RecordStepResultAsync_AccruesPerStepLedger()
    {
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "A", Status = AgentStepStatus.Pending };
        await _service.ReplaceStepsAsync(run.Id, new[] { step }, TestContext.Current.CancellationToken);

        await _service.RecordStepResultAsync(step.Id, AgentStepStatus.Done, Guid.NewGuid(), Guid.NewGuid(),
            new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 }, TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(fetched!.LedgerJson!);
        Assert.Equal(7, doc.RootElement.GetProperty("inputTokens").GetInt64());
        var perStep = doc.RootElement.GetProperty("perStep");
        Assert.Equal(1, perStep.GetArrayLength());
        Assert.Equal(7, perStep[0].GetProperty("inputTokens").GetInt64());

        var doneStep = Assert.Single(fetched.Plan);
        Assert.Equal(AgentStepStatus.Done, doneStep.Status);
    }

    // ---- the artifact a step reported, merged into ExtraJson so it survives a park and resume ----

    [Fact]
    public async Task RecordStepResultAsync_PersistsTheArtifactRefIntoExtraJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "A", Status = AgentStepStatus.Pending };
        await _service.ReplaceStepsAsync(run.Id, new[] { step }, ct);

        await _service.RecordStepResultAsync(step.Id, AgentStepStatus.Done, Guid.NewGuid(), Guid.NewGuid(), null, ct, "out/q3.md");

        var persisted = Assert.Single((await _service.GetAsync(run.Id, ct))!.Plan);
        var extras = Assert.IsType<JsonObject>(JsonNode.Parse(persisted.ExtraJson!));
        Assert.Equal("out/q3.md", extras["artifactRef"]!.GetValue<string>());
    }

    [Fact]
    public async Task RecordStepResultAsync_MergesIntoTheParallelGroupMarker_WithoutClobberingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        var step = new AgentStep
        {
            Id = Guid.NewGuid(), Ordinal = 0, Title = "A", Status = AgentStepStatus.Pending,
            ExtraJson = """{"parallelGroup":2}""",
        };
        await _service.ReplaceStepsAsync(run.Id, new[] { step }, ct);

        await _service.RecordStepResultAsync(step.Id, AgentStepStatus.Done, Guid.NewGuid(), Guid.NewGuid(), null, ct, "out/q3.md");

        var persisted = Assert.Single((await _service.GetAsync(run.Id, ct))!.Plan);
        // Read through the real consumers, so "never clobbers the marker" is load-bearing rather than textual.
        Assert.Equal(2, AgentRunOrchestrator.ParallelGroupOf(persisted));
        Assert.Equal("out/q3.md", StepExtraJson.ArtifactRefOf(persisted));
    }

    [Fact]
    public async Task RecordStepResultAsync_WithoutAnArtifactRef_LeavesExtraJsonByteIdentical()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        var step = new AgentStep
        {
            Id = Guid.NewGuid(), Ordinal = 0, Title = "A", Status = AgentStepStatus.Pending,
            ExtraJson = """{"parallelGroup":2}""",
        };
        await _service.ReplaceStepsAsync(run.Id, new[] { step }, ct);

        await _service.RecordStepResultAsync(step.Id, AgentStepStatus.Done, Guid.NewGuid(), Guid.NewGuid(), null, ct);

        var persisted = Assert.Single((await _service.GetAsync(run.Id, ct))!.Plan);
        Assert.Equal("""{"parallelGroup":2}""", persisted.ExtraJson); // no reserialization, no artifactRef:null
    }

    [Fact]
    public async Task RecordStepResultAsync_MalformedExtraJson_StillPersistsTheArtifactRef()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        var step = new AgentStep
        {
            Id = Guid.NewGuid(), Ordinal = 0, Title = "A", Status = AgentStepStatus.Pending, ExtraJson = "not json",
        };
        await _service.ReplaceStepsAsync(run.Id, new[] { step }, ct);

        await _service.RecordStepResultAsync(step.Id, AgentStepStatus.Done, Guid.NewGuid(), Guid.NewGuid(), null, ct, "out/q3.md");

        var persisted = Assert.Single((await _service.GetAsync(run.Id, ct))!.Plan);
        Assert.Equal("""{"artifactRef":"out/q3.md"}""", persisted.ExtraJson);
        Assert.Equal(AgentStepStatus.Done, persisted.Status);
    }

    [Fact]
    public async Task RecordStepResultAsync_StillAccruesTheLedgerAndStatus_WhenAnArtifactRefIsPresent()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "A", Status = AgentStepStatus.Pending };
        await _service.ReplaceStepsAsync(run.Id, new[] { step }, ct);

        await _service.RecordStepResultAsync(step.Id, AgentStepStatus.Done, Guid.NewGuid(), Guid.NewGuid(),
            new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 }, ct, "out/q3.md");

        var fetched = (await _service.GetAsync(run.Id, ct))!;
        Assert.Equal((7L, 3L), TokenTotals(run.Id));
        using var doc = JsonDocument.Parse(fetched.LedgerJson!);
        var perStep = doc.RootElement.GetProperty("perStep");
        Assert.Equal(1, perStep.GetArrayLength());
        Assert.Equal(7, perStep[0].GetProperty("inputTokens").GetInt64());
        Assert.Equal(AgentStepStatus.Done, Assert.Single(fetched.Plan).Status);
    }

    // ---- the ledger clock measures ACTIVE time, never the parked gap ----

    [Fact]
    public async Task Ledger_WallClock_ExcludesParkedGap_AndIsMonotonicAcrossTwoPauseResumeCycles()
    {
        // WallClockMs is accumulated WORKED time. StartedAt is deliberately back-dated below because
        // (UtcNow - StartedAt) is the formula that used to bill the whole parked span.
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);

        Assert.NotNull(SegmentStartedAt(run.Id)); // create opens the first segment
        BackdateOpenSegment(run.Id, TimeSpan.FromSeconds(4));
        await _service.PauseAsync(run.Id, "step-cap", ct);

        var afterFirstPause = WallClockMs(run.Id);
        Assert.InRange(afterFirstPause, 4_000, 60_000);
        Assert.Equal(afterFirstPause, ActiveMs(run.Id));
        Assert.Null(SegmentStartedAt(run.Id)); // parked → no open segment

        // Parked overnight. StartedAt is never advanced by the resume path, so this is the poisoned input.
        SetStartedAt(run.Id, DateTime.UtcNow - TimeSpan.FromHours(12));

        Assert.True(await _service.TryBeginResumeAsync(run.Id, ct));
        Assert.NotNull(SegmentStartedAt(run.Id));                 // claim opened a fresh segment
        Assert.Equal(afterFirstPause, ActiveMs(run.Id));          // the 12h gap accrued nothing
        Assert.InRange(WallClockMs(run.Id), afterFirstPause, afterFirstPause + 60_000);

        // A usage accrual mid-segment reports the live total without billing the gap either.
        await _service.AddUsageAsync(run.Id, null, new UsageDetails { InputTokenCount = 3, OutputTokenCount = 1 }, ct);
        Assert.InRange(WallClockMs(run.Id), afterFirstPause, afterFirstPause + 60_000);
        Assert.Equal(afterFirstPause, ActiveMs(run.Id));          // Refresh must not fold the segment in

        // Park again — the accumulator only ever grows.
        BackdateOpenSegment(run.Id, TimeSpan.FromSeconds(6));
        await _service.PauseAsync(run.Id, "step-cap", ct);

        var afterSecondPause = WallClockMs(run.Id);
        Assert.InRange(afterSecondPause, afterFirstPause + 6_000, afterFirstPause + 66_000);
        Assert.True(afterSecondPause < (long)TimeSpan.FromHours(1).TotalMilliseconds,
            "the 12h parked gap must never reach the reported wall clock");
    }

    [Fact]
    public async Task Ledger_WallClock_ExcludesParkedGap_OnTheStepResultAccrualSiteToo()
    {
        // The HOT accrual site: every completed step goes through RecordStepResultAsync, so a regression that
        // restored `WallClockMs = ElapsedMs(startedAt)` there would re-import the parked gap with the rest green.
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "A", Status = AgentStepStatus.Pending };
        await _service.ReplaceStepsAsync(run.Id, new[] { step }, ct);

        BackdateOpenSegment(run.Id, TimeSpan.FromSeconds(3));
        await _service.PauseAsync(run.Id, "step-cap", ct);
        var parked = WallClockMs(run.Id);
        Assert.InRange(parked, 3_000, 60_000);

        // Parked 12h. StartedAt is written once at create and never advanced — the poisoned input.
        SetStartedAt(run.Id, DateTime.UtcNow - TimeSpan.FromHours(12));
        Assert.True(await _service.TryBeginResumeAsync(run.Id, ct));

        await _service.RecordStepResultAsync(step.Id, AgentStepStatus.Done, Guid.NewGuid(), Guid.NewGuid(),
            new UsageDetails { InputTokenCount = 5, OutputTokenCount = 2 }, ct);

        Assert.InRange(WallClockMs(run.Id), parked, parked + 60_000);
        Assert.Equal(parked, ActiveMs(run.Id)); // Refresh reports the open segment without folding it in
        Assert.True(WallClockMs(run.Id) < (long)TimeSpan.FromHours(1).TotalMilliseconds,
            "the 12h parked gap must never reach the reported wall clock");
        Assert.Equal(5, TokenTotals(run.Id).Input); // the token half of the same write still accrues
    }

    [Fact]
    public async Task LedgerClockFault_IsSwallowed_AndTheStateWriteStillLands()
    {
        // MoveLedgerClock runs BEFORE the state UPDATE, so an unguarded fault there would leave a run dangling
        // Running. Forced with an unparseable StartedAt, which makes the ledger read throw inside MoveLedgerClock.
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        SetRawStartedAt(run.Id, "not-a-timestamp");

        await _service.PauseAsync(run.Id, "step-cap", ct);
        Assert.Equal((long)AgentRunState.WaitingForInput, RawState(run.Id)); // parked despite the ledger fault

        await _service.CompleteAsync(run.Id, ct: ct);
        Assert.Equal((long)AgentRunState.Completed, RawState(run.Id));       // and it can still settle
    }

    [Fact]
    public async Task TryBeginResume_Loser_DoesNotReopenTheLedgerSegment()
    {
        // Only the CAS winner opens a work segment — a second claim must leave the clock alone
        // (otherwise two racers could each restart the segment and lose accrued active time).
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.PauseAsync(run.Id, "step-cap", ct);

        Assert.True(await _service.TryBeginResumeAsync(run.Id, ct));
        var openedAt = SegmentStartedAt(run.Id);
        Assert.NotNull(openedAt);

        Assert.False(await _service.TryBeginResumeAsync(run.Id, ct)); // already Running → lost
        Assert.Equal(openedAt, SegmentStartedAt(run.Id));
    }

    [Fact]
    public async Task CompleteAsync_FreezesWallClock_AndLaterWritesDoNotGrowIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.SingleTurn, AgentRunTrigger.User), ct);

        BackdateOpenSegment(run.Id, TimeSpan.FromSeconds(3));
        await _service.CompleteAsync(run.Id, ct: ct);

        var frozen = WallClockMs(run.Id);
        Assert.InRange(frozen, 3_000, 60_000);
        Assert.Null(SegmentStartedAt(run.Id));

        // A terminal run has no open segment, so a late usage accrual (or a repeated terminal write)
        // must accrue tokens without moving the clock — even with a back-dated StartedAt.
        SetStartedAt(run.Id, DateTime.UtcNow - TimeSpan.FromHours(12));
        await _service.AddUsageAsync(run.Id, null, new UsageDetails { InputTokenCount = 2, OutputTokenCount = 1 }, ct);
        Assert.Equal(frozen, WallClockMs(run.Id));

        await _service.CompleteAsync(run.Id, ct: ct);
        Assert.Equal(frozen, WallClockMs(run.Id));
    }

    [Fact]
    public async Task SweptRun_StaleOpenSegment_IsDroppedNotBilled()
    {
        // The startup sweep settles runs in bulk and deliberately does not touch ledgers, so a swept run keeps an
        // OPEN segment forever; any later ledger write must drop it.
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.SingleTurn, AgentRunTrigger.User), ct);

        BackdateOpenSegment(run.Id, TimeSpan.FromHours(5)); // "crashed" 5h ago with the segment open
        Assert.Equal(1, await _service.FailInterruptedRunsAsync(ct));

        await _service.AddUsageAsync(run.Id, null, new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 }, ct);

        Assert.Equal(0, WallClockMs(run.Id));   // the 5h of downtime is not worked time
        Assert.Equal(0, ActiveMs(run.Id));
        Assert.Null(SegmentStartedAt(run.Id));  // stale segment cleared
    }

    [Fact]
    public async Task LegacyLedger_ParkedRun_SeedsFromReportedTotal_ThenAccumulatesActiveTime()
    {
        // A ledger persisted before active-time tracking has neither activeMs nor segmentStartedAt; a non-terminal
        // one seeds the accumulator ONCE from its last reported total.
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.PauseAsync(run.Id, "step-cap", ct);

        WriteRawLedger(run.Id, """{"inputTokens":10,"outputTokens":2,"wallClockMs":5000,"perStep":[]}""");
        SetStartedAt(run.Id, DateTime.UtcNow - TimeSpan.FromHours(12));
        Assert.Null(ActiveMs(run.Id));
        Assert.Null(SegmentStartedAt(run.Id));

        Assert.True(await _service.TryBeginResumeAsync(run.Id, ct));
        Assert.Equal(5_000, ActiveMs(run.Id)); // seeded from the legacy reported total, not from StartedAt

        BackdateOpenSegment(run.Id, TimeSpan.FromSeconds(2));
        await _service.PauseAsync(run.Id, "step-cap", ct);

        var reported = WallClockMs(run.Id);
        Assert.InRange(reported, 7_000, 67_000); // 5s legacy + ~2s of new work, never the 12h gap
        Assert.Equal(reported, ActiveMs(run.Id));
        Assert.Equal(10, TokenTotals(run.Id).Input); // token accrual is untouched by the upgrade
    }

    [Fact]
    public async Task LegacyLedger_WithoutReportedTotal_SeedsFromStartedAt()
    {
        // A legacy ledger that never accrued (wallClockMs 0) has only StartedAt to go on, so the run's whole life
        // so far counts as active.
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.SetStateAsync(run.Id, AgentRunState.Running, ct);

        WriteRawLedger(run.Id, """{"inputTokens":0,"outputTokens":0,"wallClockMs":0,"perStep":[]}""");
        SetStartedAt(run.Id, DateTime.UtcNow - TimeSpan.FromSeconds(90));

        await _service.AddUsageAsync(run.Id, null, new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 }, ct);

        Assert.InRange(WallClockMs(run.Id), 90_000, 150_000);
        Assert.InRange(ActiveMs(run.Id) ?? 0, 90_000, 150_000);
    }

    [Fact]
    public async Task LegacyLedger_TerminalRun_WallClockNeverChanges()
    {
        // A terminal legacy run is history: re-deriving it from StartedAt would inflate an archived
        // run, so its reported total is left exactly as persisted.
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.SingleTurn, AgentRunTrigger.User), ct);
        await _service.CompleteAsync(run.Id, ct: ct);

        WriteRawLedger(run.Id, """{"inputTokens":10,"outputTokens":2,"wallClockMs":5000,"perStep":[]}""");
        SetStartedAt(run.Id, DateTime.UtcNow - TimeSpan.FromHours(12));

        await _service.AddUsageAsync(run.Id, null, new UsageDetails { InputTokenCount = 5, OutputTokenCount = 1 }, ct);

        Assert.Equal(5_000, WallClockMs(run.Id));      // frozen
        Assert.Null(ActiveMs(run.Id));                 // stays legacy — nothing to upgrade
        Assert.Equal(15, TokenTotals(run.Id).Input);   // usage still accrues (bookkeeping unaffected)

        await _service.FailAsync(run.Id, "late failure", ct: ct);
        Assert.Equal(5_000, WallClockMs(run.Id));
    }

    // ---- the launch grant envelope round-trips as an opaque string ----

    [Fact]
    public async Task CreateAsync_PolicyJson_RoundTripsThroughGetAndGetByChat()
    {
        // The resume path needs the launch envelope back verbatim (it hardcodes wide grants without
        // it). The service stores it opaquely — no parsing, no reshaping.
        var ct = TestContext.Current.CancellationToken;
        var chatId = await MakeChatAsync();
        const string envelope = """{"grants":["write_file"],"v":1}""";

        var run = await _service.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, AgentRunTrigger.Schedule, Goal: "g", PolicyJson: envelope), ct);

        Assert.Equal(envelope, run.PolicyJson);
        Assert.Equal(envelope, (await _service.GetAsync(run.Id, ct))!.PolicyJson);
        var byChat = Assert.Single(await _service.GetByChatAsync(chatId, ct));
        Assert.Equal(envelope, byChat.PolicyJson);
    }

    [Fact]
    public async Task CreateAsync_WithoutPolicyJson_StaysNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatId = await MakeChatAsync();

        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), ct);

        Assert.Null(run.PolicyJson);
        Assert.Null((await _service.GetAsync(run.Id, ct))!.PolicyJson);
        Assert.Null(Assert.Single(await _service.GetByChatAsync(chatId, ct)).PolicyJson);
    }

    [Fact]
    public async Task CreateAsync_RoundTripsParentRunId()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatId = await MakeChatAsync();

        var parent = await _service.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "parent goal"), ct);
        var child = await _service.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "child goal", ParentRunId: parent.Id), ct);

        // The in-memory object is what a fresh launch hands to the orchestrator; the row is never re-read first.
        Assert.Equal(parent.Id, child.ParentRunId);
        Assert.Equal(parent.Id, (await _service.GetAsync(child.Id, ct))!.ParentRunId);

        // A top-level run stays null in both places: absence is the default and must not become Guid.Empty.
        Assert.Null(parent.ParentRunId);
        Assert.Null((await _service.GetAsync(parent.Id, ct))!.ParentRunId);
    }

    // The four pre-existing indexes are asserted alongside, so a typo'd or deleted CREATE INDEX cannot pass by
    // making the lookup match nothing.
    [Fact]
    public void TheParentRunIdIndexExists()
    {
        var conn = _ctx.GetConnection();

        Assert.True(IndexExists(conn, "IX_AgentRuns_ParentRunId"));

        Assert.True(IndexExists(conn, "IX_AgentRuns_ChatId"));
        Assert.True(IndexExists(conn, "IX_AgentRuns_State"));
        Assert.True(IndexExists(conn, "IX_AgentRuns_UpdatedAt"));
        Assert.True(IndexExists(conn, "IX_AgentRuns_TriggerRef"));
    }

    // Creation order and settle order deliberately DISAGREE: a "take any row per group" implementation would
    // still be green if they agreed.
    [Fact]
    public async Task GetLatestSettledFiringsAsync_ReturnsTheMostRecentlySettledRunPerTrigger_AndIgnoresParkedRunsAndNullTriggerRefs()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobA = Guid.NewGuid();
        var jobB = Guid.NewGuid();

        // Two firings of job A, in two chats so the returned ChatId identifies WHICH one came back.
        var firstChat = await MakeChatAsync();
        var first = await _service.CreateAsync(new AgentRunCreateRequest(
            firstChat, RunShape.Planned, AgentRunTrigger.Schedule, jobA, null, "older row, newer settle"), ct);
        var secondChat = await MakeChatAsync();
        var second = await _service.CreateAsync(new AgentRunCreateRequest(
            secondChat, RunShape.Planned, AgentRunTrigger.Schedule, jobA, null, "newer row, older settle"), ct);

        await _service.FailAsync(second.Id, "boom", cancelled: false, ct);
        await _service.CompleteAsync(first.Id, ct: ct);
        // Forced, not merely sequenced: two UtcNow stamps microseconds apart make an ordering fact a coin toss.
        SetCompletedAt(second.Id, DateTime.UtcNow.AddHours(-2));
        SetCompletedAt(first.Id, DateTime.UtcNow.AddHours(-1));

        // Job B's only run is PARKED — non-terminal, and it also has no CompletedAt.
        var parkedChat = await MakeChatAsync();
        var parked = await _service.CreateAsync(new AgentRunCreateRequest(
            parkedChat, RunShape.Planned, AgentRunTrigger.Schedule, jobB, null, "parked"), ct);
        await _service.PauseAsync(parked.Id, "step-cap", ct);

        // A settled run that is nobody's firing: no TriggerRef at all.
        var loose = await _service.CreateAsync(new AgentRunCreateRequest(
            parkedChat, RunShape.SingleTurn, AgentRunTrigger.User, Goal: "detached"), ct);
        await _service.CompleteAsync(loose.Id, ct: ct);

        var firings = await _service.GetLatestSettledFiringsAsync(ct);

        var only = Assert.Single(firings);
        Assert.Equal(jobA, only.JobId);
        Assert.Equal(first.Id, only.RunId);            // the LATEST settle, not the first row of the group
        Assert.Equal(firstChat, only.ChatId);          // the bare columns came off the SAME row as the MAX
        Assert.Equal(AgentRunState.Completed, only.State);
        // UTC, and normalized as such: the record's whole contract is that the caller never has to guess a kind.
        Assert.Equal(DateTimeKind.Utc, only.SettledAtUtc.Kind);
        Assert.Equal(DateTime.UtcNow.AddHours(-1), only.SettledAtUtc, TimeSpan.FromMinutes(1));

        Assert.DoesNotContain(firings, f => f.JobId == jobB);
        Assert.DoesNotContain(firings, f => f.RunId == loose.Id);
    }

    /// <summary>The failed run's free-text reason rides on the outcome, so the routine list can say WHY a firing
    /// failed instead of only that it did; a completed firing carries none.</summary>
    [Fact]
    public async Task Firings_CarryTheFailureReason_ForFailedRunsOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = Guid.NewGuid();
        const string reason = "Request to upstream AI provider timed out.";

        var failedChat = await MakeChatAsync();
        var failed = await _service.CreateAsync(new AgentRunCreateRequest(
            failedChat, RunShape.SingleTurn, AgentRunTrigger.Schedule, job, null, "failing"), ct);
        await _service.FailAsync(failed.Id, reason, ct: ct);
        SetCompletedAt(failed.Id, DateTime.UtcNow.AddHours(-2));

        var okChat = await MakeChatAsync();
        var ok = await _service.CreateAsync(new AgentRunCreateRequest(
            okChat, RunShape.SingleTurn, AgentRunTrigger.Schedule, job, null, "fine"), ct);
        await _service.CompleteAsync(ok.Id, ct: ct);
        SetCompletedAt(ok.Id, DateTime.UtcNow.AddHours(-1));

        var list = await _service.GetFiringsForTriggerAsync(job, 10, ct);
        Assert.Equal(reason, Assert.Single(list, f => f.RunId == failed.Id).FailureReason);
        Assert.Null(Assert.Single(list, f => f.RunId == ok.Id).FailureReason);

        // The latest-per-trigger aggregate reads the same column off the MAX row.
        SetCompletedAt(failed.Id, DateTime.UtcNow);
        var latest = Assert.Single(await _service.GetLatestSettledFiringsAsync(ct), f => f.JobId == job);
        Assert.Equal(failed.Id, latest.RunId);
        Assert.Equal(reason, latest.FailureReason);
    }

    // The index DDL lives inside EnsureSchema, which runs on EVERY open, so the index arrives at next launch with
    // no MigrateSchema entry; dropping it leaves exactly the pre-upgrade shape.
    [Fact]
    public void TheParentRunIdIndexIsAddedToAPreBatchDatabase()
    {
        using (var drop = _ctx.GetConnection().CreateCommand())
        {
            drop.CommandText = "DROP INDEX IX_AgentRuns_ParentRunId";
            drop.ExecuteNonQuery();
        }

        Assert.False(IndexExists(_ctx.GetConnection(), "IX_AgentRuns_ParentRunId"));

        using var reopened = new SqliteContext(_dbPath);
        Assert.True(IndexExists(reopened.GetConnection(), "IX_AgentRuns_ParentRunId"));
    }

    private static bool IndexExists(SqliteConnection conn, string index)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @Name";
        cmd.Parameters.AddWithValue("@Name", index);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    private async Task<Guid> MakeChatAsync()
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = id,
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = "Assistant",
        }, TestContext.Current.CancellationToken);
        return id;
    }

    // ---- raw ledger/row access: lets a test forge a legacy ledger or simulate a long parked gap
    // without sleeping (the service reads UtcNow, so the fixture moves the persisted timestamps). ----

    private JsonNode LedgerNode(Guid runId)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT LedgerJson FROM AgentRuns WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        var json = Assert.IsType<string>(cmd.ExecuteScalar());
        return JsonNode.Parse(json)!;
    }

    private long WallClockMs(Guid runId) => LedgerNode(runId)["wallClockMs"]!.GetValue<long>();

    // Null for a legacy ledger (field absent) — that is the upgrade trigger.
    private long? ActiveMs(Guid runId) => LedgerNode(runId)["activeMs"]?.GetValue<long>();

    private DateTime? SegmentStartedAt(Guid runId) => LedgerNode(runId)["segmentStartedAt"]?.GetValue<DateTime>();

    private (long Input, long Output) TokenTotals(Guid runId)
    {
        var node = LedgerNode(runId);
        return (node["inputTokens"]!.GetValue<long>(), node["outputTokens"]!.GetValue<long>());
    }

    private void BackdateOpenSegment(Guid runId, TimeSpan by)
    {
        var node = LedgerNode(runId);
        Assert.NotNull(node["segmentStartedAt"]); // nothing to back-date otherwise — the test is wrong
        node["segmentStartedAt"] = JsonValue.Create((DateTime.UtcNow - by).ToString("O"));
        WriteRawLedger(runId, node.ToJsonString());
    }

    private void WriteRawLedger(Guid runId, string ledgerJson)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE AgentRuns SET LedgerJson = @Ledger WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Ledger", ledgerJson);
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        cmd.ExecuteNonQuery();
    }

    private void SetCompletedAt(Guid runId, DateTime completedAtUtc)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE AgentRuns SET CompletedAt = @CompletedAt WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@CompletedAt", completedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        cmd.ExecuteNonQuery();
    }

    private void SetStartedAt(Guid runId, DateTime startedAt)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE AgentRuns SET StartedAt = @StartedAt WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@StartedAt", startedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        cmd.ExecuteNonQuery();
    }

    // A possibly unparseable value, to fault the ledger read that parses it.
    private void SetRawStartedAt(Guid runId, string rawValue)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE AgentRuns SET StartedAt = @StartedAt WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@StartedAt", rawValue);
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        cmd.ExecuteNonQuery();
    }

    // Straight from the row: GetAsync would itself trip over a forged StartedAt.
    private long RawState(Guid runId)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT State FROM AgentRuns WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private long RawCount(string table, string column, Guid id)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = @Id";
        cmd.Parameters.AddWithValue("@Id", id.ToString());
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @Name";
        cmd.Parameters.AddWithValue("@Name", name);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public void Dispose()
    {
        _service.Dispose();
        _chats.Dispose();
        _ctx.Dispose();
        TempPath.Remove(_tmpDir);
    }
}

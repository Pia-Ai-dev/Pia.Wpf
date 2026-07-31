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
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Durable-spine coverage for <see cref="AgentRunService"/> (phase1 plan §12.8): schema
/// idempotency, lifecycle transitions, ledger accrual, the R1 write-order/FK-cascade rules, the
/// eviction predicate, and the R2 re-query semantics of <see cref="AgentRunService.NextPendingStepAsync"/>.
/// Also covers the ledger's ACTIVE-time clock (G1 — parked gaps are not worked time) and the opaque
/// launch-envelope round-trip (<c>PolicyJson</c>).
/// Written to run on Windows/CI — the WPF-targeted test assembly cannot execute on macOS.
/// </summary>
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

        // Reopening the same file re-runs EnsureSchema over existing tables (CREATE TABLE IF NOT
        // EXISTS) — a no-op that must not throw.
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
        // Exercises the non-null-stepId branch of AddUsageAsync (AgentRunService.cs ~170-180),
        // which AddUsageAsync_AccruesRunLevelLedger (stepId: null) never hits.
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "A", Status = AgentStepStatus.Pending };
        await _service.ReplaceStepsAsync(run.Id, new[] { step }, TestContext.Current.CancellationToken);

        await _service.AddUsageAsync(run.Id, step.Id, new UsageDetails { InputTokenCount = 10, OutputTokenCount = 4 }, TestContext.Current.CancellationToken);
        await _service.AddUsageAsync(run.Id, step.Id, new UsageDetails { InputTokenCount = 5, OutputTokenCount = 1 }, TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(fetched!.LedgerJson!);

        // Grand total accrues across both calls.
        Assert.Equal(15, doc.RootElement.GetProperty("inputTokens").GetInt64());
        Assert.Equal(5, doc.RootElement.GetProperty("outputTokens").GetInt64());

        // The per-step entry for that StepId also accrues across both calls.
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
        // G-4: a crash / forced-exit leaves runs crash-recoverable (Planning/Running/Verifying); the startup
        // sweep settles exactly those to Cancelled and never touches already-terminal runs. WaitingForInput/
        // Paused are a DELIBERATE budget-parked state — they survive the sweep resumable (guardrail 3).
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
        // Parked runs survive the sweep resumable (guardrail 3).
        Assert.Equal(AgentRunState.WaitingForInput, (await _service.GetAsync(waiting.Id, ct))!.State);
        Assert.Equal(AgentRunState.Paused, (await _service.GetAsync(paused.Id, ct))!.State);
        Assert.Equal(AgentRunState.Completed, (await _service.GetAsync(completed.Id, ct))!.State);
        Assert.Equal(AgentRunState.Failed, (await _service.GetAsync(failed.Id, ct))!.State);

        // Idempotent: a second sweep settles nothing (all runs are now terminal).
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
        Assert.Null(fetched.CompletedAt); // pause is NOT terminal (guardrail 2)
        Assert.Contains("paused", fetched.ExtraJson ?? string.Empty);
        Assert.Contains("step-cap", fetched.ExtraJson ?? string.Empty);
    }

    [Fact]
    public async Task TryBeginResume_OnlyOneRacerWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.PauseAsync(run.Id, "step-cap", ct);

        // Two racers CAS-claim the same parked run; exactly one wins (guardrail 2 — never two loops).
        var a = _service.TryBeginResumeAsync(run.Id, ct);
        var b = _service.TryBeginResumeAsync(run.Id, ct);
        var results = await Task.WhenAll(a, b);

        Assert.Single(results, r => r); // exactly one true
        Assert.Equal(AgentRunState.Running, (await _service.GetAsync(run.Id, ct))!.State);
    }

    [Fact]
    public async Task TryBeginResume_NonWaitingRun_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.SetStateAsync(run.Id, AgentRunState.Running, ct);

        Assert.False(await _service.TryBeginResumeAsync(run.Id, ct)); // not parked → no-op
        Assert.Equal(AgentRunState.Running, (await _service.GetAsync(run.Id, ct))!.State);
    }

    [Fact]
    public async Task CreateAsync_BeforeChatRow_ThrowsFkConstraint()
    {
        // R1: FK enforcement is ON — a run row cannot precede its AssistantChats parent.
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
        // R2: the loop must pick up steps written by a later ReplaceStepsAsync (replan), not iterate
        // a stale snapshot.
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

        // Replan: replace the remaining plan with an entirely new step set.
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

    // ---- G1: the ledger clock measures ACTIVE time, never the parked gap ----

    [Fact]
    public async Task Ledger_WallClock_ExcludesParkedGap_AndIsMonotonicAcrossTwoPauseResumeCycles()
    {
        // G1: WallClockMs is accumulated WORKED time. The old formula (UtcNow - StartedAt) billed the
        // whole parked span, so a run parked overnight reported ~12h it never worked. StartedAt is
        // deliberately back-dated below — that is exactly the input that used to poison the ledger.
        var ct = TestContext.Current.CancellationToken;
        var run = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);

        // Cycle 1: 4s of work, then park.
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

        // Cycle 2: 6 more seconds of work, then park again — the accumulator only ever grows.
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
        // G1 changed TWO accrual sites. The AddUsageAsync one is covered above; this is the HOT one —
        // every completed step of every run goes through RecordStepResultAsync, so a regression that
        // restored `WallClockMs = ElapsedMs(startedAt)` there would re-import the parked gap with every
        // other G1 test still green.
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
        // Guardrail 1 for the ledger clock itself: MoveLedgerClock runs BEFORE the pause/terminal state
        // UPDATE, so an unguarded fault there would leave a run dangling Running — unresumable, its parked
        // work lost until the startup sweep cancels it. Forced here with an unparseable StartedAt, which
        // makes the ledger read throw (DateTime.Parse) inside MoveLedgerClock.
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
        // Crash path: the startup sweep settles a Running run to Cancelled in bulk and deliberately
        // does not touch ledgers, so the run keeps an OPEN segment forever. Any later ledger write must
        // drop that stale segment — a terminal run cannot have been working through the downtime.
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
        // Backward compatibility: a ledger persisted before active-time tracking has neither activeMs
        // nor segmentStartedAt. A non-terminal legacy run seeds the accumulator ONCE from its last
        // reported total and then behaves like any other run — the parked gap stays out.
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
        // Fallback branch of the legacy upgrade: a legacy ledger that never accrued (wallClockMs 0)
        // has only StartedAt to go on, so the run's whole life so far counts as active.
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

    // ---- D1: the launch grant envelope round-trips as an opaque string ----

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

    /// <summary>
    /// T-ST-9, REGRESSION. The column and its round-trip predate the producer: the INSERT parameter and MapRun
    /// were always correct, and only <c>CreateAsync</c>'s object initializer failed to copy the request member.
    /// BOTH halves matter and this asserts both, because the IN-MEMORY run is the object a fresh launch hands to
    /// <c>AgentRunOrchestrator.RunAsync</c> — the row is never re-read first, so a guard asking "am I a child?"
    /// reads THIS object, not the database. Neutralizing the initializer line reds both asserts at once (the
    /// INSERT sources <c>run.ParentRunId</c>, not <c>request.ParentRunId</c>), which is exactly why the omission
    /// would otherwise be invisible.
    /// </summary>
    [Fact]
    public async Task CreateAsync_RoundTripsParentRunId()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatId = await MakeChatAsync();

        var parent = await _service.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "parent goal"), ct);
        var child = await _service.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "child goal", ParentRunId: parent.Id), ct);

        // The in-memory object CreateAsync returns — the half no pre-existing test covered.
        Assert.Equal(parent.Id, child.ParentRunId);
        // …and the persisted row.
        Assert.Equal(parent.Id, (await _service.GetAsync(child.Id, ct))!.ParentRunId);

        // A top-level run stays null in both places: absence is the default and must not become Guid.Empty.
        Assert.Null(parent.ParentRunId);
        Assert.Null((await _service.GetAsync(parent.Id, ct))!.ParentRunId);
    }

    /// <summary>
    /// T-ST-10, GUARD. The link is queried ("which children is this parent still waiting on"), so it needs an
    /// index. Non-vacuity: the four pre-existing AgentRuns indexes are asserted alongside it, so a typo'd or
    /// deleted CREATE INDEX cannot pass by making the lookup match nothing.
    /// </summary>
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

    /// <summary>
    /// The UPGRADE direction for T-ST-10: an existing database has no <c>IX_AgentRuns_ParentRunId</c>, and the
    /// DDL block lives inside <c>EnsureSchema</c>'s command string, which runs on EVERY open — so the index
    /// arrives at next launch with no MigrateSchema entry. Simulated by dropping it, which leaves exactly the
    /// pre-batch shape.
    /// </summary>
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

    /// <summary>The accumulator; null for a legacy ledger (field absent) — that is the upgrade trigger.</summary>
    private long? ActiveMs(Guid runId) => LedgerNode(runId)["activeMs"]?.GetValue<long>();

    private DateTime? SegmentStartedAt(Guid runId) => LedgerNode(runId)["segmentStartedAt"]?.GetValue<DateTime>();

    private (long Input, long Output) TokenTotals(Guid runId)
    {
        var node = LedgerNode(runId);
        return (node["inputTokens"]!.GetValue<long>(), node["outputTokens"]!.GetValue<long>());
    }

    /// <summary>Pretends the currently OPEN work segment started <paramref name="by"/> ago.</summary>
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

    private void SetStartedAt(Guid runId, DateTime startedAt)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE AgentRuns SET StartedAt = @StartedAt WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@StartedAt", startedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Writes a raw (possibly unparseable) StartedAt, to fault the ledger read that parses it.</summary>
    private void SetRawStartedAt(Guid runId, string rawValue)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE AgentRuns SET StartedAt = @StartedAt WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@StartedAt", rawValue);
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        cmd.ExecuteNonQuery();
    }

    /// <summary>State straight from the row — GetAsync would itself trip over a forged StartedAt.</summary>
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
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }
}

using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

public sealed class AgentRunServiceUserPauseTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SqliteContext _ctx;
    private readonly AssistantChatService _chats;
    private readonly AgentRunService _service;

    public AgentRunServiceUserPauseTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaUserPause_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _service = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _service);
    }

    [Fact]
    public async Task TryPauseUser_FromRunning_WritesPausedWithTheUserReason_AndNoCompletedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        await _service.SetStateAsync(run.Id, AgentRunState.Running, ct);

        Assert.True(await _service.TryPauseUserAsync(run.Id, ct));

        var paused = await _service.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, paused!.State);
        Assert.Null(paused.CompletedAt);
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(paused));

        // The reader requires paused==true, so a writer that emitted only a reason reads back as null.
        var extra = JsonNode.Parse(paused.ExtraJson!)!;
        Assert.True(extra["paused"]!.GetValue<bool>());
        Assert.Equal("user", extra["reason"]!.GetValue<string>());
    }

    /// <summary>Verifying is pausable (the critic's provider call interrupts like a step's), and a pause can
    /// land on a fan-out parent before its un-park CAS.</summary>
    [Theory]
    [InlineData(AgentRunState.Verifying)]
    [InlineData(AgentRunState.WaitingForChildren)]
    public async Task TryPauseUser_FromVerifying_AndFromWaitingForChildren_AlsoWin(AgentRunState from)
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        await _service.SetStateAsync(run.Id, from, ct);

        Assert.True(await _service.TryPauseUserAsync(run.Id, ct));

        var paused = await _service.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, paused!.State);
        Assert.Null(paused.CompletedAt);
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(paused));
    }

    /// <summary>Planning is refused because a resume skips planning: the run would come back with no plan and
    /// settle Completed having done nothing.</summary>
    [Theory]
    [InlineData(AgentRunState.Planning)]
    [InlineData(AgentRunState.WaitingForInput)]
    [InlineData(AgentRunState.Paused)]
    [InlineData(AgentRunState.Completed)]
    [InlineData(AgentRunState.Failed)]
    [InlineData(AgentRunState.Cancelled)]
    public async Task TryPauseUser_FromEveryOtherState_LosesAndWritesNothing(AgentRunState from)
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        await _service.SetStateAsync(run.Id, from, ct);
        var before = RowSnapshot(run.Id);

        Assert.False(await _service.TryPauseUserAsync(run.Id, ct));

        Assert.Equal(before, RowSnapshot(run.Id));
        Assert.Equal(from, (await _service.GetAsync(run.Id, ct))!.State);
    }

    [Fact]
    public async Task TryPauseUser_ClosesTheLedgerWorkSegment()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        await _service.SetStateAsync(run.Id, AgentRunState.Running, ct);
        // Back-dated first: a fresh run's segment is microseconds old, so a deleted close would read the same.
        BackdateOpenSegment(run.Id, TimeSpan.FromSeconds(3));

        Assert.True(await _service.TryPauseUserAsync(run.Id, ct));

        Assert.Null(SegmentStartedAt(run.Id));
        var frozen = WallClockMs(run.Id);
        Assert.InRange(frozen, 3_000, 60_000);      // the 3 s worked before the pause
        Assert.Equal(frozen, ActiveMs(run.Id));
    }

    /// <summary>An asymmetric open/close pairing only corrupts the total from the second close onwards, and
    /// the three back-dated lengths differ so a double-banked segment cannot read like a correct one.</summary>
    [Fact]
    public async Task TheLedgerClock_AccumulatesAcrossADoublePauseResumePauseCycle()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        await _service.SetStateAsync(run.Id, AgentRunState.Running, ct);

        // ---- cycle 1: 3 s of work, closed by the budget park ----
        BackdateOpenSegment(run.Id, TimeSpan.FromSeconds(3));
        await _service.PauseAsync(run.Id, "step-cap", ct);
        Assert.Null(SegmentStartedAt(run.Id));
        var banked1 = ActiveMs(run.Id)!.Value;
        Assert.InRange(banked1, 3_000, 5_000);
        Assert.Equal(banked1, WallClockMs(run.Id));

        Assert.True(await _service.TryBeginResumeAsync(run.Id, ct));
        Assert.NotNull(SegmentStartedAt(run.Id));
        Assert.Equal(banked1, ActiveMs(run.Id)!.Value);

        // ---- cycle 2: 5 s of work, closed by the user pause ----
        BackdateOpenSegment(run.Id, TimeSpan.FromSeconds(5));
        Assert.True(await _service.TryPauseUserAsync(run.Id, ct));
        Assert.Null(SegmentStartedAt(run.Id));
        var banked2 = ActiveMs(run.Id)!.Value;
        // A close that re-banked cycle 1's segment would read 8 s, so the upper bound sits below 3 s + 5 s.
        Assert.InRange(banked2 - banked1, 5_000, 7_000);
        Assert.Equal(banked2, WallClockMs(run.Id));

        Assert.True(await _service.TryResumeFromPauseAsync(run.Id, ct));
        Assert.NotNull(SegmentStartedAt(run.Id));
        Assert.Equal(banked2, ActiveMs(run.Id)!.Value);

        // ---- cycle 3: 7 s of work, the user pause again ----
        BackdateOpenSegment(run.Id, TimeSpan.FromSeconds(7));
        Assert.True(await _service.TryPauseUserAsync(run.Id, ct));
        Assert.Null(SegmentStartedAt(run.Id));
        var total = ActiveMs(run.Id)!.Value;
        Assert.InRange(total - banked2, 7_000, 9_000);
        Assert.InRange(total, 15_000, 18_000);          // 3 + 5 + 7 worked, and NOT a re-banked 20 s+
        Assert.Equal(total, WallClockMs(run.Id));

        Assert.True(await _service.TryResumeFromPauseAsync(run.Id, ct));
    }

    /// <summary>The claim retires the marker it consumed: a completing run never rewrites the column, so a
    /// retained marker would offer Continue on a finished run forever.</summary>
    [Fact]
    public async Task TryResumeFromPause_ClaimsOnce_AndNullsTheEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        await _service.SetStateAsync(run.Id, AgentRunState.Running, ct);
        Assert.True(await _service.TryPauseUserAsync(run.Id, ct));
        Assert.NotNull((await _service.GetAsync(run.Id, ct))!.ExtraJson);
        Assert.Null(SegmentStartedAt(run.Id));

        Assert.True(await _service.TryResumeFromPauseAsync(run.Id, ct));

        var resumed = await _service.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Running, resumed!.State);
        Assert.Null(resumed.ExtraJson);
        Assert.NotNull(SegmentStartedAt(run.Id));

        var afterWin = RowSnapshot(run.Id);
        Assert.False(await _service.TryResumeFromPauseAsync(run.Id, ct));
        Assert.Equal(afterWin, RowSnapshot(run.Id));
    }

    /// <summary>A claim that lost must not null the marker on its way out, or a parked run loses the reason
    /// its Flow card is keyed on.</summary>
    [Fact]
    public async Task TryResumeFromPause_DoesNotClaimAWaitingForInputRun_AndTryBeginResumeDoesNotClaimAPausedOne()
    {
        var ct = TestContext.Current.CancellationToken;

        // A budget-parked run belongs to TryBeginResumeAsync alone.
        var atBudget = await NewRunAsync(ct);
        await _service.PauseAsync(atBudget.Id, "step-cap", ct);
        Assert.False(await _service.TryResumeFromPauseAsync(atBudget.Id, ct));
        var stillParked = await _service.GetAsync(atBudget.Id, ct);
        Assert.Equal(AgentRunState.WaitingForInput, stillParked!.State);
        Assert.Equal("step-cap", RunPauseEnvelope.ReadReason(stillParked));

        // A user-paused run belongs to TryResumeFromPauseAsync alone.
        var userPaused = await NewRunAsync(ct);
        await _service.SetStateAsync(userPaused.Id, AgentRunState.Running, ct);
        Assert.True(await _service.TryPauseUserAsync(userPaused.Id, ct));
        Assert.False(await _service.TryBeginResumeAsync(userPaused.Id, ct));
        var stillPaused = await _service.GetAsync(userPaused.Id, ct);
        Assert.Equal(AgentRunState.Paused, stillPaused!.State);
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(stillPaused));

        // Non-vacuity: each run IS claimable by its own method.
        Assert.True(await _service.TryBeginResumeAsync(atBudget.Id, ct));
        Assert.True(await _service.TryResumeFromPauseAsync(userPaused.Id, ct));
    }

    [Fact]
    public async Task TryRejectParkedPlanAsync_CancelsAPlanApprovalPark()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        await _service.PauseAsync(run.Id, AgentRunOrchestrator.PlanApprovalReason, ct);

        Assert.True(await _service.TryRejectParkedPlanAsync(run.Id, ct));

        var updated = await _service.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Cancelled, updated!.State);
        Assert.NotNull(updated.CompletedAt);
        Assert.Null(updated.ExtraJson);
    }

    /// <summary>The reason gate is the whole point: state alone cannot tell this plan's park from a run that
    /// re-parked on a different question since the Reject button was drawn.</summary>
    [Fact]
    public async Task TryRejectParkedPlanAsync_DoesNotCancel_WhenParkedForADifferentReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        await _service.PauseAsync(run.Id, AgentRunOrchestrator.NeedsInputReason, ct);
        var before = RowSnapshot(run.Id);

        Assert.False(await _service.TryRejectParkedPlanAsync(run.Id, ct));

        var updated = await _service.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.WaitingForInput, updated!.State);
        Assert.Equal(before, RowSnapshot(run.Id));
    }

    [Fact]
    public async Task TryRejectParkedPlanAsync_RaisesRunChangedCancelled_OnlyOnTheWin()
    {
        var ct = TestContext.Current.CancellationToken;
        var winner = await NewRunAsync(ct);
        await _service.PauseAsync(winner.Id, AgentRunOrchestrator.PlanApprovalReason, ct);
        var loser = await NewRunAsync(ct);
        await _service.PauseAsync(loser.Id, AgentRunOrchestrator.NeedsInputReason, ct);

        var seen = new List<(Guid RunId, AgentRunState State)>();
        void Handler(object? s, AgentRunChangedEventArgs e) => seen.Add((e.RunId, e.State));
        _service.RunChanged += Handler;
        try
        {
            Assert.True(await _service.TryRejectParkedPlanAsync(winner.Id, ct));
            Assert.Equal([(winner.Id, AgentRunState.Cancelled)], seen);

            Assert.False(await _service.TryRejectParkedPlanAsync(loser.Id, ct));
            Assert.Equal([(winner.Id, AgentRunState.Cancelled)], seen);
        }
        finally
        {
            _service.RunChanged -= Handler;
        }
    }

    /// <summary>The Flow surface publishes run cards straight off this event, so an event for a run nobody
    /// paused would post a "continue?" card the claim then refuses.</summary>
    [Fact]
    public async Task TryPauseUser_RaisesRunChangedPausedOnTheWinOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var winner = await NewRunAsync(ct);
        await _service.SetStateAsync(winner.Id, AgentRunState.Running, ct);
        var loser = await NewRunAsync(ct);
        await _service.SetStateAsync(loser.Id, AgentRunState.Completed, ct);

        var seen = new List<(Guid RunId, AgentRunState State)>();
        void Handler(object? s, AgentRunChangedEventArgs e) => seen.Add((e.RunId, e.State));
        _service.RunChanged += Handler;
        try
        {
            Assert.True(await _service.TryPauseUserAsync(winner.Id, ct));
            Assert.Equal([(winner.Id, AgentRunState.Paused)], seen);

            Assert.False(await _service.TryPauseUserAsync(loser.Id, ct));
            Assert.Equal([(winner.Id, AgentRunState.Paused)], seen);
        }
        finally
        {
            _service.RunChanged -= Handler;
        }
    }

    /// <summary><c>CompletedAt</c> is what catches a moved sweep threshold: the sweep stamps one, so a swept
    /// row stays distinguishable even after somebody "fixes" the state back.</summary>
    [Fact]
    public async Task TheSweepStillLeavesAUserPausedRunAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        await _service.SetStateAsync(run.Id, AgentRunState.Running, ct);
        Assert.True(await _service.TryPauseUserAsync(run.Id, ct));

        await _service.FailInterruptedRunsAsync(ct);

        var survivor = await _service.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, survivor!.State);
        Assert.Null(survivor.CompletedAt);
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(survivor));

        Assert.True(await _service.TryResumeFromPauseAsync(run.Id, ct));
    }

    public void Dispose()
    {
        _service.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
    }

    private async Task<AgentRun> NewRunAsync(CancellationToken ct)
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
        }, ct);

        return await _service.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "goal"), ct);
    }

    /// <summary>Every column as one string, so a "wrote nothing" claim covers <c>LedgerJson</c> too — an
    /// ungated ledger move rewrites it while every other column stays identical.</summary>
    private string RowSnapshot(Guid runId)
    {
        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT * FROM AgentRuns WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        var sb = new StringBuilder();
        for (var i = 0; i < reader.FieldCount; i++)
            sb.Append(reader.GetName(i)).Append('=').Append(reader.GetValue(i)).Append('|');
        return sb.ToString();
    }

    // ---- raw ledger access: the service reads UtcNow, so a test moves the timestamp instead of sleeping ----

    private JsonNode LedgerNode(Guid runId)
    {
        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT LedgerJson FROM AgentRuns WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        return JsonNode.Parse(Assert.IsType<string>(cmd.ExecuteScalar()))!;
    }

    private long WallClockMs(Guid runId) => LedgerNode(runId)["wallClockMs"]!.GetValue<long>();

    private long? ActiveMs(Guid runId) => LedgerNode(runId)["activeMs"]?.GetValue<long>();

    private DateTime? SegmentStartedAt(Guid runId) => LedgerNode(runId)["segmentStartedAt"]?.GetValue<DateTime>();

    /// <summary>Pretends the currently OPEN work segment started <paramref name="by"/> ago.</summary>
    private void BackdateOpenSegment(Guid runId, TimeSpan by)
    {
        var node = LedgerNode(runId);
        Assert.NotNull(node["segmentStartedAt"]);
        node["segmentStartedAt"] = JsonValue.Create((DateTime.UtcNow - by).ToString("O"));

        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "UPDATE AgentRuns SET LedgerJson = @Ledger WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Ledger", node.ToJsonString());
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        cmd.ExecuteNonQuery();
    }
}

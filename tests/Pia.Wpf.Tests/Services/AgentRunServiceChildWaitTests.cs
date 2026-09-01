using System.IO;
using System.Text.Json.Nodes;
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

/// <summary>
/// <see cref="AgentRunState.WaitingForChildren"/> is appended at 8, ABOVE the terminal band, so the startup
/// sweep's <c>State &lt; 3</c> leaves a waiting parent alone — the price is that every ordinal RANGE now lies.
/// </summary>
public sealed class AgentRunServiceChildWaitTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SqliteContext _ctx;
    private readonly AssistantChatService _chats;
    private readonly AgentRunService _service;

    public AgentRunServiceChildWaitTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaChildWait_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _service = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _service);
    }

    // Persisted to AgentRuns.State as an int, so a renumber silently reinterprets every historical row. The
    // member-count assert is the non-vacuity half: an INSERTED member leaves every named row below still correct.
    [Fact]
    public void AgentRunState_OrdinalsArePinned()
    {
        Assert.Equal(0, (int)AgentRunState.Planning);
        Assert.Equal(1, (int)AgentRunState.Running);
        Assert.Equal(2, (int)AgentRunState.Verifying);
        Assert.Equal(3, (int)AgentRunState.WaitingForInput);
        Assert.Equal(4, (int)AgentRunState.Paused);
        Assert.Equal(5, (int)AgentRunState.Completed);
        Assert.Equal(6, (int)AgentRunState.Failed);
        Assert.Equal(7, (int)AgentRunState.Cancelled);
        Assert.Equal(8, (int)AgentRunState.WaitingForChildren);

        var all = Enum.GetValues<AgentRunState>();
        Assert.Equal(9, all.Length);                                   // a 10th member must come here first
        Assert.Equal(all.Length, all.Select(s => (int)s).Distinct().Count());
    }

    // A parked parent is not working while its children are, so the ledger work segment closes. The open segment
    // is back-dated first, or "the clock froze" and "the clock kept running" would be the same number.
    [Fact]
    public async Task BeginChildWait_ParksTheParent_AndClosesItsLedgerSegment()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await NewRunAsync(ct);
        BackdateOpenSegment(parent.Id, TimeSpan.FromSeconds(3));

        await _service.BeginChildWaitAsync(parent.Id, 2, ct);

        var parked = await _service.GetAsync(parent.Id, ct);
        Assert.Equal(AgentRunState.WaitingForChildren, parked!.State);
        Assert.Null(parked.CompletedAt);        // a park is not a completion
        Assert.Null(parked.ExtraJson);          // the child ROWS are the marker — no counter is written

        Assert.Null(SegmentStartedAt(parent.Id));
        var frozen = WallClockMs(parent.Id);
        Assert.InRange(frozen, 3_000, 60_000);  // the 3 s worked before the park was banked
        Assert.Equal(frozen, ActiveMs(parent.Id));
    }

    // The startup reconcile re-parks the parent as WaitingForInput carrying the same pause envelope PauseAsync
    // writes, so the existing Continue button and resume CAS bring it back with no new vocabulary.
    [Fact]
    public async Task AWaitingParentSurvivesTheStartupSweep_AsWaitingForInput()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await NewRunAsync(ct);
        var childA = await NewRunAsync(ct, parentRunId: parent.Id);
        var childB = await NewRunAsync(ct, parentRunId: parent.Id);
        await _service.SetStateAsync(childA.Id, AgentRunState.Running, ct);
        await _service.SetStateAsync(childB.Id, AgentRunState.Running, ct);
        await _service.BeginChildWaitAsync(parent.Id, 2, ct);

        // 2 children Cancelled + 1 parent re-parked; the return value is the SUM of both statements.
        Assert.Equal(3, await _service.FailInterruptedRunsAsync(ct));

        Assert.Equal(AgentRunState.Cancelled, (await _service.GetAsync(childA.Id, ct))!.State);
        Assert.Equal(AgentRunState.Cancelled, (await _service.GetAsync(childB.Id, ct))!.State);

        var reparked = await _service.GetAsync(parent.Id, ct);
        Assert.Equal(AgentRunState.WaitingForInput, reparked!.State);
        Assert.Null(reparked.CompletedAt);      // re-parked, NOT finished — statement 1's CompletedAt is not copied

        var extra = JsonNode.Parse(reparked.ExtraJson!)!;
        Assert.True(extra["paused"]!.GetValue<bool>());
        Assert.Equal("children-interrupted", extra["reason"]!.GetValue<string>());

        // The behavioural half: the re-park lands in the one state the resume CAS can claim.
        Assert.True(await _service.TryBeginResumeAsync(parent.Id, ct));
        Assert.Equal(AgentRunState.Running, (await _service.GetAsync(parent.Id, ct))!.State);
    }

    // NextPendingStepAsync selects on Status=Pending, so a dispatched sibling left Running is invisible to the
    // resume drain and a re-parked parent would skip its whole delegated group on Continue.
    [Fact]
    public async Task AnInterruptedFanOutsDelegatedStepsGoBackToPending_SoTheResumeCanSeeThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await NewRunAsync(ct);
        // Ids are minted HERE, not by ReplaceStepsAsync: it generates one for a Guid.Empty step and does not
        // write it back, so an unset id would leave every SetStepStatusAsync below a silent no-op.
        var done = new AgentStep { Id = Guid.NewGuid(), RunId = parent.Id, Ordinal = 0, Title = "done", Intent = "i0" };
        var groupA = new AgentStep { Id = Guid.NewGuid(), RunId = parent.Id, Ordinal = 1, Title = "A", Intent = "i1" };
        var groupB = new AgentStep { Id = Guid.NewGuid(), RunId = parent.Id, Ordinal = 2, Title = "B", Intent = "i2" };
        var after = new AgentStep { Id = Guid.NewGuid(), RunId = parent.Id, Ordinal = 3, Title = "after", Intent = "i3" };
        await _service.ReplaceStepsAsync(parent.Id, [done, groupA, groupB, after], ct);
        await _service.SetStepStatusAsync(done.Id, AgentStepStatus.Done, ct);
        // Exactly what the fan-out leaves behind when the machine dies mid-dispatch.
        await _service.SetStepStatusAsync(groupA.Id, AgentStepStatus.Running, ct);
        await _service.SetStepStatusAsync(groupB.Id, AgentStepStatus.Running, ct);
        await _service.BeginChildWaitAsync(parent.Id, 2, ct);

        await _service.FailInterruptedRunsAsync(ct);

        var plan = (await _service.GetAsync(parent.Id, ct))!.Plan.OrderBy(s => s.Ordinal).ToList();
        Assert.Equal(AgentStepStatus.Done, plan[0].Status);      // non-vacuity: a Done step is NOT rewound
        Assert.Equal(AgentStepStatus.Pending, plan[1].Status);
        Assert.Equal(AgentStepStatus.Pending, plan[2].Status);
        Assert.Equal(AgentStepStatus.Pending, plan[3].Status);

        // The behavioural half: the resume drain now reaches the delegated group FIRST, in ordinal order,
        // instead of skipping straight past it to the step that depends on its output.
        await _service.TryBeginResumeAsync(parent.Id, ct);
        var next = await _service.NextPendingStepAsync(parent.Id, ct);
        Assert.Equal(groupA.Id, next!.Id);
    }

    // An INDEPENDENT hand-written table, never derived from the predicate under test. StampsCompletedAt is here
    // because state alone cannot tell a swept Cancelled row from an untouched one.
    private static readonly IReadOnlyDictionary<AgentRunState, (AgentRunState After, bool StampsCompletedAt)>
        ExpectedSweepVerdict = new Dictionary<AgentRunState, (AgentRunState, bool)>
        {
            [AgentRunState.Planning] = (AgentRunState.Cancelled, true),
            [AgentRunState.Running] = (AgentRunState.Cancelled, true),
            [AgentRunState.Verifying] = (AgentRunState.Cancelled, true),
            [AgentRunState.WaitingForInput] = (AgentRunState.WaitingForInput, false),
            [AgentRunState.Paused] = (AgentRunState.Paused, false),
            [AgentRunState.Completed] = (AgentRunState.Completed, false),
            [AgentRunState.Failed] = (AgentRunState.Failed, false),
            [AgentRunState.Cancelled] = (AgentRunState.Cancelled, false),
            [AgentRunState.WaitingForChildren] = (AgentRunState.WaitingForInput, false),
        };

    /// <summary>The theory's cases come from the ENUM, not from a hand-listed set of rows: an appended member
    /// gets its own case the moment it exists, and hits the table lookup below with no verdict declared.</summary>
    public static TheoryData<AgentRunState> EverySweepState()
    {
        var data = new TheoryData<AgentRunState>();
        foreach (var state in Enum.GetValues<AgentRunState>())
            data.Add(state);
        return data;
    }

    // The rows are enumerated FROM the enum: the sweep's first statement is an ordinal RANGE, so a member appended
    // above WaitingForInput falls outside it silently and an [InlineData] table would simply have no row for it.
    [Theory]
    [MemberData(nameof(EverySweepState))]
    public async Task TheSweepStillCancelsOnlyStatesBelowWaitingForInput(AgentRunState before)
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.True(
            ExpectedSweepVerdict.TryGetValue(before, out var verdict),
            $"AgentRunState.{before} has no declared startup-sweep verdict. A new member must state whether the "
            + "sweep cancels it, leaves it parked/terminal, or reconciles it — statement 1 is an ordinal RANGE "
            + "and will silently ignore any member appended above WaitingForInput.");
        Assert.Equal(Enum.GetValues<AgentRunState>().Length, ExpectedSweepVerdict.Count);

        var run = await NewRunAsync(ct);
        await _service.SetStateAsync(run.Id, before, ct);
        Assert.Null((await _service.GetAsync(run.Id, ct))!.CompletedAt);     // the stamp below is the sweep's

        await _service.FailInterruptedRunsAsync(ct);

        var swept = (await _service.GetAsync(run.Id, ct))!;
        Assert.Equal(verdict.After, swept.State);
        Assert.Equal(verdict.StampsCompletedAt, swept.CompletedAt is not null);
    }

    // Two writers can want a waiting parent — its own loop and the cascade-cancel path — and a blind write would
    // flip a Cancelled parent back to Running: live in the panel and owned by nobody.
    [Fact]
    public async Task TryEndChildWait_IsACAS()
    {
        var ct = TestContext.Current.CancellationToken;

        var winner = await NewRunAsync(ct);
        await _service.BeginChildWaitAsync(winner.Id, 1, ct);
        Assert.Null(SegmentStartedAt(winner.Id));                 // parked ⇒ no open segment

        Assert.True(await _service.TryEndChildWaitAsync(winner.Id, ct));
        Assert.Equal(AgentRunState.Running, (await _service.GetAsync(winner.Id, ct))!.State);
        Assert.NotNull(SegmentStartedAt(winner.Id));              // a FRESH work segment on the win

        // The loser: a parent something else already settled must not come back.
        var settled = await NewRunAsync(ct);
        await _service.BeginChildWaitAsync(settled.Id, 1, ct);
        await _service.FailAsync(settled.Id, null, cancelled: true, ct);

        Assert.False(await _service.TryEndChildWaitAsync(settled.Id, ct));
        Assert.Equal(AgentRunState.Cancelled, (await _service.GetAsync(settled.Id, ct))!.State);
        Assert.Null(SegmentStartedAt(settled.Id));                // the loser never re-opens a clock
    }

    // Unlike the resume claim, this transition has no pause marker to retire — and ExtraJson is the only place a
    // truncation reason or an error lives, so nulling it here would erase that.
    [Fact]
    public async Task TryEndChildWait_DoesNotClearExtraJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);

        // Any writer of ExtraJson will do; PauseAsync is the one that exists, and SetStateAsync moves the
        // state off WaitingForInput without touching the column.
        await _service.PauseAsync(run.Id, "step-cap", ct);
        await _service.SetStateAsync(run.Id, AgentRunState.WaitingForChildren, ct);
        var before = (await _service.GetAsync(run.Id, ct))!.ExtraJson;
        Assert.NotNull(before);

        Assert.True(await _service.TryEndChildWaitAsync(run.Id, ct));

        Assert.Equal(before, (await _service.GetAsync(run.Id, ct))!.ExtraJson);
    }

    // The children's wall clock belongs to the children, so a token roll-up onto a still-parked parent must not
    // re-open its work segment and bill the rest of the wait to it as worked time.
    [Fact]
    public async Task AddUsage_OnAWaitingParent_AccruesTokensWithoutReopeningTheClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await NewRunAsync(ct);
        BackdateOpenSegment(parent.Id, TimeSpan.FromSeconds(3));
        await _service.BeginChildWaitAsync(parent.Id, 2, ct);
        var frozen = WallClockMs(parent.Id);

        await _service.AddUsageAsync(parent.Id, null, new UsageDetails { InputTokenCount = 40, OutputTokenCount = 9 }, ct);
        await _service.AddUsageAsync(parent.Id, null, new UsageDetails { InputTokenCount = 2, OutputTokenCount = 1 }, ct);

        Assert.Equal((42, 10), TokenTotals(parent.Id));
        Assert.Null(SegmentStartedAt(parent.Id));
        Assert.Equal(frozen, WallClockMs(parent.Id));
        Assert.Equal(AgentRunState.WaitingForChildren, (await _service.GetAsync(parent.Id, ct))!.State);
    }

    // ApplyLedgerClock's terminal test used to be state >= Completed, and WaitingForChildren = 8, so a parked
    // parent read as TERMINAL: its open segment dropped and its wallClockMs frozen for the rest of its life.
    [Theory]
    [InlineData(AgentRunState.Planning)]
    [InlineData(AgentRunState.Running)]
    [InlineData(AgentRunState.Verifying)]
    [InlineData(AgentRunState.WaitingForInput)]
    [InlineData(AgentRunState.Paused)]
    [InlineData(AgentRunState.Completed)]
    [InlineData(AgentRunState.Failed)]
    [InlineData(AgentRunState.Cancelled)]
    [InlineData(AgentRunState.WaitingForChildren)]
    public async Task TheLedgerTerminalTest_MatchesTheOldRangeForEveryPreExistingState_AndExcludesWaitingForChildren(
        AgentRunState state)
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        BackdateOpenSegment(run.Id, TimeSpan.FromSeconds(3));
        await _service.SetStateAsync(run.Id, state, ct); // blind: moves the state, never the ledger

        await _service.AddUsageAsync(run.Id, null, new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 }, ct);

        // What the enum's ordinal band says, and what "can never work again" actually says.
        var oldRangeSaidTerminal = state >= AgentRunState.Completed;
        var reallyTerminal = state is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled;
        // They agree for every pre-existing member, and for EXACTLY ONE they disagree.
        Assert.Equal(state != AgentRunState.WaitingForChildren, oldRangeSaidTerminal == reallyTerminal);

        if (reallyTerminal)
        {
            Assert.Null(SegmentStartedAt(run.Id));  // stale segment dropped, downtime never billed
            Assert.Equal(0, WallClockMs(run.Id));
        }
        else
        {
            Assert.NotNull(SegmentStartedAt(run.Id)); // still working (or parked mid-work) — segment survives
            Assert.InRange(WallClockMs(run.Id), 3_000, 60_000);
        }
    }

    // How a parent counts what it is still waiting on, which is why no "waiting on N children" counter is
    // persisted anywhere. Ordered by CreatedAt, scoped to the parent, deliberately WITHOUT the steps.
    [Fact]
    public async Task GetChildRuns_ReturnsOnlyTheChildren_InCreationOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await NewRunAsync(ct);
        var first = await NewRunAsync(ct, parentRunId: parent.Id);
        var second = await NewRunAsync(ct, parentRunId: parent.Id);
        var unrelated = await NewRunAsync(ct);
        await _service.ReplaceStepsAsync(first.Id, [new AgentStep { Ordinal = 0, Title = "t", Intent = "i" }], ct);

        var children = await _service.GetChildRunsAsync(parent.Id, ct);

        Assert.Equal<IEnumerable<Guid>>([first.Id, second.Id], children.Select(c => c.Id).ToList());
        Assert.DoesNotContain(unrelated.Id, children.Select(c => c.Id));
        // No LoadSteps pass: both callers want state + ledger, and a 4-child roll-up must not pay 4 plan
        // queries. The step above exists precisely so an empty Plan here is a measured absence.
        Assert.All(children, c => Assert.Empty(c.Plan));

        Assert.Empty(await _service.GetChildRunsAsync(unrelated.Id, ct));
    }

    public void Dispose()
    {
        _service.Dispose();
        _chats.Dispose();
        _ctx.Dispose();
        TempPath.Remove(_tmpDir);
    }

    private async Task<AgentRun> NewRunAsync(CancellationToken ct, Guid? parentRunId = null)
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
            chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "goal", ParentRunId: parentRunId), ct);
    }

    // ---- raw ledger access, mirroring AgentRunServiceTests' fixture: the service reads UtcNow, so a test
    // simulates elapsed work by moving the persisted timestamp instead of sleeping. ----

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

        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "UPDATE AgentRuns SET LedgerJson = @Ledger WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Ledger", node.ToJsonString());
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        cmd.ExecuteNonQuery();
    }
}

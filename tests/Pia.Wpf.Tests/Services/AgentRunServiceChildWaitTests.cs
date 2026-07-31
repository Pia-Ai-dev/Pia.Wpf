using System.IO;
using System.Text.Json.Nodes;
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
/// Batch 07 G8 — the persisted <see cref="AgentRunState.WaitingForChildren"/> state, end to end against a real
/// SQLite <see cref="AgentRunService"/>.
/// <para>
/// The state exists because D7 gave child runs their own concurrency pool, so a parent AWAITS its children and
/// no pre-existing state can say so: 0–2 are swept to Cancelled at every startup, 3 is the one state the
/// resume CAS claims (parking there invites a second loop onto one run), 4 is reserved for Batch 08, and 5–7
/// are terminal. Appending at 8 — ABOVE the terminal band — is what makes the sweep's <c>State &lt; 3</c>
/// leave a waiting parent alone for free; the price is that every ordinal RANGE over this enum now lies, which
/// is what <see cref="TheLedgerTerminalTest_MatchesTheOldRangeForEveryPreExistingState_AndExcludesWaitingForChildren"/>
/// pins.
/// </para>
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

    /// <summary>
    /// T-ST-1, <b>GUARD</b>. The persisted ordinals, pinned by name. This enum is written to
    /// <c>AgentRuns.State</c> as an <c>int</c>, so a renumber silently reinterprets every historical row — and
    /// until this test there was no pin at all (R9).
    /// <para>
    /// The member-count assert is the non-vacuity half: without it, an INSERTED member (the one mistake that
    /// actually shifts ordinals) would leave every named row below still correct for its own name while
    /// everything after the insertion point moved, and this test would pass.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// T-ST-2, <b>REGRESSION</b>. Parking a parent writes state 8 and CLOSES the ledger work segment: the
    /// parent is not working while its children are, and each child bills its own wall clock into its own
    /// ledger (D15's tokens-only roll-up depends on this).
    /// <para>
    /// The open segment is back-dated 3 s first, on purpose. A freshly created run's segment is only
    /// microseconds old, so without the back-date "the clock froze" and "the clock kept running" are the same
    /// number and the test would pass with <c>MoveLedgerClock(CloseSegment)</c> deleted.
    /// </para>
    /// Neutralize: drop the <c>MoveLedgerClock(CloseSegment)</c> from <c>BeginChildWaitAsync</c> — the segment
    /// stays open and both the null and the frozen-total asserts red.
    /// </summary>
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
        Assert.Null(parked.ExtraJson);          // the child ROWS are the marker (§0.4) — no counter is written

        Assert.Null(SegmentStartedAt(parent.Id));
        var frozen = WallClockMs(parent.Id);
        Assert.InRange(frozen, 3_000, 60_000);  // the 3 s worked before the park was banked
        Assert.Equal(frozen, ActiveMs(parent.Id));
    }

    /// <summary>
    /// T-ST-3, <b>REGRESSION</b> — D14, and the whole point of this group. A process death while a parent is
    /// mid-fan-out used to lose the wait outright. Now the startup reconcile cancels the children (statement 1)
    /// and RE-PARKS the parent as <see cref="AgentRunState.WaitingForInput"/> (statement 2) carrying the same
    /// <c>{paused:true,reason}</c> envelope <c>PauseAsync</c> writes — so the panel's existing Continue button
    /// and the existing resume CAS bring it back with no new resume vocabulary.
    /// <para>
    /// The <c>TryBeginResumeAsync</c> leg is the one that matters: it proves the re-park landed in the ONE
    /// state that CAS accepts. Asserting the JSON alone would pass on a re-park to any state at all.
    /// </para>
    /// Neutralize: delete statement 2 — the parent stays at 8 and the claim returns false.
    /// </summary>
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

    /// <summary>
    /// <b>REGRESSION</b> (Phase 3 fix pass). The crash path's half of the fan-out's Pending invariant, and the
    /// other half of what "the parent survives a restart mid-fan-out" has to mean.
    /// <para>
    /// <c>TryFanOutAsync</c> sets every DISPATCHED sibling step to Running immediately, and the in-process
    /// parked arm puts them back to Pending explicitly — because <c>NextPendingStepAsync</c> selects on
    /// <c>Status=Pending</c> and a step left Running is invisible to the resume drain. No code runs on the
    /// crash path, so statement 1b has to establish the same invariant. Without it a re-parked parent skips its
    /// whole delegated group on Continue, runs the steps AFTER it out of order against inputs nothing produced,
    /// and settles Completed while the panel still renders those steps as active.
    /// </para>
    /// The non-vacuity control is the DONE step: a statement that simply reset every step would also make the
    /// Pending assertions pass. Neutralize: delete statement 1b → <c>NextPendingStepAsync</c> returns the
    /// post-group step and the two delegated ones stay Running.
    /// </summary>
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

    /// <summary>
    /// T-ST-4, <b>GUARD</b>. A row per state, so no threshold change can hide: 0–2 are swept to Cancelled,
    /// 3/4 are a deliberate park and are untouched, 5–7 are terminal and untouched, and 8 is reconciled to
    /// WaitingForInput rather than either swept or ignored. Runs are forced into each state through the blind
    /// <c>SetStateAsync</c> so the row under test is exactly the state named and nothing else.
    /// </summary>
    [Theory]
    [InlineData(AgentRunState.Planning, AgentRunState.Cancelled)]
    [InlineData(AgentRunState.Running, AgentRunState.Cancelled)]
    [InlineData(AgentRunState.Verifying, AgentRunState.Cancelled)]
    [InlineData(AgentRunState.WaitingForInput, AgentRunState.WaitingForInput)]
    [InlineData(AgentRunState.Paused, AgentRunState.Paused)]
    [InlineData(AgentRunState.Completed, AgentRunState.Completed)]
    [InlineData(AgentRunState.Failed, AgentRunState.Failed)]
    [InlineData(AgentRunState.Cancelled, AgentRunState.Cancelled)]
    [InlineData(AgentRunState.WaitingForChildren, AgentRunState.WaitingForInput)]
    public async Task TheSweepStillCancelsOnlyStatesBelowWaitingForInput(AgentRunState before, AgentRunState after)
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        await _service.SetStateAsync(run.Id, before, ct);

        await _service.FailInterruptedRunsAsync(ct);

        Assert.Equal(after, (await _service.GetAsync(run.Id, ct))!.State);
    }

    /// <summary>
    /// T-ST-5, <b>REGRESSION</b>. Leaving the wait is a CAS, not a blind write. Two writers can want a
    /// waiting parent — its own loop, and the cascade-cancel path — and <c>SetStateAsync</c> would happily
    /// flip a Cancelled parent back to Running (R11), producing a run that is live in the panel and owned by
    /// nobody.
    /// <para>
    /// Neutralize: make <c>TryEndChildWaitAsync</c> a blind UPDATE — the second half reds with a resurrected
    /// Cancelled run.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// T-ST-6, <b>GUARD</b>. The CAS does NOT null <c>ExtraJson</c>, unlike <c>TryBeginResumeAsync</c>, which
    /// clears the pause marker it is claiming. This transition is not a user "continue" and has no marker to
    /// retire — and a run's <c>ExtraJson</c> is the only place a truncation reason or an error lives, so
    /// copying the resume claim's <c>ExtraJson=NULL</c> here would erase it.
    /// </summary>
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

    /// <summary>
    /// T-ST-7, <b>REGRESSION</b>. D15's tokens-only rule at the ledger. A parent rolls up each settled child's
    /// tokens through the ordinary run-level <c>AddUsageAsync</c> while it is still parked, and that write must
    /// not re-open the work segment: the children's wall clock belongs to the children, and re-opening here
    /// would bill the rest of the wait to the parent as worked time.
    /// <para>
    /// Neutralize: make the roll-up open a segment (or let <c>ApplyLedgerClock</c> treat 8 as non-parked) —
    /// <c>segmentStartedAt</c> comes back non-null and the frozen total moves.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// T-LED-1, <b>REGRESSION</b> — D8c, the one production range comparison the appended ordinal broke.
    /// <c>ApplyLedgerClock</c>'s <c>terminal</c> test used to be <c>state &gt;= Completed</c>, and
    /// <c>WaitingForChildren = 8 &gt;= 5</c>, so a parked parent would have been read as TERMINAL: its open
    /// segment dropped and its <c>wallClockMs</c> frozen for the rest of its life.
    /// <para>
    /// Driven behaviourally rather than by reflecting the private predicate: a 3 s-old open segment plus a
    /// usage accrual answers "did this state freeze the clock?" for every member. Rows 0–7 assert exactly what
    /// the OLD range said (computed from it, in the assert, so the parity claim is in the code and not only in
    /// the name); row 8 asserts the opposite, and that disagreement is what makes the theory non-vacuous.
    /// </para>
    /// Neutralize: restore <c>state &gt;= AgentRunState.Completed</c> — the WaitingForChildren row reds.
    /// </summary>
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

        // What the enum's ordinal band says, and what "can never work again" actually says. They agree for
        // every member that existed before Batch 07, and disagree for exactly the appended one.
        var oldRangeSaidTerminal = state >= AgentRunState.Completed;
        var reallyTerminal = state is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled;
        // They agree for every member that existed before Batch 07, and for EXACTLY ONE they disagree.
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

    /// <summary>
    /// T-ST-8, <b>REGRESSION</b>. <c>GetChildRunsAsync</c> is how a parent counts what it is still waiting on
    /// and how the panel lists its children — the reason no "waiting on N children" counter is persisted
    /// anywhere (§0.4). Ordered by <c>CreatedAt</c>, scoped to the parent, and deliberately WITHOUT the steps.
    /// <para>Neutralize: drop the <c>WHERE ParentRunId</c> — the unrelated run appears and the count reds.</para>
    /// </summary>
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
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
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

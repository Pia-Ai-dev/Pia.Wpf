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

/// <summary>
/// Batch 08 G2 — <see cref="AgentRunState.Paused"/> becomes a WRITABLE state, against a real SQLite
/// <see cref="AgentRunService"/>. Two new CASes and nothing that calls them yet: this file IS the caller, and
/// that invariance is what makes the commit safe to land before the loop that uses them.
/// <para>
/// The ordinal 4 has existed since Batch 07 and is already excluded from the startup sweep, already read as
/// not-executing and already rendered in three locales — what was missing was a DRIVER. So the facts here are
/// about the two transitions and their edges, not about the enum:
/// <list type="bullet">
/// <item><c>TryPauseUserAsync</c>: an EXPLICIT source set (never an ordinal range — D7, and
/// <see cref="AgentRunState.WaitingForChildren"/> = 8 sits above the terminal band, so every threshold lies),
/// no <c>CompletedAt</c>, the app-owned <c>"user"</c> reason, and a ledger clock that closes only for the
/// winner.</item>
/// <item><c>TryResumeFromPauseAsync</c>: claims once, retires the envelope, and is DISJOINT from
/// <c>TryBeginResumeAsync</c> in both directions.</item>
/// </list>
/// The four-part resumability shape (state · no CompletedAt · the aborted step back to Pending · the reason
/// token) is asserted here minus the step half — no step is in flight when the service is the only actor. The
/// step half, and an actual resume-to-completion, belong to the orchestrator facts in G3/G4.
/// </para>
/// </summary>
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

    /// <summary>
    /// <b>REGRESSION</b>, and the load-bearing half of hazard 1: a pause must leave a RESUMABLE run, not a
    /// settled one. The failure this pins is silent in the sense that the run DOES settle — just terminally:
    /// <c>FailAsync</c> stamps <c>CompletedAt</c> unconditionally, so a pause routed through it produces a run
    /// the panel calls finished, the sweep leaves alone and no claim can ever pick up.
    /// <para>
    /// The reason token is asserted through <see cref="RunPauseEnvelope"/> rather than by string-matching the
    /// column, because the envelope reader is what the panel and the Flow surface actually use: a writer that
    /// spelled the JSON by hand (rather than through the shared <c>{paused,reason}</c> shape) would satisfy a
    /// <c>Contains("user")</c> assertion and still read back as "no stated reason".
    /// </para>
    /// Neutralize: point the CAS at <c>FailAsync</c>'s statement — <c>CompletedAt</c> reds.
    /// </summary>
    [Fact]
    public async Task TryPauseUser_FromRunning_WritesPausedWithTheUserReason_AndNoCompletedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        await _service.SetStateAsync(run.Id, AgentRunState.Running, ct);

        Assert.True(await _service.TryPauseUserAsync(run.Id, ct));

        var paused = await _service.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, paused!.State);
        Assert.Null(paused.CompletedAt);                                            // a pause is NOT a completion
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(paused));

        // The envelope shape itself, pinned once: the reader requires paused==true, so a writer that emitted
        // only a reason would read back as null and the assertion above would be the only thing to notice.
        var extra = JsonNode.Parse(paused.ExtraJson!)!;
        Assert.True(extra["paused"]!.GetValue<bool>());
        Assert.Equal("user", extra["reason"]!.GetValue<string>());
    }

    /// <summary>
    /// <b>GUARD</b> — the explicit source set, pinned member by member instead of as a range (D7).
    /// <see cref="AgentRunState.Verifying"/> is pausable because the critic's provider call is as
    /// interruptible as a step's; <see cref="AgentRunState.WaitingForChildren"/> is pausable because a pause
    /// can land before the un-park CAS moves a fan-out parent back to <see cref="AgentRunState.Running"/>.
    /// <para>
    /// Non-vacuity for the pair lives in the sibling theory below, which asserts the complement.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(AgentRunState.Verifying)]
    [InlineData(AgentRunState.WaitingForChildren)]
    public async Task TryPauseUser_FromVerifying_AndFromWaitingForChildren_AlsoWin(AgentRunState from)
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        await _service.SetStateAsync(run.Id, from, ct);      // blind: the row is exactly the state named

        Assert.True(await _service.TryPauseUserAsync(run.Id, ct));

        var paused = await _service.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, paused!.State);
        Assert.Null(paused.CompletedAt);
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(paused));
    }

    /// <summary>
    /// <b>REGRESSION</b> — the complement of the set, and two distinct defects in one theory.
    /// <list type="bullet">
    /// <item><see cref="AgentRunState.Planning"/> is refused because a resume runs
    /// <c>RunAsync(resume: true)</c>, which skips planning entirely: a run paused mid-plan would come back
    /// with NO plan, drain zero steps and settle Completed having done nothing at all.</item>
    /// <item><see cref="AgentRunState.Completed"/>/<see cref="AgentRunState.Failed"/>/
    /// <see cref="AgentRunState.Cancelled"/> are R11 — a blind write here would resurrect a run somebody else
    /// already settled, live in the panel and owned by nobody.</item>
    /// <item><see cref="AgentRunState.WaitingForInput"/> and <see cref="AgentRunState.Paused"/> are already
    /// parked; re-stamping the envelope would overwrite a budget reason with <c>"user"</c>.</item>
    /// </list>
    /// The assertion is on the WHOLE row, not just the state, and <c>LedgerJson</c> is the column that earns
    /// it: an ungated <c>MoveLedgerClock</c> would close the work segment of a run this caller lost, and
    /// State/CompletedAt/ExtraJson/UpdatedAt would all still read byte-identical.
    /// <para>Neutralize: drop the <c>affected &gt; 0</c> gate → every row reds on the ledger.</para>
    /// </summary>
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

    /// <summary>
    /// <b>REGRESSION</b>. The pause closes the ledger work segment, mirroring <c>PauseAsync</c>: the gap a
    /// paused run sits in is not worked time, and the resume claim opens a fresh segment. Without it a run
    /// paused overnight bills the night.
    /// <para>
    /// The open segment is back-dated 3 s first, for the reason the child-wait fact states: a freshly created
    /// run's segment is microseconds old, so "the clock froze" and "the clock kept running" would be the same
    /// number and the fact would pass with the close deleted.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TryPauseUser_ClosesTheLedgerWorkSegment()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        await _service.SetStateAsync(run.Id, AgentRunState.Running, ct);
        BackdateOpenSegment(run.Id, TimeSpan.FromSeconds(3));

        Assert.True(await _service.TryPauseUserAsync(run.Id, ct));

        Assert.Null(SegmentStartedAt(run.Id));
        var frozen = WallClockMs(run.Id);
        Assert.InRange(frozen, 3_000, 60_000);      // the 3 s worked before the pause was banked
        Assert.Equal(frozen, ActiveMs(run.Id));     // and banked into the accumulator, not just reported
    }

    /// <summary>
    /// <b>REGRESSION</b> — §19 Q6. The fact above pins ONE close; this runs the clock through a DOUBLE
    /// pause→resume→pause cycle, because the failure it looks for cannot show on a first cycle: an ASYMMETRIC
    /// open/close pairing corrupts accumulated active time only from the SECOND close onwards. Two shapes, both
    /// caught here — a close that re-banks a segment an earlier close already banked (the total jumps ahead of
    /// the work), and a resume that fails to re-open one (the total stops moving while the run works).
    /// <para>
    /// This is hermes #3's "must" (persist accumulated active ms; count only the post-resume delta) measured
    /// rather than argued: <c>ApplyLedgerClock</c>'s <c>ActiveMs</c> + <c>SegmentStartedAt</c> pair is that
    /// counter, and the old <c>UtcNow - StartedAt</c> snapshot it replaced would report ~15 s of work here on
    /// the first pause and grow through every parked gap thereafter.
    /// </para>
    /// <para>
    /// The two cycles deliberately go through DIFFERENT pairs — cycle 1 the budget park
    /// (<c>PauseAsync</c>/<c>TryBeginResumeAsync</c>, state 3), cycle 2 the user pause
    /// (<c>TryPauseUserAsync</c>/<c>TryResumeFromPauseAsync</c>, state 4). All four call the same
    /// <c>MoveLedgerClock</c>, so a mixed run also catches the next park that forgets its clock move; two
    /// identical cycles would only ever re-test one pair.
    /// </para>
    /// Each segment is back-dated to a DIFFERENT length (3 s, 5 s, 7 s) so every delta is distinguishable: with
    /// equal lengths a double-banked segment and a correct one can produce the same total. Neutralize: drop
    /// <c>MoveLedgerClock(OpenSegment)</c> from <c>TryResumeFromPauseAsync</c> — cycle 2's segment never opens,
    /// which no one-cycle fact in this file can see.
    /// </summary>
    [Fact]
    public async Task TheLedgerClock_AccumulatesAcrossADoublePauseResumePauseCycle()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        await _service.SetStateAsync(run.Id, AgentRunState.Running, ct);

        // ---- cycle 1: 3 s of work, closed by the BUDGET park ----
        BackdateOpenSegment(run.Id, TimeSpan.FromSeconds(3));
        await _service.PauseAsync(run.Id, "step-cap", ct);
        Assert.Null(SegmentStartedAt(run.Id));
        var banked1 = ActiveMs(run.Id)!.Value;
        Assert.InRange(banked1, 3_000, 5_000);
        Assert.Equal(banked1, WallClockMs(run.Id));

        // The resume opens a FRESH segment and must not move the accumulator: an open that banked its own
        // (zero-length) segment would be harmless here, but one that re-banked the closed 3 s would not.
        Assert.True(await _service.TryBeginResumeAsync(run.Id, ct));
        Assert.NotNull(SegmentStartedAt(run.Id));
        Assert.Equal(banked1, ActiveMs(run.Id)!.Value);

        // ---- cycle 2: 5 s of work, closed by the USER pause ----
        BackdateOpenSegment(run.Id, TimeSpan.FromSeconds(5));
        Assert.True(await _service.TryPauseUserAsync(run.Id, ct));
        Assert.Null(SegmentStartedAt(run.Id));
        var banked2 = ActiveMs(run.Id)!.Value;
        // THE Q6 ASSERTION: the second close banks its own 5 s and nothing else. A close that re-banked
        // cycle 1's segment would read 8 s here, which is why the upper bound sits below 3 s + 5 s.
        Assert.InRange(banked2 - banked1, 5_000, 7_000);
        Assert.Equal(banked2, WallClockMs(run.Id));

        Assert.True(await _service.TryResumeFromPauseAsync(run.Id, ct));
        Assert.NotNull(SegmentStartedAt(run.Id));
        Assert.Equal(banked2, ActiveMs(run.Id)!.Value);

        // ---- the third close of one run's clock: 7 s, the user pause again ----
        BackdateOpenSegment(run.Id, TimeSpan.FromSeconds(7));
        Assert.True(await _service.TryPauseUserAsync(run.Id, ct));
        Assert.Null(SegmentStartedAt(run.Id));
        var total = ActiveMs(run.Id)!.Value;
        Assert.InRange(total - banked2, 7_000, 9_000);
        Assert.InRange(total, 15_000, 18_000);          // 3 + 5 + 7 worked, and NOT a re-banked 20 s+
        Assert.Equal(total, WallClockMs(run.Id));

        // Still resumable after two full cycles — an accounting pin that left the run unclaimable would be
        // measuring the wrong thing.
        Assert.True(await _service.TryResumeFromPauseAsync(run.Id, ct));
    }

    /// <summary>
    /// <b>REGRESSION</b> — guardrail 2 (never two loops on one run) for the new claim, plus W10's deliberate
    /// erasure. The second call must lose: two racers (a double-click, the panel and the Flow card) both see
    /// <see cref="AgentRunState.Paused"/> and both call this.
    /// <para>
    /// <c>ExtraJson=NULL</c> on the win is intentional and is the same reasoning <c>TryBeginResumeAsync</c>
    /// gives: the claim RETIRES the marker it just consumed. A resumed run that completes cleanly never
    /// rewrites the column (a non-truncated <c>CompleteAsync</c> leaves it alone), so a retained marker would
    /// have the panel and the Flow surface offering Continue on a finished run forever.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TryResumeFromPause_ClaimsOnce_AndNullsTheEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        await _service.SetStateAsync(run.Id, AgentRunState.Running, ct);
        Assert.True(await _service.TryPauseUserAsync(run.Id, ct));
        Assert.NotNull((await _service.GetAsync(run.Id, ct))!.ExtraJson);
        Assert.Null(SegmentStartedAt(run.Id));                  // paused ⇒ no open segment

        Assert.True(await _service.TryResumeFromPauseAsync(run.Id, ct));

        var resumed = await _service.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Running, resumed!.State);
        Assert.Null(resumed.ExtraJson);
        Assert.NotNull(SegmentStartedAt(run.Id));               // a FRESH work segment on the win

        // The loser: the second racer finds State != Paused and must change nothing.
        var afterWin = RowSnapshot(run.Id);
        Assert.False(await _service.TryResumeFromPauseAsync(run.Id, ct));
        Assert.Equal(afterWin, RowSnapshot(run.Id));
    }

    /// <summary>
    /// <b>GUARD</b>. The two claim methods are DISJOINT by source state, asserted from BOTH sides — which is
    /// what makes the launcher's state-dispatched claim (G3) correct rather than lucky, and what lets each CAS
    /// keep a single-valued <c>@Expected</c>.
    /// <para>
    /// The envelope assertions are the sharper half: a claim that lost must not have nulled the marker on its
    /// way out, or a budget-parked run silently loses the reason its Flow card and activity line are keyed on.
    /// </para>
    /// Neutralize: widen either CAS's source to accept both states → the matching half reds.
    /// </summary>
    [Fact]
    public async Task TryResumeFromPause_DoesNotClaimAWaitingForInputRun_AndTryBeginResumeDoesNotClaimAPausedOne()
    {
        var ct = TestContext.Current.CancellationToken;

        // A BUDGET-parked run: WaitingForInput, belongs to TryBeginResumeAsync alone.
        var atBudget = await NewRunAsync(ct);
        await _service.PauseAsync(atBudget.Id, "step-cap", ct);
        Assert.False(await _service.TryResumeFromPauseAsync(atBudget.Id, ct));
        var stillParked = await _service.GetAsync(atBudget.Id, ct);
        Assert.Equal(AgentRunState.WaitingForInput, stillParked!.State);
        Assert.Equal("step-cap", RunPauseEnvelope.ReadReason(stillParked));

        // A USER-paused run: Paused, belongs to TryResumeFromPauseAsync alone.
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

    /// <summary>
    /// <b>REGRESSION</b>. <c>RunChanged(Paused)</c> fires on the WIN only. A spurious event on a lost CAS is
    /// not cosmetic: the Flow surface publishes and retracts run cards straight off this event, so an event for
    /// a run nobody paused would post an ActionRequired "continue?" card carrying a resume action that the
    /// disjoint claim above then refuses.
    /// </summary>
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
            Assert.Equal([(winner.Id, AgentRunState.Paused)], seen);   // nothing added by the loss
        }
        finally
        {
            _service.RunChanged -= Handler;
        }
    }

    /// <summary>
    /// <b>GUARD</b> — the crash sweep, extended to the state a user pause now really produces. The existing
    /// theory in <c>AgentRunServiceChildWaitTests</c> covers a bare <see cref="AgentRunState.Paused"/> row;
    /// what this adds is the ENVELOPE, written by the real CAS: statement 1 must not sweep the row (its
    /// <c>@Terminal</c> threshold is <see cref="AgentRunState.WaitingForInput"/>, so 4 is above it) and
    /// statement 2's re-park must not reach it either (it selects on
    /// <see cref="AgentRunState.WaitingForChildren"/>), or a restart would overwrite <c>"user"</c> with
    /// <c>"children-interrupted"</c>.
    /// <para>
    /// <c>CompletedAt</c> is the assertion that catches a moved threshold: statement 1 stamps one, so a swept
    /// row is distinguishable from an untouched one even after somebody "fixes" the state back.
    /// </para>
    /// </summary>
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

        // And it is still claimable afterwards — "survives restart resumable" is the whole promise.
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

    /// <summary>
    /// EVERY column of the run row, rendered as one string. A "wrote nothing" claim about a lost CAS has to
    /// cover the columns nobody thinks about — <c>LedgerJson</c> above all, which an ungated ledger move would
    /// rewrite while State, CompletedAt, ExtraJson and UpdatedAt all stayed identical.
    /// </summary>
    private string RowSnapshot(Guid runId)
    {
        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT * FROM AgentRuns WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());                 // no row means the test itself is wrong

        var sb = new StringBuilder();
        for (var i = 0; i < reader.FieldCount; i++)
            sb.Append(reader.GetName(i)).Append('=').Append(reader.GetValue(i)).Append('|');
        return sb.ToString();
    }

    // ---- raw ledger access, mirroring AgentRunServiceChildWaitTests' fixture: the service reads UtcNow, so a
    // test simulates elapsed work by moving the persisted timestamp instead of sleeping. ----

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
        Assert.NotNull(node["segmentStartedAt"]);   // nothing to back-date otherwise — the test is wrong
        node["segmentStartedAt"] = JsonValue.Create((DateTime.UtcNow - by).ToString("O"));

        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "UPDATE AgentRuns SET LedgerJson = @Ledger WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Ledger", node.ToJsonString());
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        cmd.ExecuteNonQuery();
    }
}

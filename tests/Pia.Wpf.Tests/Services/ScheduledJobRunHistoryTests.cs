using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// T2-18 — the per-job run history, which is a QUERY over the run rows rather than a new table. Before this,
/// `ScheduledJobs.LastResultEntryId` was the only record of a firing and it is overwritten on every one.
/// <para>
/// The facts worth the file are the exclusions: a CHILD run (null <c>TriggerRef</c>) is not a firing of its
/// parent's job, a PARKED run is not a firing outcome at all, and another job's runs never leak in.
/// </para>
/// </summary>
public sealed class ScheduledJobRunHistoryTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;

    public ScheduledJobRunHistoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaRunHistory_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> NewChatAsync()
    {
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = chatId, SchemaVersion = 1, Title = "t",
            CreatedAt = now, UpdatedAt = now, LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(), Messages = [],
        }, Ct);
        return chatId;
    }

    /// <summary>Creates a run for <paramref name="triggerRef"/> and settles it, or parks it when asked.</summary>
    private async Task<AgentRun> FiringAsync(Guid? triggerRef, AgentRunState settle, Guid? parentRunId = null)
    {
        var run = await _runs.CreateAsync(
            new AgentRunCreateRequest(await NewChatAsync(), RunShape.Planned, AgentRunTrigger.Schedule,
                TriggerRef: triggerRef, ParentRunId: parentRunId, Goal: "g"), Ct);

        switch (settle)
        {
            case AgentRunState.Completed:
                await _runs.CompleteAsync(run.Id, ct: Ct);
                break;
            case AgentRunState.Failed:
                await _runs.FailAsync(run.Id, "boom", ct: Ct);
                break;
            case AgentRunState.WaitingForInput:
                await _runs.PauseAsync(run.Id, "step-cap", Ct);
                break;
            default:
                await _runs.SetStateAsync(run.Id, settle, Ct);
                break;
        }

        return run;
    }

    [Fact]
    public async Task TheHistoryListsEveryFiringOfTheJob_NewestFirst()
    {
        var job = Guid.NewGuid();
        var first = await FiringAsync(job, AgentRunState.Completed);
        var second = await FiringAsync(job, AgentRunState.Failed);
        var third = await FiringAsync(job, AgentRunState.Completed);

        var history = await _runs.GetFiringsForTriggerAsync(job, 10, Ct);

        Assert.Equal(3, history.Count);
        Assert.Equal([third.Id, second.Id, first.Id], history.Select(h => h.RunId).ToArray());
        Assert.All(history, h => Assert.Equal(job, h.JobId));
        Assert.Equal(AgentRunState.Failed, history[1].State);
    }

    [Fact]
    public async Task TheLimitIsHonoured_AndKeepsTheNewest()
    {
        var job = Guid.NewGuid();
        await FiringAsync(job, AgentRunState.Completed);
        await FiringAsync(job, AgentRunState.Completed);
        var newest = await FiringAsync(job, AgentRunState.Failed);

        var history = await _runs.GetFiringsForTriggerAsync(job, 2, Ct);

        Assert.Equal(2, history.Count);
        Assert.Equal(newest.Id, history[0].RunId);
    }

    /// <summary>
    /// A zero or negative limit must not mean "everything" (SQLite reads a negative LIMIT as unbounded) and must
    /// not mean "nothing" either — it is clamped to one row.
    /// </summary>
    [Fact]
    public async Task AnAbsurdLimit_IsClamped()
    {
        var job = Guid.NewGuid();
        await FiringAsync(job, AgentRunState.Completed);
        await FiringAsync(job, AgentRunState.Completed);

        Assert.Single(await _runs.GetFiringsForTriggerAsync(job, 0, Ct));
        Assert.Single(await _runs.GetFiringsForTriggerAsync(job, -5, Ct));
    }

    [Fact]
    public async Task AnotherJobsFirings_DoNotLeakIn()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var mineRun = await FiringAsync(mine, AgentRunState.Completed);
        await FiringAsync(theirs, AgentRunState.Completed);

        var history = await _runs.GetFiringsForTriggerAsync(mine, 10, Ct);

        Assert.Single(history);
        Assert.Equal(mineRun.Id, history[0].RunId);
    }

    /// <summary>
    /// A PARKED firing is absent, the same predicate <c>GetLatestSettledFiringsAsync</c> uses: it has no settle
    /// instant to order or label it by, and it is still live — the run panel is where a run waiting for a person
    /// belongs.
    /// </summary>
    [Fact]
    public async Task AParkedFiring_IsNotHistoryYet()
    {
        var job = Guid.NewGuid();
        var settled = await FiringAsync(job, AgentRunState.Completed);
        await FiringAsync(job, AgentRunState.WaitingForInput);

        var history = await _runs.GetFiringsForTriggerAsync(job, 10, Ct);

        Assert.Single(history);
        Assert.Equal(settled.Id, history[0].RunId);
    }

    /// <summary>
    /// A delegated CHILD run carries a null <c>TriggerRef</c> by design (07 D7), so a fan-out cannot inflate its
    /// parent job's history with runs the schedule never fired.
    /// </summary>
    [Fact]
    public async Task ChildRuns_AreNotFiringsOfTheParentsJob()
    {
        var job = Guid.NewGuid();
        var parent = await FiringAsync(job, AgentRunState.Completed);
        await FiringAsync(triggerRef: null, AgentRunState.Completed, parentRunId: parent.Id);

        var history = await _runs.GetFiringsForTriggerAsync(job, 10, Ct);

        Assert.Single(history);
        Assert.Equal(parent.Id, history[0].RunId);
    }

    [Fact]
    public async Task AJobThatHasNeverFired_HasAnEmptyHistory()
    {
        Assert.Empty(await _runs.GetFiringsForTriggerAsync(Guid.NewGuid(), 5, Ct));
    }

    public void Dispose()
    {
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }
}

using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// T2-G1: the telemetry seam. <see cref="IRunObserver"/> is a read-only bystander on the ONE append
/// entrypoint the store already has, and every test here is about the same property from a different angle —
/// <b>the observer cannot hurt the audit trail</b>.
/// <para>
/// Every run emits against a REAL chat + run row, because <c>AgentTimelineEvents.RunId</c> has an enforced FK:
/// a bare <c>Guid.NewGuid()</c> would have its INSERT rejected, logged and dropped, producing a silently green
/// "zero rows" test. And every emit-then-observe awaits a barrier (<c>DrainAsync</c> for the table,
/// <c>ObserverDrainAsync</c> for the seam) rather than sleeping — the two chains are independent, so which
/// barrier a test awaits is itself part of what it asserts.
/// </para>
/// </summary>
public sealed class AgentTimelineObserverTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly List<AgentTimelineService> _services = [];

    public AgentTimelineObserverTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
    }

    // ---- fail-open: an observer cannot cost the row ----

    [Fact]
    public async Task ThrowingObserver_StillLandsTheAuditRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var thrower = new ThrowingObserver();
        var alsoRegistered = new RecordingObserver();
        var svc = Service(thrower, alsoRegistered);

        EmitOne(new AgentTimelineScope(svc, run.Id, stepId: null), "write_file");

        var rows = await svc.GetForRunAsync(run.Id, ct);
        await svc.ObserverDrainAsync();

        // The row — the system of record — is untouched by the throw.
        var row = Assert.Single(rows);
        Assert.Equal(1, row.Seq);
        Assert.Equal("write_file", row.ToolName);
        // The observer really did throw (otherwise this is a test of nothing) and the NEXT observer still ran:
        // the try/catch is per CALL, not per notification.
        Assert.Equal(1, thrower.Calls);
        Assert.Single(alsoRegistered.Seen);
    }

    [Fact]
    public async Task BlockedObserver_DoesNotBlockTheAuditWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var blocker = new BlockingObserver(entered, release);
        var svc = Service(blocker);

        EmitOne(new AgentTimelineScope(svc, run.Id, stepId: null), "write_file");

        try
        {
            // A STATE fact, not a stopwatch: proceed only once the observer is demonstrably inside its
            // callback and stuck there. Bounded so a broken build fails instead of hanging the suite.
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30), ct), "the observer was never notified");

            // The audit chain is a different chain, so this must complete WHILE the observer is still blocked.
            // BOUNDED, and that bound IS the assertion: if the notification were chained onto _writeTail,
            // GetForRunAsync's opening `await DrainAsync()` would never return — `release` is only Set in the
            // finally below, which this method would never reach. Awaited unbounded, that regression hangs the
            // whole suite indefinitely instead of failing one test.
            var readTask = svc.GetForRunAsync(run.Id, ct);
            Assert.Same(
                readTask,
                await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(30), ct)));

            var rows = await readTask;
            Assert.Single(rows);
            // Corroboration, not proof: `release` is still unset here, so this flag is false by construction.
            // It is kept because it names the property the bounded read above actually establishes — the row was
            // readable BEFORE the callback returned.
            Assert.False(blocker.Completed, "the row was only readable after the observer returned");

            // Shutdown is not hostage to a bystander either: Dispose waits on _writeTail alone.
            svc.Dispose();
        }
        finally
        {
            release.Set();
            await svc.ObserverDrainAsync();
        }
    }

    // ---- ordering: the seam sees the table's order ----

    [Fact]
    public async Task Observers_SeeEventsInSeqOrder()
    {
        var run = await MakeRunAsync();
        var observer = new RecordingObserver();
        var svc = Service(observer);
        var scope = new AgentTimelineScope(svc, run.Id, stepId: null);

        for (var i = 0; i < 5; i++)
            EmitOne(scope, $"tool_{i}");

        await svc.ObserverDrainAsync();

        // Enqueued under the same lock that allocated Seq, executed on one serial chain: the observed order is
        // the table's order, with no gaps and no reordering.
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, observer.Seen.Select(e => e.Seq).ToArray());
        Assert.Equal(new[] { "tool_0", "tool_1", "tool_2", "tool_3", "tool_4" },
            observer.Seen.Select(e => e.ToolName).ToArray());
    }

    // ---- re-entrancy ----

    [Fact]
    public async Task ReentrantObserver_DoesNotRecurse()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        // Capped at 10 re-emits on purpose: without the _inNotify guard this observer feeds itself forever, and
        // an unbounded loop in the background would poison the whole suite rather than fail one test.
        var observer = new ReentrantObserver(run.Id, maxReemits: 10);
        var svc = Service(observer);
        observer.Attach(svc);

        EmitOne(new AgentTimelineScope(svc, run.Id, stepId: null), "write_file");

        await svc.ObserverDrainAsync();

        // THE load-bearing assertion. "The test terminated" proves nothing — ObserverDrainAsync captures the
        // FIRST tail, which completes even if each notification enqueues another. The dispatch count is what
        // shows the recursive row was written but NOT notified.
        Assert.Equal(1, svc.NotifyDispatches);
        Assert.Equal(1, observer.Calls);

        // ...and the re-entrant emit was in no way suppressed: the audit table has both rows.
        var rows = await svc.GetForRunAsync(run.Id, ct);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new long[] { 1, 2 }, rows.Select(r => r.Seq).ToArray());
    }

    // ---- zero observers cost nothing (both halves; the zero case alone is vacuous) ----

    [Fact]
    public async Task OneObserver_IncrementsNotifyDispatches()
    {
        var run = await MakeRunAsync();
        var observer = new RecordingObserver();
        var svc = Service(observer);
        var scope = new AgentTimelineScope(svc, run.Id, stepId: null);

        for (var i = 0; i < 3; i++)
            EmitOne(scope, "write_file");

        await svc.ObserverDrainAsync();

        Assert.Equal(3, svc.NotifyDispatches);
        Assert.Equal(3, observer.Seen.Count);
    }

    [Fact]
    public async Task ZeroObservers_NeverDispatches()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var svc = Service(); // the production default: no IRunObserver registered anywhere
        var scope = new AgentTimelineScope(svc, run.Id, stepId: null);

        for (var i = 0; i < 3; i++)
            EmitOne(scope, "write_file");

        // Nothing was enqueued at all — no delegate, no Task, no ContinueWith — while the rows landed as usual.
        // The dispatch count is the whole observation: an identity check on _observerTail would fail (or pass)
        // for reasons unrelated to whether the notify path did any work.
        Assert.Equal(0, svc.NotifyDispatches);
        Assert.Equal(3, (await svc.GetForRunAsync(run.Id, ct)).Count);
    }

    // ---- the two consumers must agree about what happened ----

    [Fact]
    public async Task CappedEvents_AreNotObserved()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var observer = new RecordingObserver();
        var svc = Service(observer);
        var scope = new AgentTimelineScope(svc, run.Id, stepId: null);

        for (var i = 0; i < AgentTimelineService.MaxEventsPerRun + 50; i++)
            EmitOne(scope, "write_file");

        await svc.ObserverDrainAsync();
        var rows = await svc.GetForRunAsync(run.Id, ct);

        // The truncation marker IS a row, so it IS observed; the 50 events dropped after it are not. Observer
        // and table see exactly the same stream — a seam that reported events the table refused would be a
        // second, disagreeing account of the run.
        Assert.Equal(AgentTimelineService.MaxEventsPerRun + 1, rows.Count);
        Assert.Equal(rows.Count, observer.Seen.Count);
        Assert.Equal(AgentTimelineService.MaxEventsPerRun + 1, svc.NotifyDispatches);
        Assert.Equal(AgentTimelineEventKind.TraceTruncated, observer.Seen[^1].Kind);
        Assert.Single(observer.Seen, e => e.Kind == AgentTimelineEventKind.TraceTruncated);
    }

    [Fact]
    public async Task Observer_SeesTheWidenedRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var stepId = Guid.NewGuid();
        var observer = new RecordingObserver();
        var svc = Service(observer);
        var scope = new AgentTimelineScope(svc, run.Id, stepId);

        scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null,
            ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok,
            toolCallId: "call_abc123", round: 3, requestedAt: null, decidedAt: null);

        await svc.ObserverDrainAsync();
        var persisted = Assert.Single(await svc.GetForRunAsync(run.Id, ct));
        var seen = Assert.Single(observer.Seen);

        // The SERVICE-ASSIGNED fields are what discriminate here: the seam is handed `row` (post-allocation),
        // not the caller's `e`, on which both of these are still 0/null. The caller-supplied T2-14 columns
        // would look identical either way, so they are the corroboration, not the proof.
        Assert.Equal(1, seen.Seq);
        Assert.Equal(1, seen.StepOrdinal);
        Assert.Equal(persisted.Seq, seen.Seq);
        Assert.Equal(persisted.StepOrdinal, seen.StepOrdinal);
        Assert.Equal("call_abc123", seen.ToolCallId);
        Assert.Equal(3, seen.Round);
        Assert.Equal(stepId, seen.StepId);
    }

    // ---- fixture helpers ----

    private AgentTimelineService Service(params IRunObserver[] observers)
    {
        // Mirrors the production ctor: an empty array is what MS.DI injects with zero registrations.
        var svc = new AgentTimelineService(_ctx, NullLogger<AgentTimelineService>.Instance, observers);
        _services.Add(svc);
        return svc;
    }

    private static void EmitOne(AgentTimelineScope scope, string toolName) =>
        scope.Emit(ToolGateSurface.Unattended, toolName, ToolClass.Files, null,
            ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok,
            toolCallId: null, round: null, requestedAt: null, decidedAt: null);

    private async Task<AgentRun> MakeRunAsync()
    {
        var chatId = await MakeChatAsync();
        return await _runs.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User),
            TestContext.Current.CancellationToken);
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

    public void Dispose()
    {
        foreach (var svc in _services) svc.Dispose();
        _runs.Dispose();
        _ctx.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }

    // ---- observer doubles ----

    private sealed class RecordingObserver : IRunObserver
    {
        private readonly ConcurrentQueue<AgentTimelineEvent> _seen = new();

        /// <summary>Snapshot, so an assertion cannot race a late notification into the middle of a comparison.</summary>
        public IReadOnlyList<AgentTimelineEvent> Seen => _seen.ToArray();

        public void OnTimelineEvent(AgentTimelineEvent e) => _seen.Enqueue(e);
    }

    private sealed class ThrowingObserver : IRunObserver
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void OnTimelineEvent(AgentTimelineEvent e)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("observer is broken");
        }
    }

    private sealed class BlockingObserver(ManualResetEventSlim entered, ManualResetEventSlim release) : IRunObserver
    {
        private volatile bool _completed;

        /// <summary>The callback RETURNED. Volatile because the assertion reads it from the test thread while
        /// the notification chain writes it from a pool thread.</summary>
        public bool Completed => _completed;

        public void OnTimelineEvent(AgentTimelineEvent e)
        {
            entered.Set();
            release.Wait();
            _completed = true;
        }
    }

    /// <summary>An observer that emits from inside its own notification — the self-feeding case.</summary>
    private sealed class ReentrantObserver(Guid runId, int maxReemits) : IRunObserver
    {
        private AgentTimelineService? _service;
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Attach(AgentTimelineService service) => _service = service;

        public void OnTimelineEvent(AgentTimelineEvent e)
        {
            if (Interlocked.Increment(ref _calls) > maxReemits) return;

            _service!.Emit(new AgentTimelineEvent(
                Id: Guid.NewGuid(),
                RunId: runId,
                StepId: null,
                Seq: 0,
                Kind: AgentTimelineEventKind.ToolCall,
                Surface: ToolGateSurface.Unattended,
                Decision: ToolGateDecision.GrantedByName,
                Outcome: AgentTimelineOutcome.Ok,
                ToolName: "reentrant",
                ToolClass: ToolClass.Files,
                PluginId: null,
                ArgsChars: null,
                ResultChars: null,
                DurationMs: null,
                CreatedAt: DateTime.UtcNow,
                ToolCallId: null,
                Round: null,
                StepOrdinal: null,
                RequestedAt: null,
                DecidedAt: null));
        }
    }
}

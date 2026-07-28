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
/// Batch 10 W1/W2 regression net: the REAL <see cref="AssistantChatService"/> and the REAL
/// <see cref="AgentRunService"/> on ONE temp SQLite file, exercised from several threads at once. Deliberately
/// not built on the interface-substituting sync-service tests — those never touch SQLite, so they cannot see
/// the bug at all.
/// <para>
/// WHAT THESE MUST FAIL ON: before W1, two threads writing two DIFFERENT chats through the shared
/// <c>SqliteContext.GetConnection()</c> handle raise "SqliteConnection does not support nested transactions"
/// from the second <c>BeginTransaction</c>, and any untransacted read issued while a transaction is pending
/// raises "Execute requires the command to have a transaction object when the connection assigned to the
/// command is in a pending local transaction". If a test here ever passes on the pre-W1 tree, the writer is
/// not actually holding a transaction open while the other thread runs and the test is worthless.
/// </para>
/// <para>
/// net10.0-windows cannot execute on macOS — these tests are WRITTEN, not run; execution is deferred to
/// Windows/CI.
/// </para>
/// </summary>
public sealed class AssistantChatConcurrencyTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;

    public AssistantChatConcurrencyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
    }

    private static SyncAssistantChat Chat(Guid id, string title, params string[] bodies)
    {
        var now = DateTime.UtcNow;
        return new SyncAssistantChat
        {
            Id = id,
            SchemaVersion = 1,
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(),
            Messages = [.. bodies.Select(b => new SyncAssistantChatMessage
            {
                Id = Guid.NewGuid(),
                Role = "user",
                Content = b,
                Timestamp = now,
            })],
        };
    }

    [Fact]
    public async Task TwoConcurrentWritersOnDifferentChats_PlusAReaderThread_RaiseNoExceptions()
    {
        // The headline W1 case. E2 moved the chat write from once per run to once per COMPLETED STEP on pool
        // threads, with a headless slot cap of 2 — so two runs writing at once is the shipped cadence, not a
        // synthetic scenario. A third thread hammers the untransacted readers throughout, which is where the
        // only user-visible symptom lived (a pool-thread ChatsChanged posts SearchAsync from the history view
        // model; it threw and was swallowed, leaving a stale list).
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var errors = new ConcurrentBag<Exception>();
        using var stopReading = new CancellationTokenSource();

        var reader = Task.Run(async () =>
        {
            while (!stopReading.IsCancellationRequested)
            {
                try
                {
                    await _chats.SearchAsync(searchText: "message");
                    await _chats.GetAsync(a);
                    await _chats.GetAsync(b);
                    await _chats.GetAllIdsAsync();
                    await _chats.GetMaxUpdatedAtAsync();
                }
                catch (Exception ex) { errors.Add(ex); }
            }
        }, TestContext.Current.CancellationToken);

        async Task WriteManyAsync(Guid id, string tag)
        {
            for (var i = 0; i < 40; i++)
            {
                try
                {
                    await _chats.SaveAsync(Chat(id, tag + " title",
                        [.. Enumerable.Range(0, i + 1).Select(n => $"{tag} message {n}")]));
                }
                catch (Exception ex) { errors.Add(ex); }
            }
        }

        await Task.WhenAll(
            Task.Run(() => WriteManyAsync(a, "alpha"), TestContext.Current.CancellationToken),
            Task.Run(() => WriteManyAsync(b, "beta"), TestContext.Current.CancellationToken));
        await stopReading.CancelAsync();
        await reader;

        Assert.Empty(errors);

        // Both chats are intact and neither writer's rows leaked into the other's.
        var storedA = await _chats.GetAsync(a, TestContext.Current.CancellationToken);
        var storedB = await _chats.GetAsync(b, TestContext.Current.CancellationToken);
        Assert.Equal(40, storedA!.Messages.Count);
        Assert.Equal(40, storedB!.Messages.Count);
        Assert.All(storedA.Messages, m => Assert.StartsWith("alpha", m.Content));
        Assert.All(storedB.Messages, m => Assert.StartsWith("beta", m.Content));
    }

    [Fact]
    public async Task AReadIssuedWhileAWriteIsInFlight_Completes_InsteadOfThrowing()
    {
        // The assertion that pins the decision to gate READS as well as writes. Exception (b) is raised by a
        // plain command whose .Transaction is unset executing while a transaction is pending on the same
        // connection, so on the pre-W1 tree the SearchAsync below throws rather than queueing.
        //
        // HONEST LIMITATION: AssistantChatService exposes no seam INSIDE its transaction (ChatsChanged fires
        // only after commit and after the gate is released, by design), so the reader cannot be released at a
        // guaranteed mid-transaction instant. What this test does instead is widen the window as far as the
        // public API allows — a single transaction with 400 message inserts — and start the reader as soon as
        // the writer task is running. The POST-fix expectation is unconditional (it must never throw, whatever
        // the interleaving); the PRE-fix failure is highly likely rather than guaranteed. The unconditional
        // half is the one worth having.
        var writerId = Guid.NewGuid();
        var writerStarted = new ManualResetEventSlim(false);
        Exception? readError = null;
        IReadOnlyList<SyncAssistantChat>? hits = null;

        var big = Chat(writerId, "big chat", [.. Enumerable.Range(0, 400).Select(n => "message " + n)]);

        var writer = Task.Run(async () =>
        {
            writerStarted.Set();
            await _chats.SaveAsync(big);
        }, TestContext.Current.CancellationToken);

        var reader = Task.Run(async () =>
        {
            writerStarted.Wait(TimeSpan.FromSeconds(10));
            try { hits = await _chats.SearchAsync(searchText: "message"); }
            catch (Exception ex) { readError = ex; }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(writer, reader);

        Assert.Null(readError);   // queued behind the write, not rejected by it
        Assert.NotNull(hits);
    }

    [Fact]
    public async Task AWriteOnTheDedicatedConnection_IsVisibleToAReaderOnTheSharedConnection()
    {
        // Cross-connection commit visibility: the chat store's handle is private, but the ten services still
        // on SqliteContext.GetConnection() must see its commits (and vice versa — WAL, DB1).
        var id = Guid.NewGuid();
        await _chats.SaveAsync(Chat(id, "visible", "one message"), TestContext.Current.CancellationToken);

        var shared = _ctx.GetConnection();
        using var count = shared.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM AssistantChatMessages WHERE ChatId = @Id";
        count.Parameters.AddWithValue("@Id", id.ToString());
        Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken)));

        using var fts = shared.CreateCommand();
        fts.CommandText = "SELECT COUNT(*) FROM AssistantChatsFts WHERE ChatId = @Id";
        fts.Parameters.AddWithValue("@Id", id.ToString());
        Assert.Equal(1, Convert.ToInt32(await fts.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task AWriteFromAnotherConnection_DoesNotBlockTheChatStoreIndefinitely()
    {
        // DB1's reason for existing: moving chat writes to a second connection converts an intra-connection
        // InvalidOperationException into a cross-connection SQLITE_BUSY. WAL + busy_timeout is what keeps that
        // from being an instant "database is locked" for the shared connection's services.
        var shared = _ctx.GetConnection();
        using var journal = shared.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", ((string?)await journal.ExecuteScalarAsync(TestContext.Current.CancellationToken))?.ToLowerInvariant());

        // A chat write while another connection reads concurrently must simply succeed.
        var id = Guid.NewGuid();
        using var other = new SqliteConnection(_ctx.ConnectionString);
        other.Open();
        var writeTask = _chats.SaveAsync(Chat(id, "wal chat", "body"), TestContext.Current.CancellationToken);
        using var read = other.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM AssistantChats";
        _ = await read.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        await writeTask;

        Assert.NotNull(await _chats.GetAsync(id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAllAsync_RaisesOnePerId_AndNoSubscriberRunsInsideTheGate()
    {
        // "Raise after release" is not a style preference: subscribers re-enter this service on the raising
        // thread (HeadlessRunLauncher.OnChatsChanged does a recursive Directory.Delete; the history and chip
        // view models post a SearchAsync back). A subscriber that awaits a chat read would deadlock against a
        // non-reentrant gate.
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await _chats.SaveAsync(Chat(id, "chat " + i, "body " + i), TestContext.Current.CancellationToken);
        }

        var raised = new List<Guid>();
        _chats.ChatsChanged += (_, e) =>
        {
            if (e.Kind != AssistantChatChangeKind.Deleted) return;
            raised.Add(e.Id);
            // Re-entrancy from the raising thread: hangs if the event is raised under the gate.
            _ = _chats.GetAllIdsAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        };

        var deleted = await _chats.DeleteAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, deleted.Count);
        Assert.Equal(ids.OrderBy(i => i), raised.OrderBy(i => i));
        Assert.Empty(await _chats.GetAllIdsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EvictOlderThanAsync_TakesTheRunServiceLookupOutsideItsOwnGate()
    {
        // Two service gates must never be held at once. This exercises the real AgentRunService (a
        // synchronous, lock-holding store) from inside EvictOlderThanAsync's filter: a run-bearing chat is
        // RETAINED (§16 R17) and the call happens with the chat gate released, so a concurrent chat read from
        // another thread cannot deadlock against it.
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        await _chats.SaveAsync(Chat(keep, "has a run", "body"), TestContext.Current.CancellationToken);
        await _chats.SaveAsync(Chat(drop, "no run", "body"), TestContext.Current.CancellationToken);
        await _runs.CreateAsync(new AgentRunCreateRequest(keep, RunShape.Planned,
            AgentRunTrigger.User, Goal: "goal"), TestContext.Current.CancellationToken);

        using var stop = new CancellationTokenSource();
        var errors = new ConcurrentBag<Exception>();
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await _chats.GetAllIdsAsync(); }
                catch (Exception ex) { errors.Add(ex); }
            }
        }, TestContext.Current.CancellationToken);

        var evicted = await _chats.EvictOlderThanAsync(DateTime.UtcNow.AddMinutes(1), TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await reader;

        Assert.Empty(errors);
        Assert.Contains(drop, evicted);
        Assert.DoesNotContain(keep, evicted);
        Assert.NotNull(await _chats.GetAsync(keep, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TitleOnlyWriteRacingAFullReplace_LosesNeitherTheTitleNorTheMessages()
    {
        // W2a end-to-end: SetTitleAsync and SaveAsync run concurrently on the SAME chat. Whatever the order,
        // the messages the full replace wrote must all be present — a title write cannot delete them.
        var id = Guid.NewGuid();
        await _chats.SaveAsync(Chat(id, "original", "m0"), TestContext.Current.CancellationToken);
        var grown = Chat(id, "original", "m0", "m1", "m2");

        var errors = new ConcurrentBag<Exception>();
        await Task.WhenAll(
            Task.Run(async () => { try { await _chats.SaveAsync(grown); } catch (Exception ex) { errors.Add(ex); } },
                TestContext.Current.CancellationToken),
            Task.Run(async () => { try { await _chats.SetTitleAsync(id, "llm title"); } catch (Exception ex) { errors.Add(ex); } },
                TestContext.Current.CancellationToken));

        Assert.Empty(errors);
        var final = await _chats.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(3, final!.Messages.Count);
    }

    [Fact]
    public async Task TwoAppendOnlyWritersMergingConcurrently_LoseNoRow()
    {
        // W2b's atomicity, end to end. A merging save that READ through GetAsync and then wrote through
        // SaveAsync would release the gate in between, so a writer committing in that gap still has its rows
        // deleted by the replace. Here BOTH writers append 40 rows each to the same chat, interleaved by the
        // scheduler; every row must be in the final table. This test is why the merge lives inside
        // AssistantChatService's gate hold rather than in the caller.
        var id = Guid.NewGuid();
        var seed = Chat(id, "shared", "m0");
        await _chats.SaveAsync(seed, TestContext.Current.CancellationToken);

        var errors = new ConcurrentBag<Exception>();
        var expected = new ConcurrentBag<Guid>(seed.Messages.Select(m => m.Id));

        async Task Writer(string tag)
        {
            // Each writer owns an append-only view — its seed row plus its own rows, exactly like a headless
            // run's _persisted list. It never sees the other writer's rows; the store merges them.
            var mine = new List<SyncAssistantChatMessage>(seed.Messages);
            for (var i = 0; i < 40; i++)
            {
                var row = new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "assistant",
                    Content = $"{tag}-{i}",
                    Timestamp = DateTime.UtcNow,
                };
                mine.Add(row);
                expected.Add(row.Id);
                var snapshot = Chat(id, "shared");
                snapshot.Messages = [.. mine];
                try { await _chats.SaveMergedAsync(snapshot); }
                catch (Exception ex) { errors.Add(ex); }
            }
        }

        await Task.WhenAll(
            Task.Run(() => Writer("a"), TestContext.Current.CancellationToken),
            Task.Run(() => Writer("b"), TestContext.Current.CancellationToken));

        Assert.Empty(errors);
        var final = await _chats.GetAsync(id, TestContext.Current.CancellationToken);
        var stored = final!.Messages.Select(m => m.Id).ToHashSet();
        foreach (var rowId in expected)
            Assert.Contains(rowId, stored);
        Assert.Equal(81, final.Messages.Count);
    }

    [Fact]
    public async Task SaveMergedAsync_OrdersAbsorbedRowsByTimestamp_NotByAppendOrder()
    {
        // W2b ordering: Ordinal is renumbered from the list index on every replace, so an absorbed row
        // appended at the TAIL makes "the agent's step reply printed before the question the user typed
        // mid-run" durable. The merge sorts by Timestamp instead.
        var id = Guid.NewGuid();
        var t0 = new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc);

        SyncAssistantChatMessage Row(string content, int minute) => new()
        {
            Id = Guid.NewGuid(),
            Role = "user",
            Content = content,
            Timestamp = t0.AddMinutes(minute),
        };

        var goal = Row("goal", 0);
        var step1 = Row("step 1 reply", 1);
        var userMid = Row("what about the other folder?", 2);
        var liveReply = Row("live reply", 3);
        var step2 = Row("step 2 reply", 4);

        // The DB after a live turn appended two rows behind the run's back.
        var afterLiveTurn = Chat(id, "c");
        afterLiveTurn.Messages = [goal, step1, userMid, liveReply];
        await _chats.SaveAsync(afterLiveTurn, TestContext.Current.CancellationToken);

        // The run's own append-only view knows nothing about the two middle rows.
        var runView = Chat(id, "c");
        runView.Messages = [goal, step1, step2];
        var absorbed = await _chats.SaveMergedAsync(runView, TestContext.Current.CancellationToken);

        Assert.Equal(2, absorbed);
        var final = await _chats.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(
            new[] { "goal", "step 1 reply", "what about the other folder?", "live reply", "step 2 reply" },
            final!.Messages.Select(m => m.Content).ToArray());
    }

    [Fact]
    public async Task SaveMergedAsync_WithNothingToAbsorb_WritesExactlyTheCallersRows()
    {
        // The ordinary case: no other writer touched the chat, so the merge is a plain replace and reports 0.
        var id = Guid.NewGuid();
        var chat = Chat(id, "c", "m0", "m1");
        Assert.Equal(0, await _chats.SaveMergedAsync(chat, TestContext.Current.CancellationToken));

        chat.Messages.Add(new SyncAssistantChatMessage
        {
            Id = Guid.NewGuid(), Role = "assistant", Content = "m2", Timestamp = DateTime.UtcNow,
        });
        Assert.Equal(0, await _chats.SaveMergedAsync(chat, TestContext.Current.CancellationToken));

        var final = await _chats.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(["m0", "m1", "m2"], final!.Messages.Select(m => m.Content).ToArray());
    }

    [Fact]
    public async Task SaveMergedAsync_DoesNotMutateTheCallersSnapshot()
    {
        // The executor reuses its own list to build the next snapshot, so the merge must stay inside the
        // store: absorbing a foreign row into the caller's list would leak it into the run's next payload
        // (and, if it ever reached _messages, into the model context — executor parity).
        var id = Guid.NewGuid();
        var stored = Chat(id, "c", "goal", "typed mid-run");
        await _chats.SaveAsync(stored, TestContext.Current.CancellationToken);

        var runView = Chat(id, "c");
        runView.Messages = [stored.Messages[0]];
        await _chats.SaveMergedAsync(runView, TestContext.Current.CancellationToken);

        Assert.Single(runView.Messages);
    }

    public void Dispose()
    {
        // Both stores own a DEDICATED connection to the file under _dir, so both must be closed before the
        // directory is deleted or Windows keeps the temp file locked.
        _chats.Dispose();
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}

using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The real services on one temp SQLite file: an interface-substituting double never touches SQLite.</summary>
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
        // Two runs writing at once is the shipped cadence, not a synthetic scenario: a chat write lands per
        // completed step on pool threads and the headless slot cap is 2.
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
        // There is no seam INSIDE the store's transaction, so the window is widened as far as the public API
        // allows — 400 inserts in one transaction — and the reader starts as soon as the writer is running.
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
        // The chat store's handle is private, but the services on the shared connection must see its commits.
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
        // Moving chat writes to a second connection converts an intra-connection InvalidOperationException into
        // a cross-connection SQLITE_BUSY; WAL + busy_timeout is what keeps that from being "database is locked".
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
        // thread, and one that awaits a chat read would deadlock against a non-reentrant gate.
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
        // Two service gates must never be held at once: the run lookup inside EvictOlderThanAsync's filter
        // happens with the chat gate released, so a concurrent chat read cannot deadlock against it.
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
        // Whatever the order, a title write must not delete the messages the full replace wrote.
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

    private static readonly DateTime T0 = new(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc);

    private static SyncAssistantChatMessage Row(string content, int minute, string role = "assistant") => new()
    {
        Id = Guid.NewGuid(),
        Role = role,
        Content = content,
        Timestamp = T0.AddMinutes(minute),
    };

    private static SyncAssistantChat Payload(Guid id, string title, params SyncAssistantChatMessage[] rows)
    {
        var chat = Chat(id, title);
        chat.Messages = [.. rows];
        return chat;
    }

    private async Task AssertOrderedAndContiguousAsync(Guid chatId)
    {
        var final = await _chats.GetAsync(chatId, TestContext.Current.CancellationToken);
        var stamps = final!.Messages.Select(m => m.Timestamp).ToList();
        Assert.Equal(stamps.OrderBy(t => t), stamps);

        using var connection = new SqliteConnection(_ctx.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Ordinal FROM AssistantChatMessages WHERE ChatId = @c ORDER BY Ordinal";
        command.Parameters.AddWithValue("@c", chatId.ToString());
        var ordinals = new List<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            ordinals.Add(reader.GetInt32(0));
        Assert.Equal(Enumerable.Range(0, ordinals.Count), ordinals);
    }

    [Theory]
    [InlineData("LLJJ")]
    [InlineData("LJLJ")]
    [InlineData("LJJL")]
    [InlineData("JLLJ")]
    [InlineData("JLJL")]
    [InlineData("JJLL")]
    public async Task ALiveTurnAndAJobSavingOneChat_InAnyInterleaving_LoseNothingDuplicateNothingKeepOrder(string order)
    {
        // Live: insert the question with an empty answer, then fill the answer in. Job: step 1, then steps 1+2,
        // the way the executor re-sends every row it authored.
        var id = Guid.NewGuid();
        var goal = Row("goal", 0, "user");
        await _chats.SaveAsync(Payload(id, "c", goal), TestContext.Current.CancellationToken);

        var step1 = Row("step 1", 1);
        var question = Row("typed mid-run", 2, "user");
        var answer = Row("", 3);
        var step2 = Row("step 2", 4);
        var filledAnswer = new SyncAssistantChatMessage
        {
            Id = answer.Id, Role = answer.Role, Content = "live answer", Timestamp = answer.Timestamp,
        };

        var live = new Queue<SyncAssistantChat>([Payload(id, "live", question, answer), Payload(id, "live", filledAnswer)]);
        var job = new Queue<SyncAssistantChat>([Payload(id, "job", step1), Payload(id, "job", step1, step2)]);
        foreach (var writer in order)
        {
            var next = writer == 'L' ? live.Dequeue() : job.Dequeue();
            Assert.True(await _chats.UpsertMessagesAsync(next, writeHeader: false, TestContext.Current.CancellationToken));
        }

        var final = await _chats.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(
            new[] { "goal", "step 1", "typed mid-run", "live answer", "step 2" },
            final!.Messages.Select(m => m.Content).ToArray());
        Assert.Equal(5, final.Messages.Select(m => m.Id).Distinct().Count());
        Assert.Equal("c", final.Title);
        await AssertOrderedAndContiguousAsync(id);
    }

    [Fact]
    public async Task TwoWritersUpsertingConcurrently_LoseNoRow()
    {
        var id = Guid.NewGuid();
        var seed = Chat(id, "shared", "m0");
        await _chats.SaveAsync(seed, TestContext.Current.CancellationToken);

        var errors = new ConcurrentBag<Exception>();
        var expected = new ConcurrentBag<Guid>(seed.Messages.Select(m => m.Id));

        async Task Writer(string tag)
        {
            var mine = new List<SyncAssistantChatMessage>();
            for (var i = 0; i < 40; i++)
            {
                var row = new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(), Role = "assistant", Content = $"{tag}-{i}", Timestamp = DateTime.UtcNow,
                };
                mine.Add(row);
                expected.Add(row.Id);
                try { await _chats.UpsertMessagesAsync(Payload(id, tag, [.. mine]), writeHeader: false); }
                catch (Exception ex) { errors.Add(ex); }
            }
        }

        await Task.WhenAll(
            Task.Run(() => Writer("a"), TestContext.Current.CancellationToken),
            Task.Run(() => Writer("b"), TestContext.Current.CancellationToken));

        Assert.Empty(errors);
        var final = await _chats.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(expected.ToHashSet(), final!.Messages.Select(m => m.Id).ToHashSet());
        Assert.Equal(81, final.Messages.Count);
        await AssertOrderedAndContiguousAsync(id);
    }

    [Fact]
    public async Task Upsert_LeavesRowsOutsideItsPayloadUntouched()
    {
        var id = Guid.NewGuid();
        var mine = Row("mine", 0, "user");
        var theirs = Row("theirs", 1);
        await _chats.SaveAsync(Payload(id, "c", mine, theirs), TestContext.Current.CancellationToken);

        var edited = Row("mine, edited", 0, "user");
        edited.Id = mine.Id;
        await _chats.UpsertMessagesAsync(Payload(id, "c", edited), writeHeader: false, TestContext.Current.CancellationToken);

        var final = await _chats.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(["mine, edited", "theirs"], final!.Messages.Select(m => m.Content).ToArray());
    }

    [Fact]
    public async Task DeleteMessages_RemovesOnlyTheNamedRowsOfThatChat_AndRefreshesSearch()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var keep = Row("keepword", 0, "user");
        var drop = Row("dropword", 1);
        var other = Row("elsewhere", 0, "user");
        await _chats.SaveAsync(Payload(a, "a", keep, drop), TestContext.Current.CancellationToken);
        await _chats.SaveAsync(Payload(b, "b", other), TestContext.Current.CancellationToken);

        Assert.Equal(0, await _chats.DeleteMessagesAsync(b, [drop.Id], TestContext.Current.CancellationToken));
        Assert.Equal(1, await _chats.DeleteMessagesAsync(a, [drop.Id, other.Id], TestContext.Current.CancellationToken));

        var finalA = await _chats.GetAsync(a, TestContext.Current.CancellationToken);
        Assert.Equal([keep.Id], finalA!.Messages.Select(m => m.Id).ToArray());
        Assert.Single((await _chats.GetAsync(b, TestContext.Current.CancellationToken))!.Messages);
        Assert.Empty(await _chats.SearchAsync("dropword", ct: TestContext.Current.CancellationToken));
        Assert.Single(await _chats.SearchAsync("keepword", ct: TestContext.Current.CancellationToken));
        await AssertOrderedAndContiguousAsync(a);
    }

    [Fact]
    public async Task UpsertWithoutHeader_KeepsTitleProviderAndStar()
    {
        var id = Guid.NewGuid();
        var provider = Guid.NewGuid();
        var seed = Payload(id, "original", Row("m0", 0, "user"));
        seed.ProviderId = provider;
        seed.WorkingDirectory = "sub";
        await _chats.SaveAsync(seed, TestContext.Current.CancellationToken);
        await _chats.SetTitleAsync(id, "renamed", TestContext.Current.CancellationToken);
        await _chats.SetFavoriteAsync(id, true, TestContext.Current.CancellationToken);

        var stale = Payload(id, "stale title", Row("m1", 1));
        stale.ProviderId = Guid.NewGuid();
        await _chats.UpsertMessagesAsync(stale, writeHeader: false, TestContext.Current.CancellationToken);

        var final = await _chats.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("renamed", final!.Title);
        Assert.Equal(provider, final.ProviderId);
        Assert.Equal("sub", final.WorkingDirectory);
        Assert.True(final.IsFavorite);
        Assert.Equal(2, final.Messages.Count);
        Assert.Single(await _chats.SearchAsync("renamed", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpsertWithHeader_WritesTheHeaderButNotTheStar()
    {
        var id = Guid.NewGuid();
        await _chats.SaveAsync(Payload(id, "original", Row("m0", 0, "user")), TestContext.Current.CancellationToken);
        await _chats.SetFavoriteAsync(id, true, TestContext.Current.CancellationToken);

        await _chats.UpsertMessagesAsync(Payload(id, "changed"), writeHeader: true, TestContext.Current.CancellationToken);

        var final = await _chats.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("changed", final!.Title);
        Assert.True(final.IsFavorite);
        Assert.Single(final.Messages);
    }

    [Fact]
    public async Task UpsertWithoutHeader_OnAMissingChat_WritesNothing()
    {
        var id = Guid.NewGuid();
        Assert.False(await _chats.UpsertMessagesAsync(Payload(id, "c", Row("m0", 0)), writeHeader: false,
            TestContext.Current.CancellationToken));
        Assert.Null(await _chats.GetAsync(id, TestContext.Current.CancellationToken));

        Assert.True(await _chats.UpsertMessagesAsync(Payload(id, "c", Row("m0", 0)), writeHeader: true,
            TestContext.Current.CancellationToken));
        Assert.Single((await _chats.GetAsync(id, TestContext.Current.CancellationToken))!.Messages);
    }

    [Fact]
    public async Task Upsert_OrdersByTimestamp_EvenWhenARowArrivesLateOrWithALocalOffset()
    {
        var id = Guid.NewGuid();
        await _chats.SaveAsync(Payload(id, "c", Row("first", 0, "user"), Row("third", 2)), TestContext.Current.CancellationToken);

        var late = Row("second", 1, "user");
        late.Timestamp = late.Timestamp.ToLocalTime();
        await _chats.UpsertMessagesAsync(Payload(id, "c", late), writeHeader: false, TestContext.Current.CancellationToken);

        var final = await _chats.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(["first", "second", "third"], final!.Messages.Select(m => m.Content).ToArray());
        await AssertOrderedAndContiguousAsync(id);
    }

    [Fact]
    public async Task Upsert_DoesNotRewriteARowOfAnotherChatWithTheSameId()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var row = Row("in a", 0, "user");
        await _chats.SaveAsync(Payload(a, "a", row), TestContext.Current.CancellationToken);
        await _chats.SaveAsync(Payload(b, "b", Row("in b", 0, "user")), TestContext.Current.CancellationToken);

        var hijack = Row("hijack", 1);
        hijack.Id = row.Id;
        await _chats.UpsertMessagesAsync(Payload(b, "b", hijack), writeHeader: false, TestContext.Current.CancellationToken);

        Assert.Equal("in a", (await _chats.GetAsync(a, TestContext.Current.CancellationToken))!.Messages.Single().Content);
    }

    /// <summary>CommandTimeout is one second because the driver retries SQLITE_BUSY for its full 30s default.</summary>
    private static async Task ExecAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 1;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheProvidersTransactions_TakeTheWriteLockAtBegin_NotAtTheirFirstWrite()
    {
        // A premise pin, not a bug reproduction: the store's read-then-write transaction is safe under WAL only
        // while BEGIN IMMEDIATE stays the driver's default, which it never promised the store.
        await _chats.SaveAsync(Chat(Guid.NewGuid(), "seed", "body"), TestContext.Current.CancellationToken);

        using var holder = new SqliteConnection(_ctx.ConnectionString);
        using var probe = new SqliteConnection(_ctx.ConnectionString);
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await probe.OpenAsync(TestContext.Current.CancellationToken);
        await ExecAsync(probe, "PRAGMA busy_timeout=100;");
        await ExecAsync(probe, "CREATE TABLE IF NOT EXISTS BusySnapshotProbe (V TEXT)");

        // Half 1: the default overload holds the write lock from BEGIN with NO statement executed yet, so a
        // foreign write is refused. A deferred BEGIN would let it straight through.
        using (holder.BeginTransaction())
        {
            var refused = await Assert.ThrowsAsync<SqliteException>(
                () => ExecAsync(probe, "INSERT INTO BusySnapshotProbe VALUES ('blocked')"));
            Assert.Equal(5, refused.SqliteErrorCode);   // SQLITE_BUSY: the writer lock is already taken
        }

        // Half 2: what the read-first shape WOULD hit if that BEGIN ever became deferred. Do NOT route this
        // through AssistantChatService — it has no seam inside its transaction and the assertion would vanish.
        using (var deferred = holder.BeginTransaction(deferred: true))
        {
            using (var select = holder.CreateCommand())
            {
                select.CommandText = "SELECT Id FROM AssistantChats";
                select.Transaction = deferred;
                using var reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                }
            }

            // A foreign commit lands INSIDE the read snapshot's window — permitted, because a deferred
            // transaction that has only read holds no write lock.
            await ExecAsync(probe, "INSERT INTO BusySnapshotProbe VALUES ('committed')");

            var stale = await Assert.ThrowsAsync<SqliteException>(
                () => ExecAsync(holder, "DELETE FROM AssistantChats", deferred));
            Assert.Equal(5, stale.SqliteErrorCode);
            Assert.Equal(517, stale.SqliteExtendedErrorCode);   // SQLITE_BUSY_SNAPSHOT — busy_timeout cannot help
            deferred.Rollback();
        }
    }

    [Fact]
    public async Task DeleteAllAsync_WithAnotherConnectionCommittingThroughout_Completes()
    {
        // A forward guard, not a reproduction: it passes today because BeginTransaction() is already BEGIN
        // IMMEDIATE, and the deterministic guard for the deferred regression is the write-lock fact above.
        using var writer = new SqliteConnection(_ctx.ConnectionString);
        await writer.OpenAsync(TestContext.Current.CancellationToken);
        await ExecAsync(writer, "PRAGMA busy_timeout=100;");
        await ExecAsync(writer, "CREATE TABLE IF NOT EXISTS BusySnapshotProbe (V TEXT)");

        // The writer runs one AWAITED attempt per released permit rather than a free-running loop: a loop's
        // commit count is a scheduling outcome, and a saturated pool can withhold the task for the whole test,
        // so the precondition failed instead of the behaviour.
        const int rounds = 5;
        using var stopWriting = new CancellationTokenSource();
        using var attemptRequested = new SemaphoreSlim(0);
        using var attemptFinished = new SemaphoreSlim(0);
        var committed = 0;
        var refused = 0;
        Exception? writerFault = null;
        var committer = Task.Run(async () =>
        {
            while (true)
            {
                try { await attemptRequested.WaitAsync(stopWriting.Token); }
                catch (OperationCanceledException) { return; }

                // The delete transaction holds the write lock from BEGIN, so a refusal here is contention, not
                // a failure. Either outcome proves the foreign connection was live; only the delete is asserted.
                try
                {
                    await ExecAsync(writer, "INSERT INTO BusySnapshotProbe VALUES ('w')");
                    committed++;
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 5) { refused++; }
                catch (Exception ex) { writerFault ??= ex; }
                finally { attemptFinished.Release(); }
            }
        }, TestContext.Current.CancellationToken);

        // Nothing holds the write lock yet, so this first attempt MUST commit — the precondition "a foreign
        // connection really writes this file" is established here rather than counted at the end.
        attemptRequested.Release();
        await attemptFinished.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, committed);

        var errors = new List<Exception>();
        for (var round = 0; round < rounds; round++)
        {
            // Seeds and delete are caught separately so that a throw cannot skip the permit below and leave the
            // round waiting on an attempt that was never requested.
            try
            {
                for (var i = 0; i < 3; i++)
                    await _chats.SaveAsync(Chat(Guid.NewGuid(), $"round {round} chat {i}", "body"),
                        TestContext.Current.CancellationToken);
            }
            catch (Exception ex) { errors.Add(ex); }

            attemptRequested.Release();
            try
            {
                await _chats.DeleteAllAsync(TestContext.Current.CancellationToken);
            }
            catch (Exception ex) { errors.Add(ex); }

            await attemptFinished.WaitAsync(TestContext.Current.CancellationToken);
        }

        await stopWriting.CancelAsync();
        await committer;

        Assert.Null(writerFault);
        Assert.Empty(errors);
        Assert.Equal(rounds + 1, committed + refused);   // every requested attempt was awaited and accounted for
        Assert.Empty(await _chats.GetAllIdsAsync(TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        // Both stores own a DEDICATED connection to the file under _dir, so both must be closed before the
        // directory is deleted or Windows keeps the temp file locked.
        _chats.Dispose();
        _runs.Dispose();
        _ctx.Dispose();
        TempPath.Remove(_dir);
    }
}

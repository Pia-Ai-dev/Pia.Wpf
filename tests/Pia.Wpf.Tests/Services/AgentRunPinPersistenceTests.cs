using System.IO;
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
/// The pin columns on <c>AgentRuns</c> — the two values and the marker saying they were recorded: the round
/// trip, the ordinal MapRun reads them at, and the <c>ALTER TABLE</c> half of their migration. Every other test
/// and every fresh profile takes the <c>CREATE TABLE</c> path, so nothing else would notice a missing ALTER.
/// </summary>
public sealed class AgentRunPinPersistenceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir;
    private readonly string _dbPath;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;

    public AgentRunPinPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaRunPins_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "history.db");
        _ctx = new SqliteContext(_dbPath);
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
    }

    public void Dispose()
    {
        _runs.Dispose();
        _ctx.Dispose();
        SqlitePool.ClearFor($"Data Source={_dbPath}");
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task CreateAsync_PersistsBothPins_AndTheInMemoryRunCarriesThem()
    {
        var chatId = await MakeChatAsync();
        var personaId = Guid.NewGuid();

        var created = await _runs.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, AgentRunTrigger.Schedule,
            PersonaId: personaId, ReasoningEffort: ReasoningEffort.XHigh), Ct);

        // The in-memory object is the one a fresh launch hands the orchestrator; the row is never re-read first.
        Assert.Equal(personaId, created.PersonaId);
        Assert.Equal(ReasoningEffort.XHigh, created.ReasoningEffort);

        var fetched = await _runs.GetAsync(created.Id, Ct);
        Assert.Equal(personaId, fetched!.PersonaId);
        Assert.Equal(ReasoningEffort.XHigh, fetched.ReasoningEffort);
    }

    [Fact]
    public async Task GetByChatAsync_ReadsBothPinsAtTheSameOrdinals()
    {
        // A second reader over the same column list: appending a column without extending MapRun would show up
        // here as well as in GetAsync, which is the point of asserting through both.
        var chatId = await MakeChatAsync();
        var personaId = Guid.NewGuid();
        await _runs.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, AgentRunTrigger.Schedule,
            PersonaId: personaId, ReasoningEffort: ReasoningEffort.None), Ct);

        var byChat = Assert.Single(await _runs.GetByChatAsync(chatId, Ct));

        Assert.Equal(personaId, byChat.PersonaId);
        // None is a real pinnable value ("no reasoning"), not a stand-in for unset, so it has to survive.
        Assert.Equal(ReasoningEffort.None, byChat.ReasoningEffort);
    }

    [Fact]
    public async Task CreateAsync_WithoutPins_ReadsBackNull()
    {
        var chatId = await MakeChatAsync();

        var created = await _runs.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, AgentRunTrigger.User), Ct);
        var fetched = await _runs.GetAsync(created.Id, Ct);

        Assert.Null(fetched!.PersonaId);
        Assert.Null(fetched.ReasoningEffort);
        // The caller resolved no effort, so its null must NOT read as "resolved to nothing".
        Assert.False(fetched.EffortPinRecorded);
    }

    [Fact]
    public async Task ARecordedNullEffort_SurvivesTheRoundTrip_AsRecordedRatherThanUnset()
    {
        var chatId = await MakeChatAsync();

        var created = await _runs.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, AgentRunTrigger.Schedule,
            PersonaId: Guid.NewGuid(), ReasoningEffort: null, EffortPinRecorded: true), Ct);

        Assert.True(created.EffortPinRecorded);
        // Both readers: appending the column without extending MapRun would show up in each.
        Assert.True((await _runs.GetAsync(created.Id, Ct))!.EffortPinRecorded);
        var byChat = Assert.Single(await _runs.GetByChatAsync(chatId, Ct));
        Assert.True(byChat.EffortPinRecorded);
        Assert.Null(byChat.ReasoningEffort);
    }

    [Theory]
    [InlineData("Blazing")]   // a member a future build might add
    [InlineData("99")]        // TryParse accepts a bare ordinal; only an OUT-OF-RANGE one is rejected
    [InlineData("-1")]
    [InlineData("")]
    public async Task AnEffortNameThisBuildDoesNotKnow_ReadsBackAsUnset(string raw)
    {
        var chatId = await MakeChatAsync();
        var created = await _runs.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, AgentRunTrigger.Schedule, ReasoningEffort: ReasoningEffort.High), Ct);

        using (var write = _ctx.GetConnection().CreateCommand())
        {
            write.CommandText = "UPDATE AgentRuns SET ReasoningEffort=@E WHERE Id=@Id";
            write.Parameters.AddWithValue("@E", raw);
            write.Parameters.AddWithValue("@Id", created.Id.ToString());
            write.ExecuteNonQuery();
        }

        var fetched = await _runs.GetAsync(created.Id, Ct);

        Assert.NotNull(fetched);
        Assert.Null(fetched!.ReasoningEffort);
    }

    [Fact]
    public async Task AnExistingDatabase_GainsEveryPinColumn_AndKeepsItsRuns()
    {
        // Its own file, since the database has to outlive the first "launch" for the second to migrate it.
        var dir = Path.Combine(Path.GetTempPath(), "PiaRunPinMigrate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "history.db");
        try
        {
            Guid runId;
            using (var ctx = new SqliteContext(dbPath))
            using (var runs = new AgentRunService(ctx, NullLogger<AgentRunService>.Instance))
            {
                var chats = new AssistantChatService(ctx, runs);
                var chatId = Guid.NewGuid();
                var now = DateTime.UtcNow;
                await chats.SaveAsync(new SyncAssistantChat
                {
                    Id = chatId,
                    SchemaVersion = 1,
                    Title = "t",
                    CreatedAt = now,
                    UpdatedAt = now,
                    LastAccessedAt = now,
                    WindowMode = WindowMode.Assistant.ToString(),
                    Messages = [],
                }, Ct);
                runId = (await runs.CreateAsync(new AgentRunCreateRequest(
                    chatId, RunShape.Planned, AgentRunTrigger.Schedule, Goal: "pre-migration"), Ct)).Id;

                // DROP rather than a pasted old CREATE TABLE: defined against whatever this build creates.
                foreach (var column in new[] { "PersonaId", "ReasoningEffort", "EffortPinRecorded" })
                {
                    using var drop = ctx.GetConnection().CreateCommand();
                    drop.CommandText = $"ALTER TABLE AgentRuns DROP COLUMN {column}";
                    drop.ExecuteNonQuery();
                }
            }
            SqlitePool.ClearFor($"Data Source={dbPath}");

            using var reopened = new SqliteContext(dbPath);
            var columns = new List<string>();
            using (var pragma = reopened.GetConnection().CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(AgentRuns)";
                using var r = pragma.ExecuteReader();
                while (r.Read()) columns.Add(r.GetString(1));
            }
            // CREATE TABLE IF NOT EXISTS is a no-op on an existing table, so only the ALTER pass can have added
            // these — and both, not just the first.
            Assert.Contains("PersonaId", columns);
            Assert.Contains("ReasoningEffort", columns);
            Assert.Contains("EffortPinRecorded", columns);

            using var migratedRuns = new AgentRunService(reopened, NullLogger<AgentRunService>.Instance);
            var migrated = await migratedRuns.GetAsync(runId, Ct);
            Assert.NotNull(migrated);
            Assert.Equal("pre-migration", migrated!.Goal);
            // A row that predates the columns resumes on the per-mode persona, exactly as it did before them.
            Assert.Null(migrated.PersonaId);
            Assert.Null(migrated.ReasoningEffort);
            // The ADD COLUMN's constant default, which is the whole reason a legacy row keeps falling through
            // instead of freezing on an effort it never resolved.
            Assert.False(migrated.EffortPinRecorded);

            // Idempotent: a THIRD launch must not re-issue the ALTER. SQLite errors on a duplicate column name
            // and MigrateSchema has no try/catch, so an unguarded ALTER takes startup down on every later open.
            migratedRuns.Dispose();
            reopened.Dispose();
            SqlitePool.ClearFor($"Data Source={dbPath}");
            using var third = new SqliteContext(dbPath);
            Assert.NotNull(third.GetConnection());
        }
        finally
        {
            SqlitePool.ClearFor($"Data Source={dbPath}");
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    private async Task<Guid> MakeChatAsync()
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
        }, Ct);
        return chatId;
    }
}

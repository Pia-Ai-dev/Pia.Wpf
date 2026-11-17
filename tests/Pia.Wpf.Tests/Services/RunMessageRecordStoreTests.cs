using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

public sealed class RunMessageRecordStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly RunMessageRecordStore _sut;

    public RunMessageRecordStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        _sut = new RunMessageRecordStore(_ctx, NullLogger<RunMessageRecordStore>.Instance);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _chats.Dispose();
        _runs.Dispose();
        _ctx.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Guid ChatId, Guid RunId, Guid MessageId)> SeedAsync(
        DateTime? accessed = null, RunShape shape = RunShape.Planned)
    {
        var chatId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var at = accessed ?? DateTime.UtcNow;
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = chatId,
            SchemaVersion = 1,
            Title = "t",
            CreatedAt = at,
            UpdatedAt = at,
            LastAccessedAt = at,
            WindowMode = "Assistant",
            Messages = [new SyncAssistantChatMessage { Id = messageId, Role = "assistant", Content = "reply", Timestamp = at }],
        }, Ct);
        var run = await _runs.CreateAsync(new AgentRunCreateRequest(chatId, shape, AgentRunTrigger.User), Ct);
        await _sut.RecordAsync(new RunMessageRecord(messageId, run.Id, Guid.NewGuid(), 1, DateTime.UtcNow), Ct);
        return (chatId, run.Id, messageId);
    }

    [Fact]
    public async Task ARecord_IsReadBackByItsChat_AndRaised()
    {
        RunMessageRecord? raised = null;
        _sut.Recorded += (_, r) => raised = r;

        var (chatId, runId, messageId) = await SeedAsync();

        var record = Assert.Single(await _sut.GetByChatAsync(chatId, Ct));
        Assert.Equal(messageId, record.MessageId);
        Assert.Equal(runId, record.RunId);
        Assert.Equal(1, record.StepOrdinal);
        Assert.Equal(messageId, raised?.MessageId);
        Assert.Empty(await _sut.GetByChatAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task DeletingTheMessage_DeletesItsRecord()
    {
        var (chatId, _, messageId) = await SeedAsync();

        await _chats.DeleteMessagesAsync(chatId, [messageId], Ct);

        Assert.Empty(await _sut.GetByChatAsync(chatId, Ct));
        Assert.Equal(0, CountRows());
    }

    [Fact]
    public async Task DeletingTheMessageUnderAnotherChat_KeepsItsRecord()
    {
        var (chatId, _, messageId) = await SeedAsync();
        var (otherChat, _, _) = await SeedAsync();

        Assert.Equal(0, await _chats.DeleteMessagesAsync(otherChat, [messageId], Ct));

        Assert.Single(await _sut.GetByChatAsync(chatId, Ct));
    }

    [Fact]
    public async Task DeletingTheChat_DeletesItsRecords()
    {
        var (chatId, _, _) = await SeedAsync();

        await _chats.DeleteAsync(chatId, Ct);

        Assert.Equal(0, CountRows());
    }

    [Fact]
    public async Task DeletingAllChats_DeletesEveryRecord()
    {
        await SeedAsync();
        await SeedAsync();

        await _chats.DeleteAllAsync(Ct);

        Assert.Equal(0, CountRows());
    }

    [Fact]
    public async Task EvictingAChat_DeletesItsRecords()
    {
        // Eviction keeps chats with a planned run, so this one carries a single-turn run.
        await SeedAsync(DateTime.UtcNow.AddDays(-400), RunShape.SingleTurn);

        await _chats.EvictOlderThanAsync(DateTime.UtcNow.AddDays(-30), Ct);

        Assert.Equal(0, CountRows());
    }

    [Fact]
    public async Task AFullChatSave_KeepsTheRecords()
    {
        var (chatId, _, _) = await SeedAsync();
        var chat = (await _chats.GetAsync(chatId, Ct))!;

        await _chats.SaveAsync(chat, Ct);

        Assert.Single(await _sut.GetByChatAsync(chatId, Ct));
    }

    [Fact]
    public async Task ARecordForAMissingRun_IsSwallowed()
    {
        await _sut.RecordAsync(new RunMessageRecord(Guid.NewGuid(), Guid.NewGuid(), null, null, DateTime.UtcNow), Ct);

        Assert.Equal(0, CountRows());
    }

    private long CountRows()
    {
        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AgentRunMessages";
        return (long)cmd.ExecuteScalar()!;
    }
}

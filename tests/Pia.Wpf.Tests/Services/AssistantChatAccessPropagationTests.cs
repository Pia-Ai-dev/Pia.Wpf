using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Services;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Retention deletes globally — an evicted chat is deleted from the server and that tombstone reaches every
/// device — so a chat this device still reads has to keep the server's access date alive, or another device
/// evicts it out from under the reader.
/// </summary>
public class AssistantChatAccessPropagationTests : IDisposable
{
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _service;
    private readonly string _tmpDir;

    public AssistantChatAccessPropagationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _service = new AssistantChatService(_ctx, _runs);
    }

    [Fact]
    public async Task TouchLastAccessed_WhenTheStoredDateIsFromAnEarlierDay_RaisesChatAccessed()
    {
        var chat = await StoreChatAsync(lastAccessed: DateTime.UtcNow.AddDays(-400));
        var accessed = new List<Guid>();
        _service.ChatAccessed += (_, id) => accessed.Add(id);

        await _service.TouchLastAccessedAsync(chat.Id, TestContext.Current.CancellationToken);

        Assert.Equal([chat.Id], accessed);
    }

    // Day granularity is what the wire carries, so a second open on the same day would push an identical
    // document — and the heavy chats in an imported archive are megabytes each.
    [Fact]
    public async Task TouchLastAccessed_WhenAlreadyAccessedToday_RaisesNothing()
    {
        var chat = await StoreChatAsync(lastAccessed: DateTime.UtcNow.AddMinutes(-5));
        var accessed = new List<Guid>();
        _service.ChatAccessed += (_, id) => accessed.Add(id);

        await _service.TouchLastAccessedAsync(chat.Id, TestContext.Current.CancellationToken);

        Assert.Empty(accessed);
    }

    [Fact]
    public async Task TouchLastAccessed_ForAChatThatIsNotStored_RaisesNothing()
    {
        var accessed = new List<Guid>();
        _service.ChatAccessed += (_, id) => accessed.Add(id);

        await _service.TouchLastAccessedAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Empty(accessed);
    }

    // The day gate decides whether to SYNC, never whether to write: retention compares the stored value
    // against a cutoff, and the history list orders by it.
    [Fact]
    public async Task TouchLastAccessed_OnASameDayOpen_StillAdvancesTheStoredStamp()
    {
        var earlier = DateTime.UtcNow.AddMinutes(-30);
        var chat = await StoreChatAsync(lastAccessed: earlier);

        await _service.TouchLastAccessedAsync(chat.Id, TestContext.Current.CancellationToken);

        Assert.True(await ReadLastAccessedAsync(chat.Id) > earlier);
    }

    [Fact]
    public async Task TouchLastAccessed_DoesNotRaiseChatsChanged()
    {
        // ChatsChanged means "the content changed": every subscriber reloads on one, and the history list
        // would rebuild itself on every chat the user opens.
        var chat = await StoreChatAsync(lastAccessed: DateTime.UtcNow.AddDays(-400));
        var changed = 0;
        _service.ChatsChanged += (_, _) => changed++;

        await _service.TouchLastAccessedAsync(chat.Id, TestContext.Current.CancellationToken);

        Assert.Equal(0, changed);
    }

    private async Task<SyncAssistantChat> StoreChatAsync(DateTime lastAccessed)
    {
        var chat = new SyncAssistantChat
        {
            Id = Guid.NewGuid(),
            SchemaVersion = 1,
            Title = "Archived conversation",
            CreatedAt = lastAccessed,
            UpdatedAt = lastAccessed,
            LastAccessedAt = lastAccessed,
            WindowMode = "Assistant",
            Messages =
            [
                new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "user",
                    Content = "still worth keeping",
                    Timestamp = lastAccessed,
                }
            ],
        };

        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        return chat;
    }

    private async Task<DateTime> ReadLastAccessedAsync(Guid id)
    {
        var connection = _ctx.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT LastAccessedAt FROM AssistantChats WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        var value = (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        return DateTime.Parse(value).ToUniversalTime();
    }

    public void Dispose()
    {
        _service.Dispose();
        _runs.Dispose();
        _ctx.Dispose();
        TempPath.Remove(_tmpDir);
    }
}

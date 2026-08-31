using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Services;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The protected-route flag must outlive the turn that produced it: it drives the shield in the answer
/// footer, which was in-memory only until it got its own column.
/// </summary>
public class AssistantChatProtectedRouteTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _dbPath;
    private SqliteContext _ctx;
    private AgentRunService _runs;
    private AssistantChatService _service;

    public AssistantChatProtectedRouteTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _dbPath = Path.Combine(_tmpDir, "history.db");
        (_ctx, _runs, _service) = OpenStack();
    }

    private (SqliteContext, AgentRunService, AssistantChatService) OpenStack()
    {
        var ctx = new SqliteContext(_dbPath);
        var runs = new AgentRunService(ctx, NullLogger<AgentRunService>.Instance);
        return (ctx, runs, new AssistantChatService(ctx, runs));
    }

    private void CloseStack()
    {
        _service.Dispose();
        _runs.Dispose();
        _ctx.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveAsync_RoundTripsIsProtectedRoute(bool isProtected)
    {
        var chat = MakeChat(isProtected);
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);

        var reloaded = await _service.GetAsync(chat.Id, TestContext.Current.CancellationToken);

        var assistant = Assert.Single(reloaded!.Messages, m => m.Role == "assistant");
        Assert.Equal(isProtected, assistant.IsProtectedRoute);
    }

    [Fact]
    public async Task SaveAsync_LeavesTheFlagOffUserMessages()
    {
        var chat = MakeChat(isProtected: true);
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);

        var reloaded = await _service.GetAsync(chat.Id, TestContext.Current.CancellationToken);

        var user = Assert.Single(reloaded!.Messages, m => m.Role == "user");
        Assert.False(user.IsProtectedRoute);
    }

    [Fact]
    public async Task ADatabaseWithoutTheColumn_IsMigratedAndReadsAsNotProtected()
    {
        // Simulates a profile written before the column existed: drop it, reopen, and the schema pass must
        // add it back rather than leaving every read to throw on a missing ordinal.
        var chat = MakeChat(isProtected: true);
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);

        using (var drop = _ctx.GetConnection().CreateCommand())
        {
            drop.CommandText = "ALTER TABLE AssistantChatMessages DROP COLUMN IsProtectedRoute";
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        CloseStack();

        (_ctx, _runs, _service) = OpenStack();

        var reloaded = await _service.GetAsync(chat.Id, TestContext.Current.CancellationToken);
        var assistant = Assert.Single(reloaded!.Messages, m => m.Role == "assistant");
        Assert.False(assistant.IsProtectedRoute);
    }

    private static SyncAssistantChat MakeChat(bool isProtected)
    {
        var now = DateTime.UtcNow;
        return new SyncAssistantChat
        {
            Id = Guid.NewGuid(),
            SchemaVersion = 1,
            Title = "Protected route",
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = "Assistant",
            ProviderId = null,
            Messages =
            [
                new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "user",
                    Content = "sensitive question",
                    Timestamp = now,
                },
                new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "assistant",
                    Content = "guarded answer",
                    Timestamp = now,
                    ModelName = "Pia Cloud",
                    IsProtectedRoute = isProtected,
                },
            ],
        };
    }

    public void Dispose()
    {
        CloseStack();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }
}

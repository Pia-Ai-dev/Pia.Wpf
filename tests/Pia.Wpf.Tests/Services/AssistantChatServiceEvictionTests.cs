using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>A chat bearing a <see cref="RunShape.Planned"/> run outlives stale-chat eviction, so a resumable run is never deleted.</summary>
public sealed class AssistantChatServiceEvictionTests
{
    private static SyncAssistantChat Chat(Guid id, DateTime lastAccessed) => new()
    {
        Id = id,
        SchemaVersion = 1,
        Title = "t",
        CreatedAt = lastAccessed,
        UpdatedAt = lastAccessed,
        LastAccessedAt = lastAccessed,
        WindowMode = WindowMode.Assistant.ToString(),
        Messages = [],
    };

    [Fact]
    public async Task Evict_SkipsChatsWithPlannedRun()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var ctx = new SqliteContext(Path.Combine(dir, "history.db"));
        using var runs = new AgentRunService(ctx, NullLogger<AgentRunService>.Instance);
        var chats = new AssistantChatService(ctx, runs);

        var oldTime = DateTime.UtcNow.AddDays(-100);
        var plainId = Guid.NewGuid();
        var plannedChatId = Guid.NewGuid();
        await chats.SaveAsync(Chat(plainId, oldTime), TestContext.Current.CancellationToken);
        await chats.SaveAsync(Chat(plannedChatId, oldTime), TestContext.Current.CancellationToken);
        await runs.CreateAsync(new AgentRunCreateRequest(plannedChatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        // Cutoff in the future → both chats are old enough to evict.
        var evicted = await chats.EvictOlderThanAsync(DateTime.UtcNow, TestContext.Current.CancellationToken);

        Assert.Contains(plainId, evicted);
        Assert.DoesNotContain(plannedChatId, evicted);
        Assert.Null(await chats.GetAsync(plainId, TestContext.Current.CancellationToken));
        Assert.NotNull(await chats.GetAsync(plannedChatId, TestContext.Current.CancellationToken));

        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
}

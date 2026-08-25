using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The ranked search path: relevance order, an excerpt of the matching MESSAGE text, and the same
/// row filters and date bounds the recency path applies.
/// </summary>
public class AssistantChatServiceRankedSearchTests : IDisposable
{
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _service;
    private readonly string _tmpDir;

    public AssistantChatServiceRankedSearchTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _service = new AssistantChatService(_ctx, _runs);
    }

    [Fact]
    public async Task SearchRankedAsync_QuotesTheBody_NotTheTitle()
    {
        // The FTS table must carry content for the snippet to say anything, and the snippet column must
        // be Body. Asserting non-empty would not separate those: the search term is absent from the
        // title, so a snippet taken from the Title column is still non-empty.
        var ct = TestContext.Current.CancellationToken;
        var chat = MakeChat("AlphaTitleWord planning", "We settled on ZenithBodyWord for the box.");
        await _service.SaveAsync(chat, ct);

        var hits = await _service.SearchRankedAsync("zenithbodyword", null, null, null, null, 10, ct);

        var hit = Assert.Single(hits);
        Assert.Equal(chat.Id, hit.Id);
        Assert.NotEmpty(hit.Snippet);
        Assert.Contains("ZenithBodyWord", hit.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AlphaTitleWord", hit.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("AlphaTitleWord planning", hit.Title);
        Assert.Equal(1, hit.MessageCount);
    }

    [Fact]
    public async Task SearchRankedAsync_OrdersByRelevance_NotByRecency()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;

        // Older, but the term dominates a short transcript.
        var relevant = MakeChat(
            "Older chat",
            "Kubernetes Kubernetes Kubernetes Kubernetes Kubernetes",
            now.AddDays(-7));
        // Newer, and the term appears once in a long one.
        var recent = MakeChat(
            "Newer chat",
            "We mentioned Kubernetes once " + string.Join(' ', Enumerable.Repeat("filler", 200)),
            now);
        await _service.SaveAsync(relevant, ct);
        await _service.SaveAsync(recent, ct);

        var ranked = await _service.SearchRankedAsync("kubernetes", null, null, null, null, 10, ct);
        Assert.Equal(2, ranked.Count);
        Assert.Equal(relevant.Id, ranked[0].Id);

        // The recency path is what this is NOT: the same two chats, the opposite order.
        var recency = await _service.SearchAsync(searchText: "kubernetes", ct: ct);
        Assert.Equal(recent.Id, recency[0].Id);
    }

    [Fact]
    public async Task SearchRankedAsync_HonoursExcludeChatId()
    {
        var ct = TestContext.Current.CancellationToken;
        var current = MakeChat("Current", "Talking about PelicanWord today.");
        var other = MakeChat("Other", "We mentioned PelicanWord last week too.");
        await _service.SaveAsync(current, ct);
        await _service.SaveAsync(other, ct);

        var all = await _service.SearchRankedAsync("pelicanword", null, null, null, null, 10, ct);
        Assert.Equal(2, all.Count);

        var excluded = await _service.SearchRankedAsync("pelicanword", null, null, null, current.Id, 10, ct);
        Assert.Equal(other.Id, Assert.Single(excluded).Id);
    }

    [Fact]
    public async Task SearchRankedAsync_HidesMessageLessStubChats()
    {
        var ct = TestContext.Current.CancellationToken;
        var real = MakeChat("Real chat", "OrchidWord is in the transcript.");
        await _service.SaveAsync(real, ct);

        // What a failed/empty headless turn leaves behind: a titled row with no messages, whose FTS row
        // still matches on the title alone.
        var now = DateTime.UtcNow;
        await _service.SaveAsync(new SyncAssistantChat
        {
            Id = Guid.NewGuid(),
            SchemaVersion = 1,
            Title = "OrchidWord stub",
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = "Assistant",
            Messages = [],
        }, ct);

        var hits = await _service.SearchRankedAsync("orchidword", null, null, null, null, 10, ct);
        Assert.Equal(real.Id, Assert.Single(hits).Id);
    }

    [Fact]
    public async Task SearchRankedAsync_ExpandsToDateToEndOfDay_LikeSearchAsync()
    {
        // A caller picks between the two paths only by whether it passed a query, so an unexpanded
        // to-date here would answer the same request differently.
        var ct = TestContext.Current.CancellationToken;
        // 13:37, deliberately not midnight: a fixture written at 00:00 passes either way.
        var today = DateTime.UtcNow.Date.AddHours(13).AddMinutes(37);

        var earlier = MakeChat("Last week", "TulipWord came up.", today.AddDays(-5));
        var laterToday = MakeChat("Today", "TulipWord again.", today);
        var tomorrow = MakeChat("Tomorrow", "TulipWord in the future.", today.AddDays(1));
        foreach (var chat in new[] { earlier, laterToday, tomorrow })
            await _service.SaveAsync(chat, ct);

        var toDate = today.Date;
        var ranked = await _service.SearchRankedAsync("tulipword", null, toDate, null, null, 25, ct);
        var recency = await _service.SearchAsync(searchText: "tulipword", toDate: toDate, ct: ct);

        // Sets, not order — the two paths sort differently by design.
        Assert.Equal(
            recency.Select(c => c.Id).OrderBy(id => id).ToArray(),
            ranked.Select(h => h.Id).OrderBy(id => id).ToArray());
        Assert.Contains(ranked, h => h.Id == laterToday.Id);
        Assert.Contains(ranked, h => h.Id == earlier.Id);
        Assert.DoesNotContain(ranked, h => h.Id == tomorrow.Id);
    }

    private static SyncAssistantChat MakeChat(string title, string body, DateTime? updatedAt = null)
    {
        var stamp = updatedAt ?? DateTime.UtcNow;
        return new SyncAssistantChat
        {
            Id = Guid.NewGuid(),
            SchemaVersion = 1,
            Title = title,
            CreatedAt = stamp,
            UpdatedAt = stamp,
            LastAccessedAt = stamp,
            WindowMode = "Assistant",
            Messages =
            [
                new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "user",
                    Content = body,
                    Timestamp = stamp,
                },
            ],
        };
    }

    public void Dispose()
    {
        // Both services hold a dedicated connection to the same file; Windows keeps the temp file locked
        // until each one is closed.
        _service.Dispose();
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }
}

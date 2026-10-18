using System.Text.Json;
using Pia.Shared.Knowledge;
using Xunit;

namespace Pia.Tests.KnowledgeManager;

/// <summary>The server serializes with ASP.NET web defaults; a renamed member would silently read as its default, so the camelCase names are pinned.</summary>
public sealed class KbManagerContractsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void KnowledgeBase_RoundTripsWithCamelCaseNames()
    {
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new KbManagerKnowledgeBase(id, "Handbook", 3, true, false), Web);

        Assert.Contains("\"documentCount\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"shared\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"hasPrompt\":false", json, StringComparison.Ordinal);
        Assert.Equal(id, JsonSerializer.Deserialize<KbManagerKnowledgeBase>(json, Web)!.Id);
    }

    [Fact]
    public void Stats_ReadsTheServerShape()
    {
        const string json = """
            {"documentCount":4,"pendingCount":1,"processingCount":0,"readyCount":2,"failedCount":1,
             "sizeBytes":1200,"byteLimit":52428800,"documentLimit":100,"chunkCount":9,"indexedTokens":870,
             "retrievalCount":12,"lastRetrievedAt":"2026-09-30T08:00:00Z","embeddingTokensThisMonth":900}
            """;

        var stats = JsonSerializer.Deserialize<KbManagerStats>(json, Web)!;

        Assert.Equal(4, stats.DocumentCount);
        Assert.Equal(1, stats.FailedCount);
        Assert.Equal(52428800, stats.ByteLimit);
        Assert.Equal(900, stats.EmbeddingTokensThisMonth);
    }

    [Fact]
    public void Limits_MatchTheSpec()
    {
        Assert.Equal(10 * 1024 * 1024, KbManagerLimits.MaxContentBytes);
        Assert.Equal(32 * 1024, KbManagerLimits.MaxInlineContentBytes);
        Assert.Equal(8_000, KbManagerLimits.MaxPromptChars);
        Assert.Equal(500, KbManagerLimits.MaxTitleChars);
    }
}

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class ToolLoopUsageMeterTests
{
    private static ToolLoopUsageMeter Meter() => new(AiProviderType.Anthropic, "50000", "tool-loop");

    private static ChatMessage Result(string callId, object? body) =>
        new(ChatRole.Tool, [new FunctionResultContent(callId, body)]);

    [Fact]
    public void SizeOf_MeasuresStringsAsIs_AndObjectsAsJson()
    {
        Assert.Equal(0, ToolLoopUsageMeter.SizeOf(null));
        Assert.Equal(5, ToolLoopUsageMeter.SizeOf("hello"));
        Assert.Equal("{\"count\":3}".Length, ToolLoopUsageMeter.SizeOf(new Dictionary<string, int> { ["count"] = 3 }));
    }

    [Fact]
    public void RecordResult_TracksTotalsAndTheLargestTool()
    {
        var meter = Meter();

        meter.RecordResult("search_files", "c1", new string('x', 300));
        meter.RecordResult("git_log", "c2", new string('y', 900));
        meter.RecordResult("read_file", "c3", new string('z', 100));

        Assert.Equal(3, meter.Results);
        Assert.Equal(1300, meter.ResultChars);
        Assert.Equal(900, meter.LargestResultChars);
        Assert.Equal("git_log", meter.LargestResultTool);
    }

    [Fact]
    public void RecordRequest_CountsAResultOnEveryRequestThatCarriesIt()
    {
        var meter = Meter();
        var messages = new List<ChatMessage> { new(ChatRole.User, "go") };

        meter.RecordRequest(messages);
        messages.Add(Result("c1", new string('a', 1000)));
        meter.RecordResult("search_files", "c1", new string('a', 1000));
        meter.RecordRequest(messages);
        meter.RecordRequest(messages);

        Assert.Equal(3, meter.Requests);
        Assert.Equal(1000, meter.ResultChars);
        Assert.Equal(2000, meter.ToolResultCharsSent);
    }

    [Fact]
    public void RecordRequest_MeasuresAnObjectResultByItsRecordedSize()
    {
        var meter = Meter();
        var body = new Dictionary<string, int> { ["count"] = 3 };
        var chars = meter.RecordResult("query_items", "c1", body);

        meter.RecordRequest([Result("c1", body)]);

        Assert.Equal(chars, meter.ToolResultCharsSent);
    }

    [Fact]
    public void RecordRequest_CountsResultsCarriedFromAnEarlierStep()
    {
        var meter = Meter();

        meter.RecordRequest([Result("earlier", new string('c', 400))]);

        Assert.Equal(400, meter.ToolResultCharsSent);
        Assert.Equal(0, meter.Results);
    }

    [Fact]
    public void RecordUsage_SumsInputCachedAndCacheWrites()
    {
        var meter = Meter();

        meter.RecordUsage(new UsageDetails
        {
            InputTokenCount = 10_000,
            CachedInputTokenCount = 0,
            AdditionalCounts = new() { ["CacheCreationInputTokens"] = 9_000 },
        });
        meter.RecordUsage(new UsageDetails { InputTokenCount = 11_000, CachedInputTokenCount = 9_000 });

        Assert.Equal(21_000, meter.InputTokens);
        Assert.Equal(9_000, meter.CachedInputTokens);
        Assert.Equal(9_000, meter.CacheWriteTokens);
    }

    [Fact]
    public void LogSummary_IsOneInformationLineCarryingTheSettingsInEffect()
    {
        var meter = Meter();
        meter.RecordResult("search_files", "c1", "secret payload");
        var logger = new CapturingLogger<ToolLoopUsageMeterTests>();

        meter.LogSummary(logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("provider=Anthropic", entry.Message, StringComparison.Ordinal);
        Assert.Contains("largest=14 (search_files)", entry.Message, StringComparison.Ordinal);
        Assert.Contains("mcpCap=50000", entry.Message, StringComparison.Ordinal);
        Assert.Contains("promptCache=tool-loop", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret payload", entry.Message, StringComparison.Ordinal);
    }
}

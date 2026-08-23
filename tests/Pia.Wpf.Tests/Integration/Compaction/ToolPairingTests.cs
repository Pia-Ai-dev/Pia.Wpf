using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Integration.Compaction;

/// <summary>
/// Every provider that accepts tool rounds rejects a request whose tool calls and tool results do not pair up,
/// so an unpaired survivor is a 400 on a real step rather than a harness detail.
/// </summary>
public class ToolPairingTests
{
    private static readonly AgentContextBudget SmallWindow = new(8_000, 2_000);

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    public async Task Compaction_LeavesNoToolCallWithoutItsResult(int turns)
    {
        var transcript = SyntheticTranscript.Build(new SyntheticTranscriptOptions
        {
            Shape = SyntheticTranscriptShape.ChatToolHeavy,
            TurnCount = turns,
        });

        var result = await AgentContextCompactor.CompactAsync(
            transcript.Messages, SmallWindow, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.True(
            result.Count < transcript.Messages.Count,
            $"this fixture must be over budget or it proves nothing, but {transcript.Messages.Count} messages came back as {result.Count}");

        var calls = CallIds<FunctionCallContent>(result, c => c.CallId);
        var results = CallIds<FunctionResultContent>(result, c => c.CallId);

        Assert.Equal(calls.Order(StringComparer.Ordinal), results.Order(StringComparer.Ordinal));
    }

    private static List<string> CallIds<T>(IEnumerable<ChatMessage> messages, Func<T, string> id) where T : AIContent =>
        [.. messages.SelectMany(m => m.Contents).OfType<T>().Select(id)];
}

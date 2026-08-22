using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Integration.Compaction;

// No Microsoft.Agents.AI.Compaction type appears here: the experimental MAAI001 surface is contained inside
// AgentContextCompactor.cs, which is why this file compiles without a pragma.
public class UserMessagePinningTests
{
    private const int Notes = 12;

    private static readonly NullLogger Logger = NullLogger.Instance;

    private static readonly AgentContextBudget SmallWindow = new(8_000, 2_000);

    private static string Bulk(int approximateTokens) => new('x', approximateTokens * 4);

    // The agent-step shape with user messages in the MIDDLE, which is the only place the "user messages are
    // never compacted" invariant is contested — both ends are pinned.
    private static List<ChatMessage> UserInterleavedMessages(int notes = Notes, int fillerTokens = 600)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are Pia, an agent."),
            new(ChatRole.User, "THE GOAL: audit the ingest pipeline."),
        };

        for (var k = 1; k <= notes; k++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"NOTE {k}: stage {k} writes to the overflow shard."));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"reply {k}: {Bulk(fillerTokens)}"));
        }

        messages.Add(new ChatMessage(ChatRole.User, $"Execute step {notes + 1}"));
        return messages;
    }

    [Fact]
    public async Task HeadGoalAndNewestUserMessage_AreWithheldFromCompaction()
    {
        var messages = UserInterleavedMessages();

        var result = await AgentContextCompactor.CompactAsync(
            messages, SmallWindow, Logger, TestContext.Current.CancellationToken);

        Assert.True(
            result.Count < messages.Count,
            $"this fixture must be over budget or it proves nothing, but {messages.Count} messages came back as {result.Count}");

        // Reference identity, not text, so a future package version that clones fails here.
        Assert.Same(messages[0], result[0]);
        Assert.Same(messages[1], result[1]);
        Assert.Same(messages[^1], result[^1]);
    }

    [Fact]
    public async Task MiddleUserMessages_AreNotPinned_AndTheOldestAreEvicted()
    {
        var messages = UserInterleavedMessages();

        var result = await AgentContextCompactor.CompactAsync(
            messages, SmallWindow, Logger, TestContext.Current.CancellationToken);

        var retained = SyntheticTranscript.Trace(result);
        var survivors = Enumerable
            .Range(1, Notes)
            .Count(k => retained.Contains($"NOTE {k}:", StringComparison.Ordinal));

        // Pia pins the FIRST user message and the NEWEST one; everything between them is ordinary
        // compactable history.
        Assert.True(
            survivors < Notes,
            $"middle user messages must be evictable, but all {Notes} planted notes survived");

        Assert.DoesNotContain("NOTE 1:", retained, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEvictedUserMessage_IsGone_NotSummarized()
    {
        var messages = UserInterleavedMessages();

        // The oldest evictable position, and a token no filler can reproduce.
        messages.Insert(2, new ChatMessage(ChatRole.User, "LEDGER 4471: the spill ledger was reconciled by hand."));
        var sent = SyntheticTranscript.Trace(messages);

        var result = await AgentContextCompactor.CompactAsync(
            messages, SmallWindow, Logger, TestContext.Current.CancellationToken);

        // Its absence is also what proves the fixture was over budget: an uncompacted request still carries it.
        Assert.DoesNotContain("LEDGER 4471", SyntheticTranscript.Trace(result), StringComparison.Ordinal);

        // Nothing on this path summarizes, so an evicted message leaves no stand-in either — no message comes back
        // carrying text the caller never sent, which is what makes an anchor index the only route back to it.
        Assert.All(result, m => Assert.Contains(m.Text, sent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheHeadPinCoversExactlyOneUserMessage()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are Pia, an agent."),
            new(ChatRole.User, "THE GOAL: audit the ingest pipeline."),
            new(ChatRole.User, "SECOND HEAD NOTE: the ingest shard rotates at midnight."),
        };
        for (var i = 1; i <= 12; i++)
            messages.Add(new ChatMessage(ChatRole.Assistant, $"step {i} reply: {Bulk(600)}"));
        messages.Add(new ChatMessage(ChatRole.User, "Execute step 13"));

        var result = await AgentContextCompactor.CompactAsync(
            messages, SmallWindow, Logger, TestContext.Current.CancellationToken);

        Assert.True(
            result.Count < messages.Count,
            $"this fixture must be over budget or it proves nothing, but {messages.Count} messages came back as {result.Count}");

        // The boundary in both directions: widening the head pin to the whole leading user run keeps the second
        // note, narrowing it to system-only drops the goal.
        Assert.Same(messages[1], result[1]);
        Assert.Same(messages[^1], result[^1]);
        Assert.DoesNotContain("SECOND HEAD NOTE", SyntheticTranscript.Trace(result), StringComparison.Ordinal);
    }
}

using Microsoft.Extensions.AI;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The bound on cross-step tool context: what a round contributes, and what a later step's request keeps of it.
/// </summary>
public class AgentToolCarryoverTests
{
    private static ChatMessage Call(string callId, string tool, string? path = null) =>
        new(ChatRole.Assistant, [new FunctionCallContent(
            callId, tool, path is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["path"] = path })]);

    private static ChatMessage Result(string callId, string body) =>
        new(ChatRole.Tool, [new FunctionResultContent(callId, body)]);

    private static List<ChatMessage> Rounds(int count, int bodyChars = 10) =>
        [.. Enumerable.Range(0, count).SelectMany(i => new[]
        {
            Call("c" + i, "read_file", "f" + i + ".csv"),
            Result("c" + i, new string((char)('a' + (i % 26)), bodyChars)),
        })];

    private static string? Body(ChatMessage message) =>
        message.Contents.OfType<FunctionResultContent>().FirstOrDefault()?.Result as string;

    [Fact]
    public void Capture_CapsALongResult_AndSaysSo()
    {
        var round = new[] { Call("c0", "read_file", "big.csv"), Result("c0", new string('x', AgentToolCarryover.MaxCarriedResultChars + 500)) };

        var captured = AgentToolCarryover.Capture(round);

        var body = Body(captured[1])!;
        Assert.StartsWith(new string('x', AgentToolCarryover.MaxCarriedResultChars), body, StringComparison.Ordinal);
        Assert.EndsWith("[truncated]", body, StringComparison.Ordinal);
    }

    /// <summary>The capped copy must not be the object the tool loop is still sending this round.</summary>
    [Fact]
    public void Capture_LeavesTheRoundsOwnMessagesAlone()
    {
        var long_ = new string('x', AgentToolCarryover.MaxCarriedResultChars + 500);
        var round = new[] { Call("c0", "read_file", "big.csv"), Result("c0", long_) };

        AgentToolCarryover.Capture(round);

        Assert.Equal(long_, Body(round[1]));
    }

    [Fact]
    public void Capture_KeepsAShortResultVerbatim()
    {
        var round = new[] { Call("c0", "read_file", "small.csv"), Result("c0", "two rows") };

        var captured = AgentToolCarryover.Capture(round);

        Assert.Equal("read_file", captured[0].Contents.OfType<FunctionCallContent>().Single().Name);
        Assert.Equal("two rows", Body(captured[1]));
    }

    /// <summary>The persisted shape holds tool content only; the live carry keeps the prose (AsSent).</summary>
    [Fact]
    public void Capture_DropsTheProseThatCameWithTheCall()
    {
        var round = new ChatMessage[]
        {
            new(ChatRole.Assistant, [
                new TextContent("Let me read that file."),
                new FunctionCallContent("c0", "read_file", new Dictionary<string, object?> { ["path"] = "a.csv" }),
            ]),
            Result("c0", "two rows"),
        };

        var captured = AgentToolCarryover.Capture(round);

        Assert.DoesNotContain(captured.SelectMany(m => m.Contents), c => c is TextContent);
        Assert.Single(captured.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
    }

    /// <summary>The live carry is the round as the next request sends it: prose, calls and the full result.</summary>
    [Fact]
    public void AsSent_KeepsProseCallsAndFullResults_AndDropsOnlyReasoning()
    {
        var long_ = new string('x', AgentToolCarryover.MaxCarriedResultChars + 500);
        var round = new ChatMessage[]
        {
            new(ChatRole.Assistant, [
                new TextReasoningContent("weighing it"),
                new TextContent("Let me read that file."),
                new FunctionCallContent("c0", "read_file", new Dictionary<string, object?> { ["path"] = "a.csv" }),
            ]),
            Result("c0", long_),
            new(ChatRole.Assistant, [new TextReasoningContent("only thinking")]),
        };

        var sent = AgentToolCarryover.AsSent(round);

        Assert.Equal(2, sent.Count);
        Assert.DoesNotContain(sent.SelectMany(m => m.Contents), c => c is TextReasoningContent);
        Assert.Equal("Let me read that file.", sent[0].Text);
        Assert.Equal("c0", sent[0].Contents.OfType<FunctionCallContent>().Single().CallId);
        Assert.Same(round[1], sent[1]);
        Assert.Equal(long_, Body(sent[1]));
        // The tool loop still holds its own thinking-bearing message.
        Assert.Contains(round[0].Contents, c => c is TextReasoningContent);
    }

    /// <summary>A turn that sends no tools gets the transcript without them — a provider handed tool_calls with
    /// no tools declared can reject the whole request.</summary>
    [Fact]
    public void WithoutToolExchanges_KeepsOnlyTheOrdinaryTranscript()
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, "sys"), new(ChatRole.User, "goal") };
        messages.AddRange(Rounds(2));
        messages.Add(new ChatMessage(ChatRole.Assistant, "the reply"));

        var stripped = AgentToolCarryover.WithoutToolExchanges(messages);

        Assert.Equal(3, stripped.Count);
        Assert.DoesNotContain(stripped.SelectMany(m => m.Contents), c => c is FunctionCallContent or FunctionResultContent);
        // Nothing to strip returns the input rather than a copy.
        Assert.Same(stripped, AgentToolCarryover.WithoutToolExchanges(stripped));
    }

    // One sixth of the budget, so 17 such results clear exactly the oldest 8.
    private const int Big = AgentToolCarryover.CarriedResultBudgetChars / 6;

    private static List<string> Bodies(IReadOnlyList<ChatMessage> messages) =>
        [.. AgentToolCarryover.ClearOldResults(messages).Select(Body).Where(b => b is not null).Select(b => b!)];

    private static int ClearedPrefix(IReadOnlyList<ChatMessage> messages)
    {
        var bodies = Bodies(messages);
        var cut = bodies.TakeWhile(b => b.StartsWith("[result cleared;", StringComparison.Ordinal)).Count();
        Assert.DoesNotContain(bodies.Skip(cut), b => b.StartsWith("[result cleared;", StringComparison.Ordinal));
        return cut;
    }

    [Fact]
    public void ClearOldResults_ReturnsTheInput_WhenNothingIsStaleYet()
    {
        var messages = Rounds(60);

        var cleared = AgentToolCarryover.ClearOldResults(messages);

        Assert.Same(messages, cleared);
    }

    /// <summary>Tiny results cost nothing to carry; clearing them would only re-write the cached prefix.</summary>
    [Theory]
    [MemberData(nameof(OneToSixty))]
    public void ClearOldResults_NeverClearsTinyResults(int results)
    {
        Assert.Equal(0, ClearedPrefix(Rounds(results)));
    }

    public static TheoryData<int> OneToSixty() => [.. Enumerable.Range(1, 60)];

    /// <summary>A pure function of the result sizes: the cut moves only in multiples of 8, and only once the
    /// uncleared tail would exceed the budget (here 20 bodies fit).</summary>
    [Theory]
    [InlineData(20, 0)]
    [InlineData(21, 8)]
    [InlineData(28, 8)]
    [InlineData(29, 16)]
    [InlineData(36, 16)]
    [InlineData(37, 24)]
    [InlineData(60, 40)]
    public void ClearOldResults_CutsInChunksOfEight_OnlyWhenTheTailExceedsTheBudget(int results, int expected)
    {
        var messages = Rounds(results, AgentToolCarryover.CarriedResultBudgetChars / 20);

        Assert.Equal(expected, ClearedPrefix(messages));
        Assert.Equal(expected, ClearedPrefix(messages));
    }

    /// <summary>A result larger than the whole budget clears what it can but never the newest 8.</summary>
    [Theory]
    [InlineData(9, 0)]
    [InlineData(21, 8)]
    public void ClearOldResults_KeepsTheNewest8_EvenWhenOneOfThemExceedsTheBudget(int results, int expected)
    {
        var messages = Rounds(results - 1);
        messages.Add(Call("huge", "read_file", "huge.csv"));
        messages.Add(Result("huge", new string('h', AgentToolCarryover.CarriedResultBudgetChars + 1)));

        var cut = ClearedPrefix(messages);

        Assert.Equal(expected, cut);
        Assert.True(results - cut >= 8);
    }

    /// <summary>Build, never mutate: the input list is the executor's accumulating transcript, and an in-place
    /// rewrite would take the body from every later step at once — with nothing failing.</summary>
    [Fact]
    public void ClearOldResults_DoesNotMutateTheInput()
    {
        var messages = Rounds(18, Big);
        var before = messages.Select(Body).ToList();

        AgentToolCarryover.ClearOldResults(messages);

        Assert.Equal(before, messages.Select(Body));
    }

    /// <summary>The call is the record that the call was made — it is what lets the model issue it again.</summary>
    [Fact]
    public void ClearOldResults_KeepsTheCallAndNamesItsPath()
    {
        var messages = Rounds(17, Big);

        var cleared = AgentToolCarryover.ClearOldResults(messages);

        Assert.Contains(cleared.SelectMany(m => m.Contents).OfType<FunctionCallContent>(), c => c.CallId == "c0");
        Assert.Equal("[result cleared; call read_file on f0.csv again if you need it]", Body(cleared[1]));
    }

    [Fact]
    public void ClearOldResults_OmitsThePath_WhenTheCallCarriedNone()
    {
        var messages = new List<ChatMessage> { Call("listing", "list_files"), Result("listing", "a listing") };
        messages.AddRange(Rounds(16, Big));

        var cleared = AgentToolCarryover.ClearOldResults(messages);

        Assert.Equal("[result cleared; call list_files again if you need it]", Body(cleared[1]));
    }

    // The server's native-content store matches on the toolu_ id alone, so a carried id must never change.
    [Fact]
    public void Capture_KeepsEveryCallIdVerbatim_EvenWhenItCapsTheResult()
    {
        var round = new[]
        {
            Call("toolu_01AbC-dEf_9", "read_file", "big.csv"),
            Result("toolu_01AbC-dEf_9", new string('x', AgentToolCarryover.MaxCarriedResultChars + 1)),
        };

        var captured = AgentToolCarryover.Capture(round);

        Assert.Equal("toolu_01AbC-dEf_9", captured[0].Contents.OfType<FunctionCallContent>().Single().CallId);
        Assert.Equal("toolu_01AbC-dEf_9", captured[1].Contents.OfType<FunctionResultContent>().Single().CallId);
    }

    [Fact]
    public void ClearOldResults_KeepsEveryCallIdVerbatim_InOrder()
    {
        var messages = Rounds(19, Big);
        var before = messages.SelectMany(m => m.Contents).Select(IdOf).ToList();

        var cleared = AgentToolCarryover.ClearOldResults(messages);

        Assert.Equal(before, cleared.SelectMany(m => m.Contents).Select(IdOf));
    }

    private static string? IdOf(AIContent content) => content switch
    {
        FunctionCallContent c => "call:" + c.CallId,
        FunctionResultContent r => "result:" + r.CallId,
        _ => null,
    };
}

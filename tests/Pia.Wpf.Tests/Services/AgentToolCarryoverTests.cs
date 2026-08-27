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

    /// <summary>The prose a round produced is already in the exchange's visible reply; carrying it here too
    /// would cost tokens and read as the model having said it twice.</summary>
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

    [Fact]
    public void ClearOldResults_ReturnsTheInput_WhenNothingIsStaleYet()
    {
        var messages = Rounds(AgentToolCarryover.KeptResults);

        var cleared = AgentToolCarryover.ClearOldResults(messages);

        Assert.Same(messages, cleared);
    }

    [Fact]
    public void ClearOldResults_KeepsTheNewestKeptCount_AndClearsTheRest()
    {
        var messages = Rounds(AgentToolCarryover.KeptResults + 3);

        var cleared = AgentToolCarryover.ClearOldResults(messages);

        var bodies = cleared.Select(Body).Where(b => b is not null).ToList();
        Assert.Equal(AgentToolCarryover.KeptResults + 3, bodies.Count);
        Assert.Equal(3, bodies.Count(b => b!.StartsWith("[result cleared;", StringComparison.Ordinal)));
        Assert.All(bodies.Take(3), b => Assert.StartsWith("[result cleared;", b!, StringComparison.Ordinal));
    }

    /// <summary>Build, never mutate: the input list is the executor's accumulating transcript, and an in-place
    /// rewrite would take the body from every later step at once — with nothing failing.</summary>
    [Fact]
    public void ClearOldResults_DoesNotMutateTheInput()
    {
        var messages = Rounds(AgentToolCarryover.KeptResults + 2);
        var before = messages.Select(Body).ToList();

        AgentToolCarryover.ClearOldResults(messages);

        Assert.Equal(before, messages.Select(Body));
    }

    /// <summary>The call is the record that the call was made — it is what lets the model issue it again.</summary>
    [Fact]
    public void ClearOldResults_KeepsTheCallAndNamesItsPath()
    {
        var messages = Rounds(AgentToolCarryover.KeptResults + 1);

        var cleared = AgentToolCarryover.ClearOldResults(messages);

        Assert.Contains(cleared.SelectMany(m => m.Contents).OfType<FunctionCallContent>(), c => c.CallId == "c0");
        Assert.Equal("[result cleared; call read_file on f0.csv again if you need it]", Body(cleared[1]));
    }

    [Fact]
    public void ClearOldResults_OmitsThePath_WhenTheCallCarriedNone()
    {
        var messages = new List<ChatMessage> { Call("listing", "list_files"), Result("listing", "a listing") };
        messages.AddRange(Rounds(AgentToolCarryover.KeptResults));

        var cleared = AgentToolCarryover.ClearOldResults(messages);

        Assert.Equal("[result cleared; call list_files again if you need it]", Body(cleared[1]));
    }
}

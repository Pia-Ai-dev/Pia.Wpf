using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Verifier behavior (§13.x). An <c>emit_verdict</c> call parses into a VerdictResult; no-call retries
/// once (firmer) then degrades to ACCEPT; usage is summed from the yielded <see cref="Finished"/> items
/// (unlike the planner, which discards it) so the orchestrator can accrue it run-level.
/// </summary>
public sealed class AgentVerifierTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };

    private static RunContext Ctx()
    {
        var c = new RunContext("build a thing", RunProfile.Interactive);
        c.RecordStep(new AgentStep { Ordinal = 0, Title = "A", Intent = "ia" },
            new StepTurnResult(true, false, null, "step result text", null, Guid.NewGuid(), Guid.NewGuid()));
        return c;
    }

    private AgentVerifier BuildVerifier() => new(_ai, NullLogger<AgentVerifier>.Instance);

    // Drives one verify turn: invokes the captured toolHandler with a synthetic emit_verdict call
    // (when emitArgs is set) then yields Finished carrying usage — the loop drains the whole stream.
    private static async IAsyncEnumerable<ChatStreamItem> VerdictStream(
        Func<FunctionCallContent, Task<object?>>? handler, Dictionary<string, object?>? emitArgs, UsageDetails? usage)
    {
        if (handler is not null && emitArgs is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_verdict", emitArgs));
        await Task.Yield();
        yield return new Finished(usage, "test-model");
    }

    private static Dictionary<string, object?> V(bool passed, string reason, params string[] missing) =>
        new() { ["passed"] = passed, ["reason"] = reason, ["missing"] = missing.Cast<object?>().ToArray() };

    private void ReturnsVerdict(Dictionary<string, object?>? emitArgs, UsageDetails? usage = null)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => VerdictStream(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3), emitArgs, usage));
    }

    [Fact]
    public async Task VerifyAsync_EmitPassedTrue_ReturnsPassed()
    {
        ReturnsVerdict(V(true, "looks complete"));

        var result = await BuildVerifier().VerifyAsync(Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        Assert.Empty(result.Missing);
        _ai.Received(1).GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyAsync_EmitPassedFalse_ReturnsFailWithMissing()
    {
        ReturnsVerdict(V(false, "incomplete", "artifact X"));

        var result = await BuildVerifier().VerifyAsync(Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal("incomplete", result.Reason);
        Assert.Contains("artifact X", result.Missing);
    }

    [Fact]
    public async Task VerifyAsync_NoCall_RetriesOnce_ThenAccept()
    {
        ReturnsVerdict(emitArgs: null); // no emit_verdict call on either attempt

        var result = await BuildVerifier().VerifyAsync(Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.Passed); // degrade → accept
        _ai.Received(2).GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyAsync_CapturesUsageFromFinished()
    {
        ReturnsVerdict(V(true, "ok"), new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 });

        var result = await BuildVerifier().VerifyAsync(Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.NotNull(result.Usage);
        Assert.Equal(7, result.Usage!.InputTokenCount);
        Assert.Equal(3, result.Usage.OutputTokenCount);
    }
}

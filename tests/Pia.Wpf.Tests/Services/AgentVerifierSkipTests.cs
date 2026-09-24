using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>A lone step that named no artifact gives the critic nothing but the step's own summary, so the
/// verify turn can only agree with it — at the cost of a full round-trip.</summary>
public sealed class AgentVerifierSkipTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();

    public AgentVerifierSkipTests()
        => _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(new AppSettings()));

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AgentVerifier Verifier() => new(_ai, _settingsService, NullLogger<AgentVerifier>.Instance);

    private static RunContext Ctx(params (string? Declared, string? Reported, bool Succeeded)[] steps)
    {
        var c = new RunContext("answer the question", RunProfile.Interactive);
        for (var i = 0; i < steps.Length; i++)
        {
            c.RecordStep(
                new AgentStep { Ordinal = i, Title = "S" + i, Intent = "do " + i, ExpectedArtifact = steps[i].Declared },
                new StepTurnResult(steps[i].Succeeded, false, null, "did it", null, Guid.NewGuid(), Guid.NewGuid(),
                    Outcome: new StepOutcomeClaim(steps[i].Succeeded, "done", steps[i].Reported)));
        }
        return c;
    }

    private void AssertNoVerifyTurn() => _ai.DidNotReceiveWithAnyArgs().GetChatCompletionWithToolsAsync(
        default!, default!, default, default, default, default, default, cancellationToken: default);

    private void AssertVerifyTurnRan() => _ai.ReceivedWithAnyArgs(1).GetChatCompletionWithToolsAsync(
        default!, default!, default, default, default, default, default, cancellationToken: default);

    [Fact]
    public async Task OneStepNamingNoArtifact_SkipsTheTurnAndAccepts()
    {
        var verdict = await Verifier().VerifyAsync(Ctx((null, null, true)), Persona(), Provider(), Ct);

        Assert.True(verdict.Passed);
        Assert.Null(verdict.Usage);
        AssertNoVerifyTurn();
    }

    [Fact]
    public async Task OneStepDeclaringAnArtifact_StillVerifies()
    {
        StubVerdict();

        await Verifier().VerifyAsync(Ctx(("report.md", null, true)), Persona(), Provider(), Ct);

        AssertVerifyTurnRan();
    }

    [Fact]
    public async Task OneStepReportingAnArtifactItNeverDeclared_StillVerifies()
    {
        StubVerdict();

        await Verifier().VerifyAsync(Ctx((null, "notes.md", true)), Persona(), Provider(), Ct);

        AssertVerifyTurnRan();
    }

    [Fact]
    public async Task TwoStepsNamingNoArtifact_StillVerifies()
    {
        StubVerdict();

        await Verifier().VerifyAsync(Ctx((null, null, true), (null, null, true)), Persona(), Provider(), Ct);

        AssertVerifyTurnRan();
    }

    [Fact]
    public async Task OneStepThatFailed_StillVerifies()
    {
        StubVerdict();

        await Verifier().VerifyAsync(Ctx((null, null, false)), Persona(), Provider(), Ct);

        AssertVerifyTurnRan();
    }

    [Fact]
    public async Task OneStepWithSkippedSiblings_StillVerifies()
    {
        StubVerdict();
        var ctx = Ctx((null, null, true));
        ctx.SetSkippedTitles(["the step that never ran"]);

        await Verifier().VerifyAsync(ctx, Persona(), Provider(), Ct);

        AssertVerifyTurnRan();
    }

    private void StubVerdict()
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => VerdictStream(ci.ArgAt<ToolCallHandler?>(3)));
    }

    private static async IAsyncEnumerable<ChatStreamItem> VerdictStream(ToolCallHandler? handler)
    {
        if (handler is not null)
            await handler(
                new FunctionCallContent(Guid.NewGuid().ToString(), "emit_verdict", new Dictionary<string, object?>
                {
                    ["passed"] = true, ["reason"] = "fine", ["missing"] = Array.Empty<object?>(),
                }),
                new ToolDispatchContext(1));
        await Task.Yield();
        yield return new Finished(null, "test-model");
    }
}

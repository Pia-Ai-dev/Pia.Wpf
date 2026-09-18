using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The plan and verify turns want exactly one structured call and read it out of the arguments, so
/// the round after it is dead air the user waits through. Both handlers must end the loop on capture.</summary>
public sealed class AgentTerminalToolStopTests : IDisposable
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly AppSettings _settings = new();
    private readonly ToolLoopStopSignal _stop = new();
    private readonly string _dir;

    public AgentTerminalToolStopTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaTerminalStop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _settings.AssistantFilesFolder = _dir;
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_settings));
    }

    public void Dispose() => TempPath.Remove(_dir);

    private static AiProvider Provider() => new()
    {
        Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI, SupportsToolCalling = true,
    };

    private static Persona Persona() => new() { Id = Guid.NewGuid(), Name = "Pia", SystemPrompt = "sys" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async IAsyncEnumerable<ChatStreamItem> Stream(
        ToolCallHandler? handler, string toolName, Dictionary<string, object?> args)
    {
        if (handler is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), toolName, args),
                new ToolDispatchContext(1, _stop));
        await Task.Yield();
        yield return new Finished(null, "test-model");
    }

    private void Emits(string toolName, Dictionary<string, object?> args)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => Stream(ci.ArgAt<ToolCallHandler?>(3), toolName, args));
    }

    [Fact]
    public async Task EmitPlan_EndsThePlanTurn()
    {
        Emits("emit_plan", new Dictionary<string, object?>
        {
            ["steps"] = new object[]
            {
                new Dictionary<string, object?> { ["title"] = "do it", ["intent"] = "get it done" },
            },
        });

        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        var planner = new AgentPlanner(_ai, new AiProviderHandlerResolver([handler]), _settingsService,
            NullLogger<AgentPlanner>.Instance);

        await planner.PlanAsync(new RunContext("ship the widget", RunProfile.Interactive).Goal,
            new RunContext("ship the widget", RunProfile.Interactive), Persona(), Provider(), Ct);

        Assert.True(_stop.IsStopRequested);
    }

    [Fact]
    public async Task UnreadablePlanArguments_StillEndThePlanTurn()
    {
        // The salvage path returns whatever it can and the turn is over either way; leaving the loop running
        // here would pay the round on exactly the turns that already went wrong.
        Emits("emit_plan", new Dictionary<string, object?> { ["steps"] = "not an array" });

        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        var planner = new AgentPlanner(_ai, new AiProviderHandlerResolver([handler]), _settingsService,
            NullLogger<AgentPlanner>.Instance);

        await planner.PlanAsync("ship the widget", new RunContext("ship the widget", RunProfile.Interactive),
            Persona(), Provider(), Ct);

        Assert.True(_stop.IsStopRequested);
    }

    [Fact]
    public async Task EmitVerdict_EndsTheVerifyTurn()
    {
        Emits("emit_verdict", new Dictionary<string, object?>
        {
            ["passed"] = true, ["reason"] = "looks right", ["missing"] = Array.Empty<object?>(),
        });

        var ctx = new RunContext("build a thing", RunProfile.Interactive);
        ctx.RecordStep(new AgentStep { Ordinal = 0, Title = "A", Intent = "ia" },
            new StepTurnResult(true, false, null, "step result text", null, Guid.NewGuid(), Guid.NewGuid()));

        var verifier = new AgentVerifier(_ai, _settingsService, NullLogger<AgentVerifier>.Instance);

        await verifier.VerifyAsync(ctx, Persona(), Provider(), Ct);

        Assert.True(_stop.IsStopRequested);
    }
}

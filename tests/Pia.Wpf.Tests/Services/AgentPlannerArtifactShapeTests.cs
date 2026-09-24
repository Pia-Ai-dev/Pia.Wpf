using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The plan prompt asks a multi-file step to name every file it writes, so a compliant model sends
/// <c>expectedArtifact</c> as an array — which used to throw away the whole plan and degrade the run to a
/// single turn with an empty plan box.
/// </summary>
public sealed class AgentPlannerArtifactShapeTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly AppSettings _appSettings = new();

    public AgentPlannerArtifactShapeTests() =>
        _settings.GetSettingsAsync().Returns(_ => Task.FromResult(_appSettings));

    [Fact]
    public async Task AnArrayOfArtifacts_KeepsThePlan_AndJoinsTheNames()
    {
        Emit(new Dictionary<string, object?>
        {
            ["steps"] = new object[]
            {
                Step("Read the inputs", "read them", null),
                Step("Write the outputs", "write them", new[] { "reorder-report.md", "reorder-list.csv" }),
            },
        });

        var plan = await Plan();

        Assert.False(plan.FallBackToSingleTurn);
        Assert.Equal(2, plan.Steps.Count);
        // ", " is a separator AgentVerifier.FileCandidates already splits a declaration on, so both names
        // stay probeable.
        Assert.Equal("reorder-report.md, reorder-list.csv", plan.Steps[1].ExpectedArtifact);
    }

    [Fact]
    public async Task APlainStringArtifact_IsUnchanged()
    {
        Emit(new Dictionary<string, object?>
        {
            ["steps"] = new object[]
            {
                Step("Read the inputs", "read them", null),
                Step("Write one file", "write it", "report.md"),
            },
        });

        var plan = await Plan();

        Assert.Equal("report.md", plan.Steps[1].ExpectedArtifact);
    }

    [Fact]
    public async Task AnEmptyArtifactArray_ReadsAsNoArtifact()
    {
        Emit(new Dictionary<string, object?>
        {
            ["steps"] = new object[]
            {
                Step("Read the inputs", "read them", Array.Empty<string>()),
                Step("Think about it", "think", null),
            },
        });

        var plan = await Plan();

        Assert.False(plan.FallBackToSingleTurn);
        Assert.Null(plan.Steps[0].ExpectedArtifact);
    }

    /// <summary>One unreadable step used to cost the plan every readable sibling beside it.</summary>
    [Fact]
    public async Task OneMalformedStep_KeepsTheReadableOnes()
    {
        Emit(new Dictionary<string, object?>
        {
            ["steps"] = new object[]
            {
                Step("Read the inputs", "read them", null),
                new Dictionary<string, object?> { ["title"] = new[] { "not", "a", "title" }, ["intent"] = "broken" },
                Step("Write the report", "write it", "report.md"),
            },
        });

        var plan = await Plan();

        Assert.False(plan.FallBackToSingleTurn);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("Read the inputs", plan.Steps[0].Title);
        Assert.Equal("Write the report", plan.Steps[1].Title);
    }

    private static Dictionary<string, object?> Step(string title, string intent, object? artifact) =>
        new() { ["title"] = title, ["intent"] = intent, ["expectedArtifact"] = artifact };

    private void Emit(Dictionary<string, object?> args) =>
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => Stream((ToolCallHandler?)ci[3], args));

    private static async IAsyncEnumerable<ChatStreamItem> Stream(ToolCallHandler? handler, Dictionary<string, object?> args)
    {
        if (handler is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_plan", args), new ToolDispatchContext(1));
        await Task.Yield();
        yield return new Finished(null, "test-model");
    }

    private Task<PlanResult> Plan()
    {
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        handler.DropsReasoningEffortWithTools.Returns(false);
        var planner = new AgentPlanner(
            _ai, new AiProviderHandlerResolver([handler]), _settings, NullLogger<AgentPlanner>.Instance);
        return planner.PlanAsync(
            "build the reorder report",
            new RunContext("build the reorder report", RunProfile.Interactive),
            new Persona { Name = "Pia", SystemPrompt = "sys" },
            new AiProvider { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI, SupportsToolCalling = true },
            TestContext.Current.CancellationToken);
    }
}

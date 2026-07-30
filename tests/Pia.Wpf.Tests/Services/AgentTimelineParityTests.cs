using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The executor-parity guardrail, executable: the SAME tool call, authorized for the SAME reason, recorded by
/// the two run gates, must produce rows that differ only in <c>Surface</c>. A timeline that works headless and
/// not live (or the reverse) is a defect, and only a test that drives both catches it.
/// <para>
/// The call is a policy-covered <c>write_file</c>, chosen because it is the one authorization both surfaces can
/// reach for the identical reason (<c>AutoApprovedPolicy</c>): a standing grant is interactive-only and a named
/// grant is unattended-only, so either of those would compare two different decisions.
/// </para>
/// </summary>
public sealed class AgentTimelineParityTests
{
    [Fact]
    public async Task LiveAndHeadlessRecordTheSameCall_DifferingOnlyInSurface()
    {
        var policy = new RunAutonomyPolicy([ToolClass.Files]);
        var filesId = BuiltInPluginDefaults.FilesPluginId;

        var live = await RecordLiveAsync(policy, filesId);
        var headless = await RecordHeadlessAsync(policy, filesId);

        Assert.Equal(ToolGateDecision.AutoApprovedPolicy, live.Decision);
        Assert.Equal(live.Decision, headless.Decision);
        Assert.Equal(live.Outcome, headless.Outcome);
        Assert.Equal(live.ToolName, headless.ToolName);
        Assert.Equal(live.ToolClass, headless.ToolClass);
        Assert.Equal(live.Kind, headless.Kind);
        Assert.Equal(live.ResultChars, headless.ResultChars);

        // …and the one thing that MUST differ.
        Assert.Equal(ToolGateSurface.Interactive, live.Surface);
        Assert.Equal(ToolGateSurface.Unattended, headless.Surface);
    }

    private static async Task<AgentTimelineEvent> RecordLiveAsync(RunAutonomyPolicy policy, Guid filesId)
    {
        var timeline = new RecordingTimelineService();
        var ai = Substitute.For<IAiClientService>();
        var plugins = Substitute.For<IPluginService>();
        var cards = Substitute.For<IActionCardBuilder>();
        var loc = Substitute.For<ILocalizationService>();
        var permissions = Substitute.For<IToolPermissionService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Done");
        cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<ToolClass?>())
            .Returns(new ActionCardInfo
            {
                Title = "write_file", Summary = "write_file",
                Category = ActionCardCategory.Files, ToolName = "write_file", PluginId = filesId,
            });
        plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)Pending(filesId)));
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<AgentContextBudget?>())
            .Returns(ci => Drive(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3)));

        var session = new ChatSession(
            Substitute.For<ITokenMapService>(), ai, plugins, cards, permissions, loc, NullLogger.Instance, _ => true);
        var runId = Guid.NewGuid();
        var result = await session.RunStepTurnAsync(
            new StepTurnSpec(
                RunId: runId, Ordinal: 0, Intent: "write it", ExpectedArtifact: null, SystemPrompt: "system",
                Persona: new PersonaAttribution(Guid.NewGuid(), "Pia", "🤖"), Provider: Provider(),
                Tools: new List<AITool>(), SupportsTools: true, WebSearchActive: false, TokenizationEnabled: false,
                Policy: policy, StepId: Guid.NewGuid(),
                Timeline: new AgentTimelineScope(timeline, runId, null)),
            new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        return Assert.Single(timeline.Rows);
    }

    private static async Task<AgentTimelineEvent> RecordHeadlessAsync(RunAutonomyPolicy policy, Guid filesId)
    {
        var timeline = new RecordingTimelineService();
        var ai = Substitute.For<IAiClientService>();
        var plugins = Substitute.For<IPluginService>();
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)Pending(filesId)));
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Drive(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3)));

        ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();
        var runner = new BackgroundAssistantTurnRunner(
            ai, plugins, Substitute.For<IAssistantPromptComposer>(), Substitute.For<IPersonaService>(),
            Substitute.For<IAssistantChatService>(), Substitute.For<IChatTitleService>(), settings,
            TokenMapFactory, Substitute.For<IAgentRunService>(), new ExecutingRunStore(),
            NullLogger<BackgroundAssistantTurnRunner>.Instance);

        var runId = Guid.NewGuid();
        await runner.RunExchangeAsync(
            [new ChatMessage(ChatRole.User, "go")], Provider(),
            new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false),
            // No named grant: the POLICY has to be the only authority, or the two decisions would not match.
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            TestContext.Current.CancellationToken,
            policy: policy,
            timeline: new AgentTimelineScope(timeline, runId, null));

        return Assert.Single(timeline.Rows);
    }

    private static PluginToolCall Pending(Guid filesId) => new(
        "write_file", filesId, "files", "files: write_file", null, () => Task.FromResult<object?>("written"));

    private static AiProvider Provider() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test",
        Endpoint = "http://localhost",
        ProviderType = AiProviderType.OpenAI,
        TimeoutSeconds = 60,
    };

    private static async IAsyncEnumerable<ChatStreamItem> Drive(Func<FunctionCallContent, Task<object?>>? handler)
    {
        if (handler is not null)
            await handler(new FunctionCallContent("call-1", "write_file", new Dictionary<string, object?> { ["path"] = "a.md" }));

        yield return new TextDelta("Done.");
        yield return new Finished(null, "test-model");
    }
}

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

/// <summary>A policy-covered <c>write_file</c> is the one authorization both gates reach for the identical reason; a standing
/// grant is interactive-only and a named grant unattended-only, so either would compare two different decisions.</summary>
public sealed class AgentTimelineParityTests
{
    [Fact]
    public async Task LiveAndHeadlessRecordTheSameCall_DifferingOnlyInSurface()
    {
        var policy = new RunAutonomyPolicy([ToolClass.Files]);
        var filesId = BuiltInPluginDefaults.FilesPluginId;

        var live = await RecordLiveAsync(policy, filesId, Pending(filesId));
        var headless = await RecordHeadlessAsync(policy, Pending(filesId));

        Assert.Equal(ToolGateDecision.AutoApprovedPolicy, live.Decision);
        Assert.Equal(live.Decision, headless.Decision);
        Assert.Equal(live.Outcome, headless.Outcome);
        Assert.Equal(live.ToolName, headless.ToolName);
        Assert.Equal(live.ToolClass, headless.ToolClass);
        Assert.Equal(live.Kind, headless.Kind);
        Assert.Equal(live.ResultChars, headless.ResultChars);

        // A correlation column populated on one surface and NULL on the other is a silent bug, so assert both.
        Assert.Equal("call-1", live.ToolCallId);
        Assert.Equal(live.ToolCallId, headless.ToolCallId);
        // Round is 1-based and read off the dispatch context, not counted by either gate.
        Assert.Equal(1, live.Round);
        Assert.Equal(live.Round, headless.Round);
        Assert.NotNull(live.RequestedAt);
        Assert.NotNull(live.DecidedAt);
        Assert.NotNull(headless.RequestedAt);
        Assert.NotNull(headless.DecidedAt);
        // >=, never >: Resolve is far faster than UtcNow's ~1 ms resolution, so the instants are normally equal.
        Assert.True(live.DecidedAt >= live.RequestedAt);
        Assert.True(headless.DecidedAt >= headless.RequestedAt);
        Assert.True(live.CreatedAt >= live.DecidedAt);
        Assert.True(headless.CreatedAt >= headless.DecidedAt);

        Assert.Equal(ToolGateSurface.Interactive, live.Surface);
        Assert.Equal(ToolGateSurface.Unattended, headless.Surface);
    }

    /// <summary>Routing missed, so no gate was consulted and there is no time-to-answer; <c>CallId</c> is provider-authored on
    /// every arm and survives anyway.</summary>
    [Fact]
    public async Task AnUnroutedCallRecordsNoGateTiming_ButStillRecordsTheCallId()
    {
        var live = await RecordLiveAsync(policy: null, BuiltInPluginDefaults.FilesPluginId, pending: null);
        var headless = await RecordHeadlessAsync(policy: null, pending: null);

        Assert.Equal(ToolGateDecision.UnknownTool, live.Decision);
        Assert.Null(live.RequestedAt);
        Assert.Null(live.DecidedAt);
        Assert.Null(headless.RequestedAt);
        Assert.Null(headless.DecidedAt);

        Assert.Equal("call-1", live.ToolCallId);
        Assert.Equal("call-1", headless.ToolCallId);
        Assert.Equal(1, live.Round);
        Assert.Equal(1, headless.Round);
    }

    [Theory]
    // The positive half matters: a charset too tight silently nulls the column for a whole provider's users.
    [InlineData("call_abc123", "call_abc123")]            // OpenAI
    [InlineData("toolu_01ABCdefGHIjkl", "toolu_01ABCdefGHIjkl")] // Anthropic
    [InlineData("chatcmpl-tool-9f2", "chatcmpl-tool-9f2")]
    [InlineData("f47ac10b-58cc-4372-a567-0e02b2c3d479", "f47ac10b-58cc-4372-a567-0e02b2c3d479")] // bare GUID
    [InlineData("mcp.github:call.7", "mcp.github:call.7")]
    // Anything else becomes NULL, not a sentinel: the column is nullable and null means "no usable id".
    [InlineData("""{"path":"C:/Users/marco/Therapy notes.md"}""", null)]
    [InlineData("call 1", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void SanitizeCallIdKeepsProviderIdsAndNullsEverythingElse(string? input, string? expected)
    {
        Assert.Equal(expected, AgentTimelineScope.SanitizeCallId(input));
    }

    [Fact]
    public void SanitizeCallIdBoundsTheLength()
    {
        // Nothing validates a provider-supplied CallId, so an unbounded one is an audit-table growth vector.
        Assert.Null(AgentTimelineScope.SanitizeCallId(new string('a', 129)));
        Assert.Equal(new string('a', 128), AgentTimelineScope.SanitizeCallId(new string('a', 128)));
    }

    /// <summary>On the unrouted arm the tool name is model-authored, so it is the one arm where a malformed call can break the
    /// column's "never an argument, never a path" contract.</summary>
    [Fact]
    public async Task AnUnroutedModelAuthoredToolNameIsSanitizedOnBothSurfaces()
    {
        // A provider surfaces the function name verbatim, so concatenated arguments reach ToolName unsanitized.
        const string Malformed = """read_file{"path":"C:/Users/marco/Therapy notes.md"}""";

        var live = await RecordLiveAsync(policy: null, BuiltInPluginDefaults.FilesPluginId,
            pending: null, toolName: Malformed);
        var headless = await RecordHeadlessAsync(policy: null, pending: null, toolName: Malformed);

        Assert.Equal(ToolGateDecision.UnknownTool, live.Decision);
        Assert.Equal(live.Decision, headless.Decision);

        Assert.Equal("(unnamed)", live.ToolName);
        Assert.Equal("(unnamed)", headless.ToolName);
        Assert.DoesNotContain("Therapy", live.ToolName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Therapy", headless.ToolName, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("write_file", "write_file")]
    [InlineData("mcp.github:create-issue", "mcp.github:create-issue")]
    [InlineData("""read_file{"path":"C:/x.md"}""", "(unnamed)")]
    [InlineData("tool with spaces", "(unnamed)")]
    [InlineData("", "(unnamed)")]
    [InlineData(null, "(unnamed)")]
    public void SanitizeKeepsIdentifiersAndRejectsEverythingElse(string? input, string expected)
    {
        Assert.Equal(expected, AgentTimelineScope.SanitizeUnroutedToolName(input));
    }

    [Fact]
    public void SanitizeBoundsTheLength()
    {
        // An unbounded model-authored string is an audit-table size hazard.
        Assert.Equal("(unnamed)", AgentTimelineScope.SanitizeUnroutedToolName(new string('a', 65)));
        Assert.Equal(new string('a', 64), AgentTimelineScope.SanitizeUnroutedToolName(new string('a', 64)));
    }

    private static async Task<AgentTimelineEvent> RecordLiveAsync(
        RunAutonomyPolicy? policy, Guid filesId, PluginToolCall? pending = null, string toolName = "write_file")
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
        cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>())
            .Returns(new ActionCardInfo
            {
                Title = "write_file", Summary = "write_file",
                Category = ActionCardCategory.Files, ToolName = "write_file", PluginId = filesId,
            });
        plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(Route(pending));
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(),
                Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(), contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci => Drive(ci.ArgAt<ToolCallHandler?>(3), toolName));

        var session = new ChatSession(
            Substitute.For<ITokenMapService>(), ai, plugins, cards, permissions, loc, NullLogger.Instance, _ => true);
        var runId = Guid.NewGuid();
        var result = await session.RunStepTurnAsync(
            new StepTurnSpec(
                RunId: runId, Ordinal: 0, Intent: "write it", ExpectedArtifact: null, SystemPrompt: "system",
                Persona: new PersonaAttribution(Guid.NewGuid(), "Pia", "🤖"), Provider: Provider(),
                Tools: new List<AITool>(), SupportsTools: true, WebSearchActive: false, TokenizationEnabled: false,
                Policy: policy,
                Timeline: new AgentTimelineScope(timeline, runId, null)),
            new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        return Assert.Single(timeline.Rows);
    }

    private static async Task<AgentTimelineEvent> RecordHeadlessAsync(
        RunAutonomyPolicy? policy, PluginToolCall? pending = null, string toolName = "write_file")
    {
        var timeline = new RecordingTimelineService();
        var ai = Substitute.For<IAiClientService>();
        var plugins = Substitute.For<IPluginService>();
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(Route(pending));
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => Drive(ci.ArgAt<ToolCallHandler?>(3), toolName));

        ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();
        var runner = new BackgroundAssistantTurnRunner(
            ai, plugins, Substitute.For<IToolPermissionService>(),
            Substitute.For<IAssistantPromptComposer>(), Substitute.For<IPersonaService>(),
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

    /// <summary>A null pending action is a route MISS (the unrouted arm); otherwise a deferred write.</summary>
    private static (object? Result, PluginToolCall? PendingAction)? Route(PluginToolCall? pending) =>
        pending is null ? null : ((object?)null, pending);

    private static AiProvider Provider() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test",
        Endpoint = "http://localhost",
        ProviderType = AiProviderType.OpenAI,
        TimeoutSeconds = 60,
    };

    private static async IAsyncEnumerable<ChatStreamItem> Drive(
        ToolCallHandler? handler, string toolName)
    {
        if (handler is not null)
            await handler(new FunctionCallContent("call-1", toolName, new Dictionary<string, object?> { ["path"] = "a.md" }), new ToolDispatchContext(1));

        yield return new TextDelta("Done.");
        yield return new Finished(null, "test-model");
    }
}

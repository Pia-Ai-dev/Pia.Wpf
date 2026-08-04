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

        var live = await RecordLiveAsync(policy, filesId, Pending(filesId));
        var headless = await RecordHeadlessAsync(policy, Pending(filesId));

        Assert.Equal(ToolGateDecision.AutoApprovedPolicy, live.Decision);
        Assert.Equal(live.Decision, headless.Decision);
        Assert.Equal(live.Outcome, headless.Outcome);
        Assert.Equal(live.ToolName, headless.ToolName);
        Assert.Equal(live.ToolClass, headless.ToolClass);
        Assert.Equal(live.Kind, headless.Kind);
        Assert.Equal(live.ResultChars, headless.ResultChars);

        // ---- T2-14: the five correlation columns are parity facts too. A column populated on one surface and
        // NULL on the other is a silent bug, and the only thing that catches it is asserting BOTH here.
        // The provider's call id (the Drive helpers both dispatch `new FunctionCallContent("call-1", …)`) —
        // recorded on both surfaces, not just the unrouted one.
        Assert.Equal("call-1", live.ToolCallId);
        Assert.Equal(live.ToolCallId, headless.ToolCallId);
        // 1-based: both Drive helpers stand in for the loop's FIRST round, and both gates read it off the
        // dispatch context rather than counting anything themselves.
        Assert.Equal(1, live.Round);
        Assert.Equal(live.Round, headless.Round);
        // A policy-approved call HAS been asked and answered on both surfaces, so neither instant is null.
        Assert.NotNull(live.RequestedAt);
        Assert.NotNull(live.DecidedAt);
        Assert.NotNull(headless.RequestedAt);
        Assert.NotNull(headless.DecidedAt);
        // >=, never >: both gates bracket a ToolAutonomy.Resolve call that takes far less than
        // DateTime.UtcNow's ~1 ms resolution, so the two instants are normally EQUAL.
        Assert.True(live.DecidedAt >= live.RequestedAt);
        Assert.True(headless.DecidedAt >= headless.RequestedAt);
        Assert.True(live.CreatedAt >= live.DecidedAt);
        Assert.True(headless.CreatedAt >= headless.DecidedAt);

        // …and the one thing that MUST differ.
        Assert.Equal(ToolGateSurface.Interactive, live.Surface);
        Assert.Equal(ToolGateSurface.Unattended, headless.Surface);
    }

    /// <summary>
    /// The unrouted arm's stamps, on BOTH surfaces: NULL/NULL, because routing missed and NO gate was ever
    /// consulted — there was no question, so there is no time-to-answer. The CALL ID is still recorded, which
    /// is the difference from <c>ToolName</c>: <c>CallId</c> is provider-authored on every arm.
    /// </summary>
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
    // Real provider shapes survive UNMODIFIED. Both halves of this theory matter: a charset too TIGHT silently
    // nulls the column for every user of that provider, which no "it rejects junk" test would notice.
    [InlineData("call_abc123", "call_abc123")]            // OpenAI
    [InlineData("toolu_01ABCdefGHIjkl", "toolu_01ABCdefGHIjkl")] // Anthropic
    [InlineData("chatcmpl-tool-9f2", "chatcmpl-tool-9f2")]
    [InlineData("f47ac10b-58cc-4372-a567-0e02b2c3d479", "f47ac10b-58cc-4372-a567-0e02b2c3d479")] // bare GUID
    [InlineData("mcp.github:call.7", "mcp.github:call.7")]
    // …and anything outside a correlation-id shape becomes NULL, not a sentinel: the column is nullable and
    // "no usable correlation id" is exactly what null means there.
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
        // Same size hazard as the tool name: nothing in this process validates a provider-supplied CallId, so
        // an unbounded one is an audit-table growth vector. 128 is generous against every real shape above.
        Assert.Null(AgentTimelineScope.SanitizeCallId(new string('a', 129)));
        Assert.Equal(new string('a', 128), AgentTimelineScope.SanitizeCallId(new string('a', 128)));
    }

    /// <summary>
    /// The unrouted arm, on BOTH surfaces. The tool name is the one string on this arm that the MODEL authored,
    /// so it is the one arm where the column's own contract ("never an argument, never a result, never a path")
    /// can be broken by a malformed call — and it is the arm the canary sweep in
    /// <c>AgentTimelinePrivacyTests</c> cannot reach, because that one drives a ROUTED write_file.
    /// </summary>
    [Fact]
    public async Task AnUnroutedModelAuthoredToolNameIsSanitizedOnBothSurfaces()
    {
        // A provider surfaces the raw function name verbatim; a model that concatenates its arguments into it
        // would otherwise put a user path straight into ToolName.
        const string Malformed = """read_file{"path":"C:/Users/marco/Therapy notes.md"}""";

        var live = await RecordLiveAsync(policy: null, BuiltInPluginDefaults.FilesPluginId,
            pending: null, toolName: Malformed);
        var headless = await RecordHeadlessAsync(policy: null, pending: null, toolName: Malformed);

        Assert.Equal(ToolGateDecision.UnknownTool, live.Decision);
        Assert.Equal(live.Decision, headless.Decision);

        // Parity, and the invariant: neither surface persists the path.
        Assert.Equal("(unnamed)", live.ToolName);
        Assert.Equal("(unnamed)", headless.ToolName);
        Assert.DoesNotContain("Therapy", live.ToolName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Therapy", headless.ToolName, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // A real tool identifier survives untouched — including the shapes an MCP server produces.
    [InlineData("write_file", "write_file")]
    [InlineData("mcp.github:create-issue", "mcp.github:create-issue")]
    // …and everything that is not one becomes the sentinel.
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
        // The other half of the finding: an unbounded model-authored string is also an audit-table size hazard
        // (ten calls with 100 KB names each).
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
        // A null pending action means the route MISSED — the unrouted arm — which is why this is a parameter
        // rather than a fixed stub.
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

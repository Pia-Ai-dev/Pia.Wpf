using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.Tests.Services;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Batch 03 at the INTERACTIVE gate. The audit sink arrives on <c>StepTurnSpec.Timeline</c>, so these facts
/// drive <c>RunStepTurnAsync</c> — the Planned-run path — rather than the ordinary <c>RunTurnAsync</c> turn,
/// which has no run and therefore records nothing. That is why every fact in
/// <see cref="ChatSessionStateMachineTests"/> still holds unchanged.
/// <para>
/// Cards are answered BEFORE the turn starts. <c>ActionCardInfo</c>'s decision commands complete a
/// <see cref="TaskCompletionSource{TResult}"/> and are idempotent on a non-pending card, so a pre-resolved
/// card makes <c>WaitForUserDecisionAsync</c> return immediately — which keeps these facts free of wall-clock
/// polling.
/// </para>
/// </summary>
public sealed class ChatSessionTimelineTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();
    private readonly RecordingTimelineService _timeline = new();

    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _stepId = Guid.NewGuid();

    public ChatSessionTimelineTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        _cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Done");
    }

    // ---- T-EMIT-1: the batch's acceptance test, restated for GATED calls ----

    [Fact]
    public async Task NGatedToolCalls_SomeDenied_ProduceNOrderedEvents_WithTheRightDecisions()
    {
        var todoId = BuiltInPluginDefaults.TodoPluginId;
        var reminderId = BuiltInPluginDefaults.ReminderPluginId;
        var filesId = BuiltInPluginDefaults.FilesPluginId;

        // 1) allowlisted AND granted → auto-approved on the standing grant, no card interaction.
        _permissions.IsAutoApproveEligible("create_todo").Returns(true);
        _permissions.IsGranted(todoId, "create_todo").Returns(true);
        // 2) allowlisted, not granted → card; the user clicks "Allow once".
        _permissions.IsAutoApproveEligible("append_to_list").Returns(true);
        // 3) allowlisted, not granted → card; the user clicks "Always allow".
        _permissions.IsAutoApproveEligible("create_reminder").Returns(true);
        // 4) not allowlisted, not granted → card; the user declines.
        _permissions.IsAutoApproveEligible("write_file").Returns(false);

        var pendings = new Dictionary<string, PluginToolCall>(StringComparer.Ordinal)
        {
            ["create_todo"] = Pending("create_todo", "todo", todoId),
            ["append_to_list"] = Pending("append_to_list", "todo", todoId),
            ["create_reminder"] = Pending("create_reminder", "reminder", reminderId),
            ["write_file"] = Pending("write_file", "files", filesId),
        };
        var cards = pendings.ToDictionary(p => p.Key, p => NewCard(p.Key, p.Value.PluginId), StringComparer.Ordinal);

        // Pre-answer the three cards that need an answer.
        cards["append_to_list"].AllowOnceCommand.Execute(null);
        cards["create_reminder"].AlwaysAllowCommand.Execute(null);
        cards["write_file"].DeclineCommand.Execute(null);

        ArrangeRoutes(pendings, cards);
        ArrangeStream(["create_todo", "append_to_list", "create_reminder", "write_file"]);

        var result = await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);

        var rows = _timeline.Rows;
        Assert.Equal(4, rows.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, rows.Select(r => r.Seq).ToArray());
        Assert.Equal(
            new[] { "create_todo", "append_to_list", "create_reminder", "write_file" },
            rows.Select(r => r.ToolName).ToArray());
        Assert.Equal(
            new[]
            {
                ToolGateDecision.AutoApprovedStandingGrant,
                ToolGateDecision.ApprovedOnce,
                ToolGateDecision.ApprovedAlways,
                ToolGateDecision.DeclinedByUser,
            },
            rows.Select(r => r.Decision).ToArray());
        Assert.Equal(
            new[]
            {
                AgentTimelineOutcome.Ok, AgentTimelineOutcome.Ok, AgentTimelineOutcome.Ok,
                AgentTimelineOutcome.NotExecuted,
            },
            rows.Select(r => r.Outcome).ToArray());
        Assert.All(rows, r =>
        {
            Assert.Equal(ToolGateSurface.Interactive, r.Surface);
            Assert.Equal(AgentTimelineEventKind.ToolCall, r.Kind);
            Assert.Equal(_runId, r.RunId);
        });
        // The class comes from the same classifier the gate resolved with.
        Assert.Equal(ToolClass.Todo, rows[0].ToolClass);
        Assert.Equal(ToolClass.Reminder, rows[2].ToolClass);
        Assert.Equal(ToolClass.Files, rows[3].ToolClass);
    }

    // ---- T-EMIT-2: reads emit nothing. Paired with the fact above, which proves the same path DOES emit. ----

    [Fact]
    public async Task ReadsEmitNothing()
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)"here are your files", (PluginToolCall?)null));
        ArrangeStream(["list_files"]);

        var result = await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        // The read path really ran — otherwise "no rows" would be true of a turn where nothing happened.
        await _plugins.Received().RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>());
        Assert.Empty(_timeline.Rows);
    }

    // ---- T-EMIT-3 ----

    [Fact]
    public async Task EveryEventCarriesTheStepId()
    {
        ArrangeAutoApproved("create_todo", BuiltInPluginDefaults.TodoPluginId, "todo");
        ArrangeStream(["create_todo", "create_todo"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.Equal(2, _timeline.Rows.Count);
        Assert.All(_timeline.Rows, r => Assert.Equal(_stepId, r.StepId));
    }

    [Fact]
    public async Task AnOrdinaryChatTurnRecordsNothing()
    {
        // Control for the fact above: the identical arrangement through RunTurnAsync — no spec, no scope — is
        // silent, which is the whole opt-in mechanism. The "it ran" probe is asserted so that a stub that
        // stops matching RunTurnAsync's call shape cannot make this pass for free.
        var ran = ArrangeAutoApproved("create_todo", BuiltInPluginDefaults.TodoPluginId, "todo");
        ArrangeStream(["create_todo"]);

        var session = CreateSession();
        await session.RunTurnAsync(ToolRequest(session), CancellationToken.None);

        Assert.True(ran(), "the gated tool must have run on the ordinary turn path");
        Assert.Empty(_timeline.Rows);
    }

    // ---- T-EMIT-4 ----

    [Fact]
    public async Task ACancelledCardIsRecordedAsCancelled_NotAsAUserDenial()
    {
        var filesId = BuiltInPluginDefaults.FilesPluginId;
        _permissions.IsAutoApproveEligible("write_file").Returns(false);
        var pending = Pending("write_file", "files", filesId);
        var card = NewCard("write_file", filesId);
        // Cancel (new chat / retry / scope dispose) — the gate maps this to ToolDecision.Decline internally,
        // and recording THAT as "the user declined" would be a false audit statement.
        card.CancelCommand.Execute(null);

        ArrangeRoutes(new() { ["write_file"] = pending }, new() { ["write_file"] = card });
        ArrangeStream(["write_file"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        var row = Assert.Single(_timeline.Rows);
        Assert.Equal(ToolGateDecision.CardCancelled, row.Decision);
        Assert.NotEqual(ToolGateDecision.DeclinedByUser, row.Decision);
        Assert.Equal(AgentTimelineOutcome.NotExecuted, row.Outcome);
    }

    // ---- T-EMIT-5 ----

    [Fact]
    public async Task AThrowingToolIsRecordedAsError_AndTheExceptionStillPropagates()
    {
        var todoId = BuiltInPluginDefaults.TodoPluginId;
        _permissions.IsAutoApproveEligible("create_todo").Returns(true);
        _permissions.IsGranted(todoId, "create_todo").Returns(true);

        var pending = new PluginToolCall("create_todo", todoId, "todo", "Create a todo", null,
            () => throw new InvalidOperationException("the tool blew up"));
        var card = NewCard("create_todo", todoId);
        ArrangeRoutes(new() { ["create_todo"] = pending }, new() { ["create_todo"] = card });
        ArrangeStream(["create_todo"]);

        var result = await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        var row = Assert.Single(_timeline.Rows);
        Assert.Equal(AgentTimelineOutcome.Error, row.Outcome);
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, row.Decision); // still says WHY it was allowed
        Assert.Null(row.ResultChars);
        // …and the throw still reaches the step, unchanged by this batch: a swallow here would make the step
        // succeed.
        Assert.False(result.Succeeded);
        Assert.Contains("the tool blew up", result.Error);
    }

    // ---- T-EMIT-6 ----

    [Fact]
    public async Task AnUnknownToolIsRecorded()
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?, PluginToolCall?)?)null);
        ArrangeStream(["no_such_tool"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        var row = Assert.Single(_timeline.Rows);
        Assert.Equal(ToolGateDecision.UnknownTool, row.Decision);
        Assert.Equal(AgentTimelineOutcome.NotExecuted, row.Outcome);
        Assert.Equal("no_such_tool", row.ToolName);
        Assert.Equal(ToolClass.Unknown, row.ToolClass);
        Assert.Null(row.PluginId);
    }

    [Fact]
    public async Task TheAgentModeSuggestionShortCircuitIsNotATimelineEvent()
    {
        // suggest_agent_mode returns before RouteToolCallAsync, so it is neither a gated call nor an unknown
        // tool. Pinned so a future "emit for every unrouted name" does not start recording it.
        ArrangeStream(["suggest_agent_mode"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.Empty(_timeline.Rows);
        await _plugins.DidNotReceive().RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>());
    }

    // ---- T-EMIT-7: the failure-isolation guardrail, executable ----

    [Fact]
    public async Task AFailingTimelineServiceDoesNotFailTheStep()
    {
        _timeline.ThrowOnEmit = true;
        var executed = ArrangeAutoApproved("create_todo", BuiltInPluginDefaults.TodoPluginId, "todo");
        ArrangeStream(["create_todo"]);

        var result = await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(executed(), "the tool must still have run");
        Assert.Empty(_timeline.Rows);
    }

    // ---- T-PRIV-3 (the half this suite can prove) ----

    [Fact]
    public async Task ArgsAndResultCharsAreLengthsOfWhatTheHandlerSaw()
    {
        var todoId = BuiltInPluginDefaults.TodoPluginId;
        _permissions.IsAutoApproveEligible("create_todo").Returns(true);
        _permissions.IsGranted(todoId, "create_todo").Returns(true);

        const string resultText = "created todo 42 in the salary folder";
        var pending = new PluginToolCall("create_todo", todoId, "todo", "Create a todo", null,
            () => Task.FromResult<object?>(resultText));
        ArrangeRoutes(new() { ["create_todo"] = pending }, new() { ["create_todo"] = NewCard("create_todo", todoId) });

        var args = new Dictionary<string, object?> { ["title"] = "CANARY-9f3a1c" };
        ArrangeStream(["create_todo"], args);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        var row = Assert.Single(_timeline.Rows);
        // The emit sits INSIDE the handler that TokenizingAiClientService.WrapToolHandler decorates, so these
        // are the pre-tokenization lengths (the wrapper's detokenize-in / tokenize-out ordering is pinned by
        // TokenizingAiClientServiceTests). Lengths only — never the text.
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(args).Length, row.ArgsChars);
        Assert.Equal(resultText.Length, row.ResultChars);
        Assert.NotNull(row.DurationMs);
    }

    // ---- harness ----

    private ChatSession CreateSession() => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => true);

    private StepTurnSpec Spec() => new(
        RunId: _runId,
        Ordinal: 0,
        Intent: "do the thing",
        ExpectedArtifact: null,
        SystemPrompt: "system",
        Persona: new PersonaAttribution(Guid.NewGuid(), "Pia", "🤖"),
        Provider: TestProvider,
        Tools: new List<AITool>(),
        SupportsTools: true,
        WebSearchActive: false,
        TokenizationEnabled: false,
        // The SCOPE carries the step id; the record has no StepId field of its own (it was written by one
        // executor and read by nobody, so a spec-level value could silently disagree with the scope's).
        Timeline: new AgentTimelineScope(_timeline, _runId, _stepId));

    private static AiProvider TestProvider => new()
    {
        Name = "Test",
        Endpoint = "http://localhost",
        ProviderType = AiProviderType.OpenAI,
    };

    private ChatTurnRequest ToolRequest(ChatSession session)
    {
        var user = new AssistantMessage(ChatRole.User, "hi");
        var assistant = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };
        session.Messages.Add(user);
        session.Messages.Add(assistant);
        return new ChatTurnRequest
        {
            UserMessage = user,
            AssistantMessage = assistant,
            Provider = TestProvider,
            TurnSetup = new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false),
            AtCommands = [],
            TokenizationEnabled = false,
        };
    }

    private static PluginToolCall Pending(string toolName, string pluginName, Guid pluginId) =>
        new(toolName, pluginId, pluginName, $"{pluginName}: {toolName}", null,
            () => Task.FromResult<object?>("done"));

    private static ActionCardInfo NewCard(string toolName, Guid pluginId) => new()
    {
        Title = toolName,
        Summary = toolName,
        Category = ActionCardCategory.Todo,
        ToolName = toolName,
        PluginId = pluginId,
    };

    /// <summary>Arranges one auto-approved (allowlisted + granted) tool and returns an "it ran" probe.</summary>
    private Func<bool> ArrangeAutoApproved(string toolName, Guid pluginId, string pluginName)
    {
        var ran = false;
        _permissions.IsAutoApproveEligible(toolName).Returns(true);
        _permissions.IsGranted(pluginId, toolName).Returns(true);
        var pending = new PluginToolCall(toolName, pluginId, pluginName, $"{pluginName}: {toolName}", null,
            () => { ran = true; return Task.FromResult<object?>("done"); });
        ArrangeRoutes(
            new Dictionary<string, PluginToolCall>(StringComparer.Ordinal) { [toolName] = pending },
            new Dictionary<string, ActionCardInfo>(StringComparer.Ordinal) { [toolName] = NewCard(toolName, pluginId) });
        return () => ran;
    }

    private void ArrangeRoutes(
        Dictionary<string, PluginToolCall> pendings, Dictionary<string, ActionCardInfo> cards)
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var name = ci.ArgAt<FunctionCallContent>(0).Name;
                return pendings.TryGetValue(name, out var p)
                    ? ((object?)null, (PluginToolCall?)p)
                    : null;
            });
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>())
            .Returns(ci => cards[ci.ArgAt<PluginToolCall>(0).ToolName]);
    }

    /// <summary>Drives the handler once per tool name, in order, then closes the stream.</summary>
    private void ArrangeStream(string[] toolNames, IDictionary<string, object?>? arguments = null)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(),
                Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(), contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci => Stream(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3), toolNames, arguments));
    }

    private static async IAsyncEnumerable<ChatStreamItem> Stream(
        Func<FunctionCallContent, Task<object?>>? handler, string[] toolNames, IDictionary<string, object?>? arguments)
    {
        if (handler is not null)
        {
            var i = 0;
            foreach (var name in toolNames)
            {
                await handler(new FunctionCallContent(
                    $"call-{i++}", name, arguments ?? new Dictionary<string, object?>()));
            }
        }

        yield return new TextDelta("Done.");
        await Task.Yield();
    }
}

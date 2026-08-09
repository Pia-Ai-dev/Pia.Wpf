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

// Cards are answered before the turn starts: a pre-resolved card makes WaitForUserDecisionAsync return
// immediately, which keeps these facts free of wall-clock polling.
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

    [Fact]
    public async Task NGatedToolCalls_SomeDenied_ProduceNOrderedEvents_WithTheRightDecisions()
    {
        var todoId = BuiltInPluginDefaults.TodoPluginId;
        var reminderId = BuiltInPluginDefaults.ReminderPluginId;
        var filesId = BuiltInPluginDefaults.FilesPluginId;

        // Allowlisted and granted: auto-approved on the standing grant, with no card at all.
        _permissions.IsAutoApproveEligible("create_todo").Returns(true);
        _permissions.IsGranted(todoId, "create_todo").Returns(true);
        // Allowlisted but not granted, so each of these prompts a card.
        _permissions.IsAutoApproveEligible("append_to_list").Returns(true);
        _permissions.IsAutoApproveEligible("create_reminder").Returns(true);
        _permissions.IsAutoApproveEligible("write_file").Returns(false);

        var pendings = new Dictionary<string, PluginToolCall>(StringComparer.Ordinal)
        {
            ["create_todo"] = Pending("create_todo", "todo", todoId),
            ["append_to_list"] = Pending("append_to_list", "todo", todoId),
            ["create_reminder"] = Pending("create_reminder", "reminder", reminderId),
            ["write_file"] = Pending("write_file", "files", filesId),
        };
        var cards = pendings.ToDictionary(p => p.Key, p => NewCard(p.Key, p.Value.PluginId), StringComparer.Ordinal);

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
        Assert.Equal(ToolClass.Todo, rows[0].ToolClass);
        Assert.Equal(ToolClass.Reminder, rows[2].ToolClass);
        Assert.Equal(ToolClass.Files, rows[3].ToolClass);
    }

    [Fact]
    public async Task ReadsEmitNothing()
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)"here are your files", (PluginToolCall?)null));
        ArrangeStream(["list_files"]);

        var result = await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        // Without this, "no rows" would also be true of a turn where nothing happened.
        await _plugins.Received().RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>());
        Assert.Empty(_timeline.Rows);
    }

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
        // The same arrangement through RunTurnAsync has no spec and no scope, so recording is opt-in and silent here.
        var ran = ArrangeAutoApproved("create_todo", BuiltInPluginDefaults.TodoPluginId, "todo");
        ArrangeStream(["create_todo"]);

        var session = CreateSession();
        await session.RunTurnAsync(ToolRequest(session), CancellationToken.None);

        Assert.True(ran(), "the gated tool must have run on the ordinary turn path");
        Assert.Empty(_timeline.Rows);
    }

    [Fact]
    public async Task ACancelledCardIsRecordedAsCancelled_NotAsAUserDenial()
    {
        var filesId = BuiltInPluginDefaults.FilesPluginId;
        _permissions.IsAutoApproveEligible("write_file").Returns(false);
        var pending = Pending("write_file", "files", filesId);
        var card = NewCard("write_file", filesId);
        // The gate maps a cancel to ToolDecision.Decline internally; auditing that as a user denial would be a lie.
        card.CancelCommand.Execute(null);

        ArrangeRoutes(new() { ["write_file"] = pending }, new() { ["write_file"] = card });
        ArrangeStream(["write_file"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        var row = Assert.Single(_timeline.Rows);
        Assert.Equal(ToolGateDecision.CardCancelled, row.Decision);
        Assert.NotEqual(ToolGateDecision.DeclinedByUser, row.Decision);
        Assert.Equal(AgentTimelineOutcome.NotExecuted, row.Outcome);

        // The cancel arrives as a TaskCanceledException, so a DecidedAt assigned only after a successful await
        // would be null here and the row would read "still pending" for a question that is over.
        Assert.NotNull(row.RequestedAt);
        Assert.NotNull(row.DecidedAt);
        Assert.True(row.DecidedAt >= row.RequestedAt);
        Assert.True(row.CreatedAt >= row.DecidedAt);
    }

    // Ordering is asserted with <=, never <: the pre-resolved card makes the two instants normally equal.
    [Fact]
    public async Task APromptedCardRecordsWhenItWasShownAndWhenItWasAnswered()
    {
        var filesId = BuiltInPluginDefaults.FilesPluginId;
        _permissions.IsAutoApproveEligible("write_file").Returns(false);
        var pending = Pending("write_file", "files", filesId);
        var card = NewCard("write_file", filesId);
        card.AllowOnceCommand.Execute(null);

        ArrangeRoutes(new() { ["write_file"] = pending }, new() { ["write_file"] = card });
        ArrangeStream(["write_file"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        var row = Assert.Single(_timeline.Rows);
        Assert.Equal(ToolGateDecision.ApprovedOnce, row.Decision); // the prompted arm, not the bypass one
        Assert.NotNull(row.RequestedAt);
        Assert.NotNull(row.DecidedAt);
        Assert.True(row.DecidedAt >= row.RequestedAt);
        Assert.True(row.CreatedAt >= row.DecidedAt);
        Assert.Equal("call-0", row.ToolCallId);
        Assert.Equal(1, row.Round);
    }

    // Comparing the pair to itself does not discriminate: the anchor is a third instant taken as the card is built,
    // provably after the policy resolver answered and before the card was shown.
    [Fact]
    public async Task APromptedCardsInstantsStraddleTheHumansAnswer_NotThePolicyResolver()
    {
        var filesId = BuiltInPluginDefaults.FilesPluginId;
        _permissions.IsAutoApproveEligible("write_file").Returns(false);
        var pending = Pending("write_file", "files", filesId);
        var card = NewCard("write_file", filesId);

        var afterResolve = ArrangeLateAnsweredCard(pending, card, c => c.DeclineCommand.Execute(null));
        ArrangeStream(["write_file"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        var row = Assert.Single(_timeline.Rows);
        Assert.Equal(ToolGateDecision.DeclinedByUser, row.Decision);
        Assert.NotNull(row.RequestedAt);
        Assert.NotNull(row.DecidedAt);

        Assert.True(
            row.RequestedAt > afterResolve(),
            $"RequestedAt must be the instant the CARD was shown, not the policy resolver's; got {row.RequestedAt:O} vs anchor {afterResolve():O}");
        Assert.True(
            row.DecidedAt > afterResolve(),
            $"DecidedAt must be the instant the HUMAN answered; got {row.DecidedAt:O} vs anchor {afterResolve():O}");
        Assert.True(row.DecidedAt >= row.RequestedAt);
        Assert.True(row.CreatedAt >= row.DecidedAt);
    }

    // Milliseconds the card build is held open, well past DateTime.UtcNow's resolution, so the ordering is caused
    // rather than raced for.
    private const int GapMs = 60;

    // Returns a getter for the anchor instant, captured as the gate builds the card.
    private Func<DateTime> ArrangeLateAnsweredCard(
        PluginToolCall pending, ActionCardInfo card, Action<ActionCardInfo> answer)
    {
        var anchor = DateTime.MinValue;
        ArrangeRoutes(
            new Dictionary<string, PluginToolCall>(StringComparer.Ordinal) { [pending.ToolName] = pending },
            new Dictionary<string, ActionCardInfo>(StringComparer.Ordinal) { [pending.ToolName] = card },
            onCardBuilt: built =>
            {
                anchor = DateTime.UtcNow;
                // Held synchronously so cardShownAt, taken right after Build returns, is forced past the anchor.
                Thread.Sleep(GapMs);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(GapMs);
                    answer(built);
                });
            });
        return () => anchor;
    }

    // The accept arm emits through the same local function the auto-run bypass calls, but with the other pair.
    [Fact]
    public async Task APromptedAcceptStampsTheCardsInstants_NotThePolicyResolvers()
    {
        var filesId = BuiltInPluginDefaults.FilesPluginId;
        _permissions.IsAutoApproveEligible("write_file").Returns(false);
        var pending = Pending("write_file", "files", filesId);
        var card = NewCard("write_file", filesId);

        var afterResolve = ArrangeLateAnsweredCard(pending, card, c => c.AllowOnceCommand.Execute(null));
        ArrangeStream(["write_file"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        var row = Assert.Single(_timeline.Rows);
        Assert.Equal(ToolGateDecision.ApprovedOnce, row.Decision);
        Assert.Equal(AgentTimelineOutcome.Ok, row.Outcome);
        Assert.True(
            row.RequestedAt > afterResolve(),
            $"ExecuteAndReport must receive the CARD's pair on this arm, not the resolver's; got RequestedAt={row.RequestedAt:O} vs anchor {afterResolve():O}");
        Assert.True(
            row.DecidedAt > afterResolve(),
            $"ExecuteAndReport must receive the CARD's pair on this arm, not the resolver's; got DecidedAt={row.DecidedAt:O} vs anchor {afterResolve():O}");
        Assert.True(row.DecidedAt >= row.RequestedAt);
    }

    // The bypass renders a resolved card too, so "use cardShownAt everywhere" would still produce plausible stamps.
    [Fact]
    public async Task TheAutoRunBypassStampsThePolicyResolversInstants()
    {
        var todoId = BuiltInPluginDefaults.TodoPluginId;
        var ran = ArrangeAutoApproved("create_todo", todoId, "todo");
        ArrangeStream(["create_todo"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(ran());
        var row = Assert.Single(_timeline.Rows);
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, row.Decision);
        Assert.NotNull(row.RequestedAt);
        Assert.NotNull(row.DecidedAt);
        // >=, not >: this pair brackets a few comparisons and is normally equal.
        Assert.True(row.DecidedAt >= row.RequestedAt);
        Assert.True(row.CreatedAt >= row.DecidedAt);
    }

    // Vacuous on its own; AStepTurnsRowsCarryAPerStepOrdinal reads a non-null ordinal off the same sink.
    [Fact]
    public async Task ARunLevelTurnRecordsNoStepOrdinal()
    {
        var ran = ArrangeAutoApproved("create_todo", BuiltInPluginDefaults.TodoPluginId, "todo");
        ArrangeStream(["create_todo"]);

        await CreateSession().RunStepTurnAsync(
            Spec(runLevel: true), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(ran());
        var runLevel = Assert.Single(_timeline.Rows);
        Assert.Null(runLevel.StepId);
        Assert.Null(runLevel.StepOrdinal);
    }

    // Pins the fake sink's allocator, which every other ordinal assertion in this suite reads through.
    [Fact]
    public async Task AStepTurnsRowsCarryAPerStepOrdinal()
    {
        var ran = ArrangeAutoApproved("create_todo", BuiltInPluginDefaults.TodoPluginId, "todo");
        ArrangeStream(["create_todo", "create_todo"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(ran());
        var rows = _timeline.Rows;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(_stepId, r.StepId));
        Assert.Equal(new long?[] { 1, 2 }, rows.Select(r => r.StepOrdinal).ToArray());
    }

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
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, row.Decision);
        Assert.Null(row.ResultChars);
        // The throw must still reach the step: a swallow around the emit would make the step succeed.
        Assert.False(result.Succeeded);
        Assert.Contains("the tool blew up", result.Error);
    }

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
        // suggest_agent_mode returns before RouteToolCallAsync, so it is neither a gated call nor an unknown tool.
        ArrangeStream(["suggest_agent_mode"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.Empty(_timeline.Rows);
        await _plugins.DidNotReceive().RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>());
    }

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
        // The emit sits inside the handler the tokenizing wrapper decorates, so these are pre-tokenization lengths.
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(args).Length, row.ArgsChars);
        Assert.Equal(resultText.Length, row.ResultChars);
        Assert.NotNull(row.DurationMs);
    }

    // ---- harness ----

    private ChatSession CreateSession() => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => true);

    private StepTurnSpec Spec(bool runLevel = false) => new(
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
        // The scope carries the step id; the spec has no field of its own that could disagree with it.
        Timeline: new AgentTimelineScope(_timeline, _runId, runLevel ? null : _stepId));

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

    // Returns a probe for whether the tool actually ran.
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

    // onCardBuilt is the only hook that can act between the policy resolver answering and the card being shown.
    private void ArrangeRoutes(
        Dictionary<string, PluginToolCall> pendings, Dictionary<string, ActionCardInfo> cards,
        Action<ActionCardInfo>? onCardBuilt = null)
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
            .Returns(ci =>
            {
                var built = cards[ci.ArgAt<PluginToolCall>(0).ToolName];
                onCardBuilt?.Invoke(built);
                return built;
            });
    }

    private void ArrangeStream(string[] toolNames, IDictionary<string, object?>? arguments = null)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(),
                Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(), contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci => Stream(ci.ArgAt<ToolCallHandler?>(3), toolNames, arguments));
    }

    private static async IAsyncEnumerable<ChatStreamItem> Stream(
        ToolCallHandler? handler, string[] toolNames, IDictionary<string, object?>? arguments)
    {
        if (handler is not null)
        {
            var i = 0;
            foreach (var name in toolNames)
            {
                await handler(new FunctionCallContent(
                    $"call-{i++}", name, arguments ?? new Dictionary<string, object?>()), new ToolDispatchContext(1));
            }
        }

        yield return new TextDelta("Done.");
        await Task.Yield();
    }
}

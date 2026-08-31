using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

public sealed class ChatSessionStepTurnTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    // The session's log, so a fixture can assert on the compaction diff line.
    private readonly CapturingLogger<ChatSession> _log = new();

    public ChatSessionStepTurnTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
    }

    private ChatSession CreateSession() => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, _log, _ => true);

    private static StepTurnSpec Spec(
        bool tokenizationEnabled, AiProvider? provider = null, bool supportsTools = false,
        string? workspaceRoot = null, string? extraToolName = null) => new(
        RunId: Guid.NewGuid(),
        Ordinal: 0,
        Intent: "do the thing",
        ExpectedArtifact: "artifact",
        SystemPrompt: "system",
        Persona: new PersonaAttribution(Guid.NewGuid(), "Pia", "🤖"),
        Provider: provider
            ?? new AiProvider { Name = "Test", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
        Tools: StepTools(supportsTools, extraToolName),
        SupportsTools: supportsTools,
        WebSearchActive: false,
        TokenizationEnabled: tokenizationEnabled,
        WorkspaceRoot: workspaceRoot);

    private static IList<AITool>? StepTools(bool supportsTools, string? extraToolName)
    {
        if (!supportsTools)
            return null;

        var tools = new List<AITool> { AIFunctionFactory.Create(() => string.Empty, "noop") };
        if (extraToolName is not null)
            tools.Add(AIFunctionFactory.Create(() => string.Empty, extraToolName));
        return tools;
    }

    private void ReturnsStream(Func<IAsyncEnumerable<ChatStreamItem>> factory)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => factory());
    }

    private static async IAsyncEnumerable<ChatStreamItem> Stream(params ChatStreamItem[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ChatStreamItem> ThrowingStream(Exception ex, Action? beforeThrow = null)
    {
        await Task.Yield();
        beforeThrow?.Invoke();
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ChatStreamItem> ToolRoundStream(string answer, params ChatMessage[] roundMessages)
    {
        await Task.Yield();
        yield return new ToolRoundCompleted();
        yield return new ToolRoundExchange(1, roundMessages);
        yield return new TextDelta(answer);
        yield return new Finished(null, "m");
    }

    /// <summary>The live half of cross-step tool context. AssistantMessage.ToChatMessage() carries no tool
    /// content, so without the splice a foreground run's step 2 has only step 1's prose to work from.</summary>
    [Fact]
    public async Task AStepsToolResult_ReachesTheNextStepsRequest()
    {
        var carried = new ChatMessage[]
        {
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = "inventory.csv" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "SKU-1001,Blue Widget,4,10,3.50")]),
        };

        var captured = new List<List<ChatMessage>>();
        var turns = 0;
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured.Add([.. (IList<ChatMessage>)ci[0]]);
                return ++turns == 1 ? ToolRoundStream("read it", carried) : Stream(new TextDelta("wrote it"), new Finished(null, "m"));
            });

        var session = CreateSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "goal"));
        TaskAmbient.Current = null;
        TokenMapAmbient.Current = null;

        var ctx = new RunContext("goal", RunProfile.Interactive);
        await session.RunStepTurnAsync(Spec(tokenizationEnabled: false, supportsTools: true), ctx, CancellationToken.None);
        await session.RunStepTurnAsync(Spec(tokenizationEnabled: false, supportsTools: true), ctx, CancellationToken.None);

        var second = captured[1];
        Assert.Contains(second.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => (r.Result as string) == "SKU-1001,Blue Widget,4,10,3.50");
        Assert.Contains(second.SelectMany(m => m.Contents).OfType<FunctionCallContent>(), c => c.Name == "read_file");

        // Model context only — the rendered and persisted transcript keeps one bubble per step.
        Assert.DoesNotContain(session.Messages, m => (m.Content ?? string.Empty).Contains("SKU-1001"));
    }

    /// <summary>Parity with AgentStepInstruction.Compose: both twins say what a cleared result means.</summary>
    [Fact]
    public async Task TheLiveStepInstruction_CarriesTheReReadHint()
    {
        var captured = new List<List<ChatMessage>>();
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured.Add([.. (IList<ChatMessage>)ci[0]]);
                return Stream(new TextDelta("done"), new Finished(null, "m"));
            });

        var session = CreateSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "goal"));
        TaskAmbient.Current = null;
        TokenMapAmbient.Current = null;

        await session.RunStepTurnAsync(Spec(tokenizationEnabled: false), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.Contains(captured[0], m => m.Role == ChatRole.User && m.Text.Contains(AgentToolCarryover.ReReadHint));
    }

    /// <summary>Parity with AgentStepInstruction.Compose's headless caller: the live step must gate the vault
    /// hint on the same two halves, or only one of the two run paths is hinted.</summary>
    [Fact]
    public async Task TheLiveStepInstruction_CarriesTheVaultTargetHint_OnlyInAWorkspace()
    {
        var withBoth = await LiveStepInstructionAsync(@"C:\ws", VaultTargetPolicy.CreateSourceToolName);
        var noWorkspace = await LiveStepInstructionAsync(null, VaultTargetPolicy.CreateSourceToolName);
        var noMemoryTool = await LiveStepInstructionAsync(@"C:\ws", null);

        Assert.Contains(VaultTargetPolicy.StepHint, withBoth, StringComparison.Ordinal);
        Assert.DoesNotContain(VaultTargetPolicy.StepHint, noWorkspace, StringComparison.Ordinal);
        Assert.DoesNotContain(VaultTargetPolicy.StepHint, noMemoryTool, StringComparison.Ordinal);

        foreach (var instruction in new[] { withBoth, noWorkspace, noMemoryTool })
            Assert.Contains(AgentToolCarryover.ReReadHint, instruction, StringComparison.Ordinal);
    }

    /// <summary>The live half of E1's parity: this call site hands the composer its RunContext too, so a
    /// run's produced and reserved deliverables reach both paths.</summary>
    [Fact]
    public async Task TheLiveStepInstruction_CarriesBothSeededBlocks()
    {
        var ctx = new RunContext("goal", RunProfile.Interactive);
        ctx.RecordStep(
            new AgentStep { Ordinal = 0, Title = "earlier", Intent = "earlier", ExpectedArtifact = "done.md" },
            new StepTurnResult(true, false, null, "text", null, Guid.NewGuid(), Guid.NewGuid()));
        ctx.SetPlannedArtifacts([new PlannedStepArtifact(1, "later.md")]);

        var instruction = await LiveStepInstructionAsync(workspaceRoot: null, extraToolName: null, ctx);

        Assert.Contains(AgentStepInstruction.ProducedHeader + " done.md.", instruction, StringComparison.Ordinal);
        Assert.Contains(AgentStepInstruction.ReservedHeader + " later.md.", instruction, StringComparison.Ordinal);
        Assert.Contains(AgentStepInstruction.OwnDeliverableRule, instruction, StringComparison.Ordinal);
    }

    private async Task<string> LiveStepInstructionAsync(string? workspaceRoot, string? extraToolName, RunContext? ctx = null)
    {
        var captured = new List<List<ChatMessage>>();
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured.Add([.. (IList<ChatMessage>)ci[0]]);
                return Stream(new TextDelta("done"), new Finished(null, "m"));
            });

        var session = CreateSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "goal"));
        TaskAmbient.Current = null;
        TokenMapAmbient.Current = null;

        await session.RunStepTurnAsync(
            Spec(tokenizationEnabled: false, supportsTools: true, workspaceRoot: workspaceRoot, extraToolName: extraToolName),
            ctx ?? new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        return Assert.Single(captured[0], m => m.Role == ChatRole.User && m.Text.Contains("Execute step 1")).Text;
    }

    [Fact]
    public async Task RunStepTurn_ClearsIsStreaming_DetokenizesPii()
    {
        _tokenMap.Detokenize(Arg.Any<string>()).Returns(ci => "DETOK:" + (string)ci[0]);
        ReturnsStream(() => Stream(new TextDelta("<tok>"), new Finished(null, "m")));

        var session = CreateSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "goal"));
        var states = new List<ChatState>();
        session.StateChanged += (_, e) => states.Add(e.NewState);
        var failed = 0;
        session.RunFailed += (_, _) => failed++;

        TaskAmbient.Current = null;
        TokenMapAmbient.Current = null;

        var result = await session.RunStepTurnAsync(Spec(tokenizationEnabled: true), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(result.Succeeded);
        var assistant = session.Messages.Last(m => !m.IsUser);
        Assert.False(assistant.IsStreaming);
        _tokenMap.Received().Detokenize(Arg.Any<string>());
        Assert.StartsWith("DETOK:", assistant.Content);
        Assert.NotEqual(ChatState.Error, session.State);
        Assert.DoesNotContain(ChatState.Error, states);
        Assert.Equal(0, failed);
        Assert.Null(TaskAmbient.Current);
        Assert.Null(TokenMapAmbient.Current);
        // The ephemeral instruction is never mirrored into the transcript.
        Assert.Equal(2, session.Messages.Count);
        Assert.DoesNotContain(session.Messages, m => m.Content.Contains("Execute step 1"));
    }

    [Fact]
    public async Task RunStepTurn_Exception_ReturnsFailedResult_NoErrorState()
    {
        ReturnsStream(() => ThrowingStream(new InvalidOperationException("boom")));

        var session = CreateSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "goal"));
        var failed = 0;
        session.RunFailed += (_, _) => failed++;

        var result = await session.RunStepTurnAsync(Spec(tokenizationEnabled: false), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Contains("boom", result.Error);
        Assert.NotEqual(ChatState.Error, session.State);
        Assert.Equal(0, failed);
        Assert.False(session.Messages.Last(m => !m.IsUser).IsStreaming);
    }

    // ---- Executor parity: the LIVE step path compacts too, and relays the budget into the tool loop ----

    [Fact]
    public async Task RunStepTurn_WithAConfiguredWindow_CompactsTheRequest_AndRelaysTheBudget()
    {
        // Every other fixture here leaves MaxContextWindowTokens null, so the compaction call and the relayed
        // budget could both be deleted with the suite staying green.
        List<ChatMessage>? sent = null;
        AgentContextBudget? relayed = null;
        var budgeted = new AiProvider
        {
            Name = "Budgeted",
            Endpoint = "http://localhost",
            ProviderType = AiProviderType.OpenAI,
            MaxContextWindowTokens = 8_000,
            MaxOutputTokens = 2_000,
        };
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                sent = [.. (IList<ChatMessage>)ci[0]];
                relayed = (AgentContextBudget?)ci[8];
                return Stream(new TextDelta("done"), new Finished(null, "m"));
            });

        var session = CreateSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "THE GOAL: audit the repo."));
        // 12 replies, not 8: the 8-reply shape is under the truncation trigger at 8000/2000.
        for (var i = 1; i <= 12; i++)
            session.Messages.Add(new AssistantMessage(ChatRole.Assistant, $"step {i} reply: " + new string('x', 2_000)));

        var spec = Spec(tokenizationEnabled: false, budgeted);
        var result = await session.RunStepTurnAsync(
            spec,
            new RunContext("THE GOAL: audit the repo.", RunProfile.Interactive),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(sent);
        // Built: system + goal + 12 replies + the ephemeral instruction = 15.
        Assert.True(sent!.Count < 15, $"the live step request must be compacted, saw {sent.Count} of 15");
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Contains("THE GOAL", sent[1].Text);
        Assert.Contains("Execute step 1", sent[^1].Text);
        // ...and the same budget is relayed so the IN-STEP tool loop is bounded too.
        Assert.Equal(new AgentContextBudget(8_000, 2_000), relayed);

        // Keyed on the IDS, not the wording: the compactor logs to this same logger but cannot emit either id, so
        // Assert.Single stays honest even if its line is reworded.
        var diff = Assert.Single(
            _log.Entries,
            e => e.Message.Contains($"Agent run {spec.RunId} step {spec.Ordinal}", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, diff.Level);
        Assert.Contains($"from 15 to {sent!.Count}", diff.Message);
    }

    [Fact]
    public async Task RunStepTurn_WithNoConfiguredWindow_RelaysNoBudget_AndSendsEverything()
    {
        // Opt-in: an unconfigured provider (every provider after upgrade) must behave exactly as before.
        List<ChatMessage>? sent = null;
        AgentContextBudget? relayed = new AgentContextBudget(1, 1);
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                sent = [.. (IList<ChatMessage>)ci[0]];
                relayed = (AgentContextBudget?)ci[8];
                return Stream(new TextDelta("done"), new Finished(null, "m"));
            });

        var session = CreateSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "THE GOAL: audit the repo."));
        for (var i = 1; i <= 12; i++)
            session.Messages.Add(new AssistantMessage(ChatRole.Assistant, $"step {i} reply: " + new string('x', 2_000)));

        var spec = Spec(tokenizationEnabled: false);
        await session.RunStepTurnAsync(
            spec,
            new RunContext("THE GOAL: audit the repo.", RunProfile.Interactive),
            CancellationToken.None);

        Assert.Null(relayed);
        Assert.Equal(15, sent!.Count);

        // The diff line must only appear when something actually shrank. Keyed on the seam's own id prefix: the
        // compactor logs through this same logger, so a bare "compaction" substring would couple this to its wording.
        Assert.DoesNotContain(_log.Entries, e =>
            e.Message.Contains($"Agent run {spec.RunId} step", StringComparison.Ordinal)
            && e.Message.Contains("compaction", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunStepTurn_Cancelled_ReturnsCancelledResult()
    {
        // Models a user stop: the step token fires FIRST, then the exchange aborts with an OCE.
        using var cts = new CancellationTokenSource();
        ReturnsStream(() => ThrowingStream(new OperationCanceledException("cancelled"), () => cts.Cancel()));

        var session = CreateSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "goal"));

        var result = await session.RunStepTurnAsync(Spec(tokenizationEnabled: false), new RunContext("goal", RunProfile.Interactive), cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.NotEqual(ChatState.Error, session.State);
    }

    [Fact]
    public async Task RunStepTurn_TransportOce_TokenNotCancelled_ReturnsFailedNotCancelled()
    {
        // A TaskCanceledException out of the transport with the step token never cancelled — an HTTP timeout. It is
        // a FAILURE, not a user stop: recording Cancelled would settle the run with no replan and no explanation.
        ReturnsStream(() => ThrowingStream(new TaskCanceledException("The operation was canceled.")));

        var session = CreateSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "goal"));

        var result = await session.RunStepTurnAsync(Spec(tokenizationEnabled: false), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.False(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.NotEqual(ChatState.Error, session.State);
    }
}

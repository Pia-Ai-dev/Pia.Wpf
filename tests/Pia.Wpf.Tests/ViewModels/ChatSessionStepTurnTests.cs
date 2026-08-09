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

    private static StepTurnSpec Spec(bool tokenizationEnabled, AiProvider? provider = null) => new(
        RunId: Guid.NewGuid(),
        Ordinal: 0,
        Intent: "do the thing",
        ExpectedArtifact: "artifact",
        SystemPrompt: "system",
        Persona: new PersonaAttribution(Guid.NewGuid(), "Pia", "🤖"),
        Provider: provider
            ?? new AiProvider { Name = "Test", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
        Tools: null,
        SupportsTools: false,
        WebSearchActive: false,
        TokenizationEnabled: tokenizationEnabled);

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

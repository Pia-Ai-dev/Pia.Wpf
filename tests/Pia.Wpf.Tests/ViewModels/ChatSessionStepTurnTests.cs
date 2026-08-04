using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The additive step-turn path (§13.7/§16 R4): a step-turn clears IsStreaming and detokenizes PII
/// via the shared per-exchange cleanup, converts exceptions into a failed StepTurnResult (never
/// ChatState.Error / a RunFailed snackbar), keeps the ephemeral instruction out of the transcript,
/// and restores the ambients.
/// </summary>
public sealed class ChatSessionStepTurnTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    /// <summary>The session's log, so a fixture can assert on the compaction diff LINE (D-A').</summary>
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

    private static async IAsyncEnumerable<ChatStreamItem> ThrowingStream(Exception ex)
    {
        await Task.Yield();
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
        // Ephemeral instruction is never mirrored into the transcript (§13.7).
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
        // The Headless half of the compaction change is covered by HeadlessTurnExecutorTests; this is the
        // LIVE half, which had no assertion at all — BuildStepChatMessagesAsync's CompactAsync call and the
        // budget it relays into RunModelExchangeAsync could both be deleted with the suite staying green,
        // because every other fixture here leaves MaxContextWindowTokens null (so the budget is null and the
        // 6-arg stub keeps matching). A live agent step on a provider WITH a window would then still
        // overflow while the headless path compacts.
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
                relayed = (AgentContextBudget?)ci[7];
                return Stream(new TextDelta("done"), new Finished(null, "m"));
            });

        var session = CreateSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "THE GOAL: audit the repo."));
        // 12 prior step replies of ~500 estimated tokens each — the shape measured to be over budget at
        // 8000/2000 (the 8-reply shape is NOT, see AgentContextCompactorTests).
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
        // The pins survive: system first, then the goal, and the step instruction last.
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Contains("THE GOAL", sent[1].Text);
        Assert.Contains("Execute step 1", sent[^1].Text);
        // ...and the same budget is relayed so the IN-STEP tool loop is bounded too.
        Assert.Equal(new AgentContextBudget(8_000, 2_000), relayed);

        // D-A': the seam names WHICH run and WHICH step shrank. Keyed on the IDS rather than on the
        // wording - the compactor logs to this same logger and structurally cannot emit either id, and no
        // other ChatSession log template starts "Agent run", so Assert.Single stays honest even if the
        // compactor's own line is reworded or promoted to Information.
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
                relayed = (AgentContextBudget?)ci[7];
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

        // A GUARD, NOT A REGRESSION TEST: this passes before the D-A' change too. What it pins is that the
        // diff line only appears when something actually shrank - a null budget returns at CompactAsync's
        // budget guard before anything is logged, and the seam's count comparison finds no difference.
        // Keyed on the SEAM's own id prefix, never on a bare "compaction": the compactor logs through this
        // same logger instance, so a bare substring would couple this guard to the compactor's wording.
        Assert.DoesNotContain(_log.Entries, e =>
            e.Message.Contains($"Agent run {spec.RunId} step", StringComparison.Ordinal)
            && e.Message.Contains("compaction", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunStepTurn_Cancelled_ReturnsCancelledResult()
    {
        ReturnsStream(() => ThrowingStream(new OperationCanceledException("cancelled")));

        var session = CreateSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "goal"));

        var result = await session.RunStepTurnAsync(Spec(tokenizationEnabled: false), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.NotEqual(ChatState.Error, session.State);
    }
}

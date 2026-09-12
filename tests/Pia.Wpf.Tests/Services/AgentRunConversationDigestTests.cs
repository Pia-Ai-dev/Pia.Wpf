using System.IO;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The orchestrator half: which chats get a digest at all, and that the summary round is never paid invisibly.
/// </summary>
public sealed class AgentRunConversationDigestTests : IDisposable
{
    private const string Goal = "the file is not in the working folder";

    private readonly string _dir;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AgentRunConversationDigestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaDigest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
    }

    public void Dispose()
    {
        _chats.Dispose();
        _runs.Dispose();
        _ctx.Dispose();
        TempPath.Remove(_dir);
    }

    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };

    private static AiProvider Provider() => new()
    {
        Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI,
        MaxContextWindowTokens = 200_000, MaxOutputTokens = 4096,
    };

    /// <summary>Records what the plan turn was handed and then declines, so the run parks without executing.</summary>
    private sealed class CapturingPlanner : IAgentPlanner
    {
        public string? SeenDigest { get; private set; }
        public bool Planned { get; private set; }

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
        {
            Planned = true;
            SeenDigest = ctx.ConversationDigest;
            return Task.FromResult(PlanResult.Decline("which file?"));
        }

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    private sealed class NoopExecutor : IAgentTurnExecutor
    {
        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
            => Task.FromResult(new StepTurnResult(true, false, null, "done", null, Guid.NewGuid(), Guid.NewGuid()));

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
            => Task.FromResult(new StepTurnResult(true, false, null, "done", null, Guid.NewGuid(), Guid.NewGuid()));

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct) => Task.CompletedTask;

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeVerifier : IAgentVerifier
    {
        public Task<VerdictResult> VerifyAsync(RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(new VerdictResult(true, null, [], null));
    }

    private async Task<AgentRun> NewRunAsync(string? mode)
    {
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = chatId,
            SchemaVersion = 1,
            Title = "t",
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(),
            AgentContextMode = mode,
            Messages =
            [
                new SyncAssistantChatMessage { Id = Guid.NewGuid(), Role = "user", Content = "rename quarterly.md to q3.md", Timestamp = now },
                new SyncAssistantChatMessage { Id = Guid.NewGuid(), Role = "assistant", Content = "Done — it is now q3.md in Reports.", Timestamp = now },
                new SyncAssistantChatMessage { Id = Guid.NewGuid(), Role = "user", Content = Goal, Timestamp = now },
            ],
        }, Ct);
        return await _runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: Goal), Ct);
    }

    private async Task<CapturingPlanner> RunAsync(string? mode)
    {
        var run = await NewRunAsync(mode);
        var planner = new CapturingPlanner();
        var orchestrator = new AgentRunOrchestrator(
            _runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance,
            chats: _chats, ai: _ai);

        await orchestrator.RunAsync(run, new NoopExecutor(), Persona(), Provider(), RunProfile.Interactive, Ct);
        return planner;
    }

    /// <summary>Routines, scheduled jobs and background assignments all land here: nobody was at a composer to ask.</summary>
    [Fact]
    public async Task AChatThatRecordedNoMode_PlansWithoutADigestAndStillRuns()
    {
        var planner = await RunAsync(mode: null);

        Assert.True(planner.Planned);
        Assert.Null(planner.SeenDigest);
    }

    [Fact]
    public async Task Off_PlansWithoutADigest()
    {
        var planner = await RunAsync(nameof(AgentContextMode.Off));

        Assert.Null(planner.SeenDigest);
    }

    [Fact]
    public async Task Verbatim_ReachesThePlanTurn()
    {
        var planner = await RunAsync(nameof(AgentContextMode.Verbatim));

        Assert.NotNull(planner.SeenDigest);
        Assert.Contains("rename quarterly.md to q3.md", planner.SeenDigest);
        Assert.DoesNotContain(Goal, planner.SeenDigest);
    }

    /// <summary>A mode a newer build wrote must re-ask rather than be read as "declined".</summary>
    [Fact]
    public async Task AnUnrecognizedStoredMode_PlansWithoutADigest()
    {
        var planner = await RunAsync("Telepathy");

        Assert.Null(planner.SeenDigest);
    }

    [Fact]
    public async Task TheSummaryRoundsSpend_ReachesTheRunLedger()
    {
        _ai.GetChatResponseAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "They renamed a report."))
                {
                    Usage = new UsageDetails { InputTokenCount = 900, OutputTokenCount = 40 },
                }));

        var run = await NewRunAsync(nameof(AgentContextMode.Summary));
        var planner = new CapturingPlanner();
        var orchestrator = new AgentRunOrchestrator(
            _runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance,
            chats: _chats, ai: _ai);

        await orchestrator.RunAsync(run, new NoopExecutor(), Persona(), Provider(), RunProfile.Interactive, Ct);

        Assert.Contains("They renamed a report.", planner.SeenDigest);

        var final = await _runs.GetAsync(run.Id, Ct);
        using var doc = JsonDocument.Parse(final!.LedgerJson!);
        Assert.Equal(900, doc.RootElement.GetProperty("inputTokens").GetInt64());
        Assert.Equal(40, doc.RootElement.GetProperty("outputTokens").GetInt64());
    }
}

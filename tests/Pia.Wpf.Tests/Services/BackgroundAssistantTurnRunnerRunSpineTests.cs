using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Milestone 1.1 durable-spine wiring for <see cref="BackgroundAssistantTurnRunner"/> (§12.8):
/// run bookkeeping is failure-isolated (a throwing <see cref="IAgentRunService"/> never fails the
/// turn), and a headless turn persists a durable run with the right trigger provenance + ledger.
/// </summary>
public sealed class BackgroundAssistantTurnRunnerRunSpineTests
{
    private static AiProvider Provider() => new()
    {
        Id = Guid.NewGuid(),
        Name = "P",
        Endpoint = "https://example",
        TimeoutSeconds = 60,
    };

    private static BackgroundAssistantTurnRunner BuildRunner(
        IAssistantChatService chats,
        IAgentRunService runs,
        UsageDetails? usage = null,
        string answer = "ANSWER",
        bool throwMidStream = false,
        bool throwMidStreamCanceled = false,
        IAssistantPromptComposer? composer = null,
        IExecutingRunStore? executingRuns = null)
    {
        var ai = Substitute.For<IAiClientService>();
        var plugins = Substitute.For<IPluginService>();
        composer ??= Substitute.For<IAssistantPromptComposer>();
        var personas = Substitute.For<IPersonaService>();
        var titles = Substitute.For<IChatTitleService>();
        var settings = Substitute.For<ISettingsService>();

        settings.GetSettingsAsync().Returns(new AppSettings());
        personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
            .Returns(new Persona { Name = "Pia", SystemPrompt = "sys" });
        composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: false, WebSearchActive: false));
        titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => throwMidStreamCanceled ? ThrowMidStream(canceled: true)
                : throwMidStream ? ThrowMidStream() : Drive(usage, answer));

        ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();

        return new BackgroundAssistantTurnRunner(
            ai, plugins, composer, personas, chats, titles, settings,
            TokenMapFactory, runs, executingRuns ?? new ExecutingRunStore(),
            NullLogger<BackgroundAssistantTurnRunner>.Instance);
    }

    private static async IAsyncEnumerable<ChatStreamItem> Drive(UsageDetails? usage, string answer)
    {
        await Task.CompletedTask;
        yield return new TextDelta(answer);
        yield return new Finished(usage, "test-model");
    }

    private static async IAsyncEnumerable<ChatStreamItem> ThrowMidStream(bool canceled = false)
    {
        await Task.CompletedTask;
        yield return new TextDelta("partial");
        if (canceled) throw new OperationCanceledException("stream canceled");
        throw new InvalidOperationException("stream boom");
    }

    [Fact]
    public async Task HeadlessTurn_NeverMarksSuggestAgentModeEligible()
    {
        // R7/§14.3: the headless BackgroundAssistantTurnRunner must never inject suggest_agent_mode —
        // it has no chip-render surface. PrepareTurn must be called with suggestAgentModeEligible:false only.
        var chats = Substitute.For<IAssistantChatService>();
        chats.SaveAsync(Arg.Any<SyncAssistantChat>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var runs = new ThrowingAgentRunService(throwOnCreate: false);
        var composer = Substitute.For<IAssistantPromptComposer>();
        composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: false, WebSearchActive: false));

        var runner = BuildRunner(chats, runs, new UsageDetails { InputTokenCount = 3, OutputTokenCount = 1 }, composer: composer);
        await runner.RunAsync(new BackgroundTurnRequest { Prompt = "go", Provider = Provider() }, CancellationToken.None);

        composer.DidNotReceive().PrepareTurn(
            Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
            Arg.Any<bool>(), suggestAgentModeEligible: true);
    }

    [Fact]
    public async Task ThrowingRunService_OnHotPath_DoesNotFailTheTurn()
    {
        // CreateAsync returns a run, but every subsequent call (AddUsage/SetRunMessageRange/Complete)
        // throws — the isolation wrappers must swallow them.
        var chats = Substitute.For<IAssistantChatService>();
        chats.SaveAsync(Arg.Any<SyncAssistantChat>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var runs = new ThrowingAgentRunService(throwOnCreate: false);

        var runner = BuildRunner(chats, runs, new UsageDetails { InputTokenCount = 3, OutputTokenCount = 1 });
        var result = await runner.RunAsync(new BackgroundTurnRequest { Prompt = "go", Provider = Provider() }, CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task ThrowingRunService_OnCreate_DoesNotFailTheTurn()
    {
        var chats = Substitute.For<IAssistantChatService>();
        chats.SaveAsync(Arg.Any<SyncAssistantChat>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var runs = new ThrowingAgentRunService(throwOnCreate: true);

        var runner = BuildRunner(chats, runs);
        var result = await runner.RunAsync(new BackgroundTurnRequest { Prompt = "go", Provider = Provider() }, CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task SingleTurnRun_HoldsTheComposerBracket_AcrossItsFullReplaceWrite()
    {
        // A2's bracket premise for RunShape.SingleTurn — the hole A2 closes. This runner's terminal SaveAsync
        // is a FULL REPLACE with no merge and no bound, so a user message that lands while it is in flight is
        // deleted outright; the bracket must therefore be open across exactly that write, and gone once
        // RunAsync returns (a stale entry is a permanently dead composer).
        var store = new ExecutingRunStore();
        var bracketOpenAtWrite = new List<bool>();

        var chats = Substitute.For<IAssistantChatService>();
        chats.SaveAsync(Arg.Do<SyncAssistantChat>(c => bracketOpenAtWrite.Add(store.IsExecuting(c.Id))), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // The CreateAsync stub is LOAD-BEARING, not decoration: unconfigured, NSubstitute yields a null
        // AgentRun, the runner proceeds run-less, no bracket is ever registered — and the assertions below
        // would pass vacuously with the whole feature deleted.
        var runId = Guid.NewGuid();
        var runs = Substitute.For<IAgentRunService>();
        runs.CreateAsync(Arg.Any<AgentRunCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new AgentRun
            {
                Id = runId,
                ChatId = ci.Arg<AgentRunCreateRequest>().ChatId,
                RunShape = RunShape.SingleTurn,
                State = AgentRunState.Running,
            }));

        var runner = BuildRunner(chats, runs, executingRuns: store);
        var result = await runner.RunAsync(
            new BackgroundTurnRequest { Prompt = "go", Provider = Provider() }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        // Two writes: the FK stub, which precedes the run row and has nothing to lose (the chat is empty), and
        // the terminal full replace, which is the one that must be bracketed.
        Assert.Collection(bracketOpenAtWrite,
            stub => Assert.False(stub),
            terminal => Assert.True(terminal));
        Assert.False(store.IsExecuting(result.ChatId));
        Assert.Null(store.GetChatId(runId));
    }

    [Fact]
    public async Task HeadlessTurn_PersistsDurableRun_WithTriggerAndLedger()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        using var ctx = new SqliteContext(Path.Combine(tmpDir, "history.db"));
        using var runs = new AgentRunService(ctx, NullLogger<AgentRunService>.Instance);
        var chats = new AssistantChatService(ctx, runs);

        try
        {
            var jobId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var runner = BuildRunner(chats, runs, new UsageDetails { InputTokenCount = 11, OutputTokenCount = 6 }, answer: "done");

            var result = await runner.RunAsync(new BackgroundTurnRequest
            {
                Prompt = "research X",
                Provider = Provider(),
                Trigger = AgentRunTrigger.Schedule,
                TriggerRef = jobId,
                OwnerDeviceId = deviceId,
            }, CancellationToken.None);

            Assert.True(result.Succeeded);

            var runList = await runs.GetByChatAsync(result.ChatId, TestContext.Current.CancellationToken);
            var run = Assert.Single(runList);
            Assert.Equal(AgentRunState.Completed, run.State);
            Assert.Equal(AgentRunTrigger.Schedule, run.TriggerKind);
            Assert.Equal(jobId, run.TriggerRef);
            Assert.Equal(deviceId, run.OwnerDeviceId);

            // R3: FirstMessageId/LastMessageId must pin the actual persisted user/assistant message
            // ids — not merely be non-null — so the run's transcript slice is provably correct.
            var chat = await chats.GetAsync(result.ChatId, CancellationToken.None);
            Assert.NotNull(chat);
            var userMessage = Assert.Single(chat!.Messages, m => m.Role == "user");
            var assistantMessage = Assert.Single(chat.Messages, m => m.Role == "assistant");
            Assert.Equal(userMessage.Id, run.FirstMessageId);
            Assert.Equal(assistantMessage.Id, run.LastMessageId);

            using var doc = System.Text.Json.JsonDocument.Parse(run.LedgerJson!);
            Assert.Equal(11, doc.RootElement.GetProperty("inputTokens").GetInt64());
            Assert.Equal(6, doc.RootElement.GetProperty("outputTokens").GetInt64());
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task HeadlessTurn_AiThrowsMidStream_LeavesStubChat_AndMarksRunFailed()
    {
        // R1 write-order: even when the AI stream throws mid-turn, the up-front stub AssistantChats
        // row must remain so the Failed run's ChatId FK still resolves. Uses the real chat + run
        // services against a temp SqliteContext (FK enforcement ON) to prove the persisted state.
        var tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        using var ctx = new SqliteContext(Path.Combine(tmpDir, "history.db"));
        using var runs = new AgentRunService(ctx, NullLogger<AgentRunService>.Instance);
        var chats = new AssistantChatService(ctx, runs);

        try
        {
            var runner = BuildRunner(chats, runs, throwMidStream: true);

            var result = await runner.RunAsync(
                new BackgroundTurnRequest { Prompt = "go", Provider = Provider() }, CancellationToken.None);

            Assert.False(result.Succeeded);

            // The stub chat row persisted before the run was created still exists (FK target resolves).
            var stub = await chats.GetAsync(result.ChatId, CancellationToken.None);
            Assert.NotNull(stub);

            var runList = await runs.GetByChatAsync(result.ChatId, CancellationToken.None);
            var run = Assert.Single(runList);
            Assert.Equal(AgentRunState.Failed, run.State);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task HeadlessTurn_AiThrowsOperationCanceled_RethrowsAndMarksRunCancelled()
    {
        // §12.6 mapping: an OperationCanceledException mid-stream must both propagate to the caller
        // (never swallowed as an ordinary failure) AND leave the durable run Cancelled, not Failed.
        var tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        using var ctx = new SqliteContext(Path.Combine(tmpDir, "history.db"));
        using var runs = new AgentRunService(ctx, NullLogger<AgentRunService>.Instance);
        var chats = new AssistantChatService(ctx, runs);

        try
        {
            var runner = BuildRunner(chats, runs, throwMidStreamCanceled: true);

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                runner.RunAsync(new BackgroundTurnRequest { Prompt = "go", Provider = Provider() }, TestContext.Current.CancellationToken));

            var chatId = Assert.Single(await chats.GetAllIdsAsync(TestContext.Current.CancellationToken));
            var runList = await runs.GetByChatAsync(chatId, TestContext.Current.CancellationToken);
            var run = Assert.Single(runList);
            Assert.Equal(AgentRunState.Cancelled, run.State);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Hand-written fake: bookkeeping methods throw so the runner's isolation wrappers are exercised.</summary>
    private sealed class ThrowingAgentRunService : IAgentRunService
    {
        private readonly bool _throwOnCreate;

        public ThrowingAgentRunService(bool throwOnCreate) => _throwOnCreate = throwOnCreate;

#pragma warning disable CS0067 // event never used
        public event EventHandler<AgentRunChangedEventArgs>? RunChanged;
#pragma warning restore CS0067

        public Task<AgentRun> CreateAsync(AgentRunCreateRequest request, CancellationToken ct = default)
        {
            if (_throwOnCreate) throw new InvalidOperationException("create boom");
            return Task.FromResult(new AgentRun { Id = Guid.NewGuid(), ChatId = request.ChatId, State = AgentRunState.Running });
        }

        public Task SetStateAsync(Guid runId, AgentRunState state, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task AddUsageAsync(Guid runId, Guid? stepId, UsageDetails usage, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task SetRunMessageRangeAsync(Guid runId, Guid firstMessageId, Guid lastMessageId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task CompleteAsync(Guid runId, bool truncated = false, string? truncationReason = null, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task FailAsync(Guid runId, string? error, bool cancelled = false, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task PauseAsync(Guid runId, string? reason, CancellationToken ct = default, string? approvalTool = null) => throw new InvalidOperationException("boom");
        public Task UpdatePolicyJsonAsync(Guid runId, string? policyJson, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<bool> TryBeginResumeAsync(Guid runId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<bool> TryPauseUserAsync(Guid runId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<bool> TryResumeFromPauseAsync(Guid runId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task BeginChildWaitAsync(Guid runId, int childCount, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<bool> TryEndChildWaitAsync(Guid runId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<int> FailInterruptedRunsAsync(CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<AgentRun>> GetByChatAsync(Guid chatId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<AgentRun>> GetChildRunsAsync(Guid parentRunId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<bool> ChatHasPlannedRunAsync(Guid chatId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<bool> AnyExecutingRunForTriggerAsync(Guid triggerRef, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<ScheduledFiringOutcome>> GetLatestSettledFiringsAsync(CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task ReplaceStepsAsync(Guid runId, IReadOnlyList<AgentStep> steps, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<PlanMutationResult> ApplyPlanMutationAsync(Guid runId, IReadOnlyList<PlanStepEdit> pendingSteps, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<AgentStep?> NextPendingStepAsync(Guid runId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task SetStepStatusAsync(Guid stepId, AgentStepStatus status, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task RecordStepResultAsync(Guid stepId, AgentStepStatus status, Guid? firstMessageId, Guid? lastMessageId, UsageDetails? usage, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    }
}

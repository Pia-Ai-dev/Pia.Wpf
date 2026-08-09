using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// T2-18 — the run/step logging scopes the orchestrator opens. <c>ScopeRenderingLoggerProviderTests</c> proves the
/// SINK renders a scope; this proves the run loop actually opens one, with the ids a person can correlate on.
/// <para>
/// The ordinal, not the step id, is the step scope's value: the plan, the run panel and the audit table all show
/// the ordinal, so it is what a log line has to be matched against.
/// </para>
/// </summary>
public sealed class AgentRunScopeCorrelationTests
{
    /// <summary>Records what was pushed and what was open at each line — the two questions this file asks.</summary>
    private sealed class ScopeRecordingLogger : ILogger<AgentRunOrchestrator>
    {
        private readonly AsyncLocal<ImmutableStackish?> _open = new();

        private sealed class ImmutableStackish
        {
            internal ImmutableStackish(string text, ImmutableStackish? parent)
            {
                Text = text;
                Parent = parent;
            }

            internal string Text { get; }
            internal ImmutableStackish? Parent { get; }
        }

        public List<string> Pushed { get; } = [];

        /// <summary>Every logged line, prefixed with the scopes open at the time (outermost first).</summary>
        public List<string> Lines { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var text = state.ToString() ?? string.Empty;
            Pushed.Add(text);
            var previous = _open.Value;
            _open.Value = new ImmutableStackish(text, previous);
            return new Pop(this, previous);
        }

        private sealed class Pop : IDisposable
        {
            private readonly ScopeRecordingLogger _owner;
            private readonly ImmutableStackish? _previous;
            internal Pop(ScopeRecordingLogger owner, ImmutableStackish? previous)
            {
                _owner = owner;
                _previous = previous;
            }
            public void Dispose() => _owner._open.Value = _previous;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var parts = new List<string>();
            for (var s = _open.Value; s is not null; s = s.Parent) parts.Add(s.Text);
            parts.Reverse();
            Lines.Add((parts.Count == 0 ? "" : "[" + string.Join(" ", parts) + "] ") + formatter(state, exception));
        }

        /// <summary>What was open when <paramref name="predicate"/> matched a line.</summary>
        public string? LineMatching(Func<string, bool> predicate) => Lines.FirstOrDefault(predicate);
    }

    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };
    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

    private static StepTurnResult Ok() => new(true, false, null, "done", null, Guid.NewGuid(), Guid.NewGuid());

    private sealed class OneStepPlanner : IAgentPlanner
    {
        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(new PlanResult(
                [new AgentStep { Id = Guid.Empty, Ordinal = 0, Title = "A", Intent = "s1", Status = AgentStepStatus.Pending }],
                false));

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    /// <summary>Logs from INSIDE the step turn, which is where the step scope has to be visible.</summary>
    private sealed class LoggingExecutor : IAgentTurnExecutor
    {
        private readonly ILogger _logger;
        internal LoggingExecutor(ILogger logger) => _logger = logger;

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            _logger.LogInformation("executing the step turn");
            return Task.FromResult(Ok());
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
            => Task.FromResult(Ok());

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct)
        {
            _logger.LogInformation("ending the run");
            return Task.CompletedTask;
        }

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task ARunOpensARunScope_AndEachStepNestsItsOrdinalInside()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(Path.GetTempPath(), "PiaScope_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var sqlite = new SqliteContext(Path.Combine(dir, "history.db"));
            using var runs = new AgentRunService(sqlite, NullLogger<AgentRunService>.Instance);
            var chats = new AssistantChatService(sqlite, runs);

            var chatId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await chats.SaveAsync(new SyncAssistantChat
            {
                Id = chatId, SchemaVersion = 1, Title = "t",
                CreatedAt = now, UpdatedAt = now, LastAccessedAt = now,
                WindowMode = WindowMode.Assistant.ToString(), Messages = [],
            }, ct);
            var run = await runs.CreateAsync(
                new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.Schedule, Goal: "goal"), ct);

            var logger = new ScopeRecordingLogger();
            var orchestrator = new AgentRunOrchestrator(runs, new OneStepPlanner(), new FakeVerifier(), logger);

            await orchestrator.RunAsync(run, new LoggingExecutor(logger), Persona(), Provider(),
                RunProfile.Interactive, ct);

            // The run scope carries the run id, and the step scope its ORDINAL.
            Assert.Contains($"run {run.Id}", logger.Pushed);
            Assert.Contains("step 0", logger.Pushed);

            // Nesting, read off a line written from INSIDE the step turn: this is the fact a wrong scope
            // placement (or a scope opened around the wrong call) breaks.
            var stepLine = logger.LineMatching(l => l.EndsWith("executing the step turn"));
            Assert.Equal($"[run {run.Id} step 0] executing the step turn", stepLine);

            // And the step scope CLOSED with the step: a run-level line after it carries the run scope only.
            var endLine = logger.LineMatching(l => l.EndsWith("ending the run"));
            Assert.Equal($"[run {run.Id}] ending the run", endLine);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }
    }
}

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

// The step scope's value is the ordinal, not the step id: the plan, run panel and audit table all show the ordinal.
public sealed class AgentRunScopeCorrelationTests
{
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

            Assert.Contains($"run {run.Id}", logger.Pushed);
            Assert.Contains("step 0", logger.Pushed);

            // Read off a line written from inside the step turn, which is where the nesting has to hold.
            var stepLine = logger.LineMatching(l => l.EndsWith("executing the step turn"));
            Assert.Equal($"[run {run.Id} step 0] executing the step turn", stepLine);

            var endLine = logger.LineMatching(l => l.EndsWith("ending the run"));
            Assert.Equal($"[run {run.Id}] ending the run", endLine);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }
    }
}

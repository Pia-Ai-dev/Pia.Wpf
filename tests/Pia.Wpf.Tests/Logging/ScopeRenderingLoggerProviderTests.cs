using Microsoft.Extensions.Logging;
using Pia.Logging;
using Xunit;

namespace Pia.Tests.Logging;

/// <summary><c>NReco.Logging.File</c> has no scope support, so without this decorator the run/step scope never reaches
/// the file; every fact is asserted on what the INNER provider was handed.</summary>
public class ScopeRenderingLoggerProviderTests
{
    /// <summary>Stands in for the file sink: records the FORMATTED text, which is all a text sink ever writes.</summary>
    private sealed class RecordingProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public List<string> Categories { get; } = [];
        public bool Disposed { get; private set; }

        public ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            return new RecordingLogger(this);
        }

        public void Dispose() => Disposed = true;

        private sealed class RecordingLogger : ILogger
        {
            private readonly RecordingProvider _owner;
            internal RecordingLogger(RecordingProvider owner) => _owner = owner;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => _owner.Lines.Add(formatter(state, exception));
        }
    }

    private static (ILogger Logger, RecordingProvider Inner) Build(string category = "Pia.Services.Test")
    {
        var inner = new RecordingProvider();
        var provider = new ScopeRenderingLoggerProvider(inner);
        return (provider.CreateLogger(category), inner);
    }

    [Fact]
    public void WithNoScope_TheLineIsUntouched()
    {
        var (logger, inner) = Build();

        logger.LogInformation("Round 1/10 starting");

        Assert.Equal("Round 1/10 starting", Assert.Single(inner.Lines));
    }

    [Fact]
    public void AScopeIsPrefixed_UsingTheStatesOwnText()
    {
        var (logger, inner) = Build();
        var runId = Guid.NewGuid();

        using (logger.BeginScope("run {RunId}", runId))
        {
            logger.LogInformation("Round 1/10 starting");
        }

        Assert.Equal($"[run {runId}] Round 1/10 starting", Assert.Single(inner.Lines));
    }

    [Fact]
    public void NestedScopes_ReadOutermostFirst()
    {
        var (logger, inner) = Build();
        var runId = Guid.NewGuid();

        using (logger.BeginScope("run {RunId}", runId))
        using (logger.BeginScope("step {StepOrdinal}", 3))
        {
            logger.LogInformation("Invoking tool handler");
        }

        Assert.Equal($"[run {runId} step 3] Invoking tool handler", Assert.Single(inner.Lines));
    }

    [Fact]
    public void AClosedScope_StopsLabelling()
    {
        var (logger, inner) = Build();

        using (logger.BeginScope("run {RunId}", Guid.Empty))
        {
            logger.LogInformation("inside");
        }
        logger.LogInformation("outside");

        Assert.Equal(2, inner.Lines.Count);
        Assert.StartsWith("[run ", inner.Lines[0]);
        Assert.Equal("outside", inner.Lines[1]);
    }

    [Fact]
    public void AnInnerScopeClosing_LeavesTheOuterOneStanding()
    {
        var (logger, inner) = Build();
        var runId = Guid.NewGuid();

        using (logger.BeginScope("run {RunId}", runId))
        {
            using (logger.BeginScope("step {StepOrdinal}", 1))
            {
                logger.LogInformation("in the step");
            }
            logger.LogInformation("between steps");
        }

        Assert.Equal($"[run {runId} step 1] in the step", inner.Lines[0]);
        Assert.Equal($"[run {runId}] between steps", inner.Lines[1]);
    }

    /// <summary>Why the scope stack is static rather than per-logger: a run scope opened on one category must also
    /// label the lines another category writes inside that flow.</summary>
    [Fact]
    public void AScopeOpenedOnOneCategory_LabelsAnotherCategorysLines()
    {
        var inner = new RecordingProvider();
        var provider = new ScopeRenderingLoggerProvider(inner);
        var orchestrator = provider.CreateLogger("Pia.Services.AgentRunOrchestrator");
        var toolHandler = provider.CreateLogger("Pia.Services.FilesToolHandler");
        var runId = Guid.NewGuid();

        using (orchestrator.BeginScope("run {RunId}", runId))
        {
            toolHandler.LogInformation("write_file prepared");
        }

        Assert.Equal($"[run {runId}] write_file prepared", Assert.Single(inner.Lines));
    }

    /// <summary>A run loop awaits on every line it writes, so an ambient that did not flow would label almost nothing.</summary>
    [Fact]
    public async Task AScopeSurvivesAnAwait()
    {
        var (logger, inner) = Build();
        var runId = Guid.NewGuid();

        using (logger.BeginScope("run {RunId}", runId))
        {
            await Task.Yield();
            await Task.Delay(1, TestContext.Current.CancellationToken);
            logger.LogInformation("after the await");
        }

        Assert.Equal($"[run {runId}] after the await", Assert.Single(inner.Lines));
    }

    /// <summary>Two concurrent runs must not see each other's scope, or the log is unreadable once the pool is wider than one.</summary>
    [Fact]
    public async Task ConcurrentFlows_DoNotSeeEachOthersScopes()
    {
        var inner = new RecordingProvider();
        var provider = new ScopeRenderingLoggerProvider(inner);
        var logger = provider.CreateLogger("Pia.Services.Test");
        var gate = new SemaphoreSlim(0, 2);

        async Task RunAsync(string id)
        {
            using (logger.BeginScope("run {RunId}", id))
            {
                gate.Release();
                await gate.WaitAsync(TestContext.Current.CancellationToken); // both are inside their scope here
                logger.LogInformation("line from {Id}", id);
            }
        }

        await Task.WhenAll(RunAsync("A"), RunAsync("B"));

        Assert.Equal(2, inner.Lines.Count);
        Assert.Contains("[run A] line from A", inner.Lines);
        Assert.Contains("[run B] line from B", inner.Lines);
    }

    [Fact]
    public void AnEmptyScopeState_AddsNothing()
    {
        var (logger, inner) = Build();

        using (logger.BeginScope(string.Empty))
        {
            logger.LogInformation("plain");
        }

        Assert.Equal("plain", Assert.Single(inner.Lines));
    }

    [Fact]
    public void DisposingTheProvider_DisposesTheSinkBehindIt()
    {
        var inner = new RecordingProvider();
        var provider = new ScopeRenderingLoggerProvider(inner);

        provider.Dispose();

        Assert.True(inner.Disposed);
    }

    [Fact]
    public void TheCategoryReachesTheSinkUnchanged()
    {
        var (_, inner) = Build("Pia.Services.AgentPlanner");

        Assert.Equal("Pia.Services.AgentPlanner", Assert.Single(inner.Categories));
    }
}

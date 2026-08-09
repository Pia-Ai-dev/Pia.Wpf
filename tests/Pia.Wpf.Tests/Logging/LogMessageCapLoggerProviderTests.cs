using Microsoft.Extensions.Logging;
using Pia.Logging;
using Xunit;

namespace Pia.Tests.Logging;

// A content-agnostic length bound, not redaction: the Sensitive* family is already erased from release builds.
public class LogMessageCapLoggerProviderTests
{
    private sealed class RecordingProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public bool Disposed { get; private set; }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);
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
            {
                _owner.Lines.Add(formatter(state, exception));
                _owner.Exceptions.Add(exception);
            }
        }
    }

    private static (ILogger Logger, RecordingProvider Inner) Build(int cap)
    {
        var inner = new RecordingProvider();
        return (new LogMessageCapLoggerProvider(inner, cap).CreateLogger("Pia.Services.Test"), inner);
    }

    [Fact]
    public void AShortLine_IsUntouched()
    {
        var (logger, inner) = Build(cap: 100);

        logger.LogInformation("Created todo 42");

        Assert.Equal("Created todo 42", Assert.Single(inner.Lines));
    }

    [Fact]
    public void ALongLine_IsCappedAndSaysHowMuchIsMissing()
    {
        var (logger, inner) = Build(cap: 20);

        logger.LogInformation("{Payload}", new string('x', 100));

        var line = Assert.Single(inner.Lines);
        Assert.StartsWith(new string('x', 20), line);
        Assert.Contains("+80 chars withheld", line);
        Assert.DoesNotContain(new string('x', 21), line);
    }

    // The head is what survives, which is why Bootstrapper puts the scope prefix inside the cap.
    [Fact]
    public void TruncationKeepsTheHead_SoAPrefixSurvives()
    {
        var (logger, inner) = Build(cap: 30);

        logger.LogInformation("[run 1234] {Payload}", new string('y', 500));

        Assert.StartsWith("[run 1234] ", Assert.Single(inner.Lines));
    }

    // The cap applies to the message only — the sink appends the exception, so a stack trace is never truncated.
    [Fact]
    public void TheExceptionIsPassedThroughUntouched()
    {
        var (logger, inner) = Build(cap: 10);
        var boom = new InvalidOperationException(new string('z', 5000));

        logger.LogError(boom, "{Payload}", new string('x', 500));

        Assert.Same(boom, Assert.Single(inner.Exceptions));
        Assert.Contains("withheld", Assert.Single(inner.Lines)); // non-vacuity: the MESSAGE really was capped
    }

    [Fact]
    public void ABoundaryLengthLine_IsNotCapped()
    {
        var (logger, inner) = Build(cap: 15);

        logger.LogInformation("{Payload}", new string('x', 15));

        Assert.Equal(new string('x', 15), Assert.Single(inner.Lines));
    }

    [Fact]
    public void AnAbsurdCap_StillLeavesSomething()
    {
        var (logger, inner) = Build(cap: 0);

        logger.LogInformation("{Payload}", new string('x', 10));

        Assert.StartsWith("x…", Assert.Single(inner.Lines));
    }

    [Fact]
    public void ScopesAndLevelsPassThrough()
    {
        var inner = new RecordingProvider();
        var logger = new LogMessageCapLoggerProvider(inner, 100).CreateLogger("c");

        Assert.True(logger.IsEnabled(LogLevel.Trace));
        Assert.Null(logger.BeginScope("run 1")); // the inner sink's answer, not a substitute of our own
    }

    [Fact]
    public void DisposingTheProvider_DisposesTheSinkBehindIt()
    {
        var inner = new RecordingProvider();
        var provider = new LogMessageCapLoggerProvider(inner, 100);

        provider.Dispose();

        Assert.True(inner.Disposed);
    }

    // DEBUG must not truncate: a developer's log is the thing being diagnosed, and Sensitive* lines only exist there.
    [Fact]
    public void TheDefaultCapMatchesTheBuild()
    {
        var inner = new RecordingProvider();
        var logger = new LogMessageCapLoggerProvider(inner).CreateLogger("c");
        var long_ = new string('x', LogMessageCapLoggerProvider.ReleaseCapChars + 500);

        logger.LogInformation("{Payload}", long_);
        var line = Assert.Single(inner.Lines);

#if DEBUG
        Assert.Equal(int.MaxValue, LogMessageCapLoggerProvider.DefaultCapChars);
        Assert.Equal(long_, line);
#else
        Assert.Equal(LogMessageCapLoggerProvider.ReleaseCapChars, LogMessageCapLoggerProvider.DefaultCapChars);
        Assert.Contains("withheld", line);
#endif
    }
}

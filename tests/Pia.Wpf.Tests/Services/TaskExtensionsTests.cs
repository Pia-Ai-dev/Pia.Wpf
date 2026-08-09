using Microsoft.Extensions.Logging;
using NSubstitute;
using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Services;

public class TaskExtensionsTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();

    [Fact]
    public async Task SafeFireAndForget_CompletedTask_DoesNotLog()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetResult();

        tcs.Task.SafeFireAndForget(_logger);

        // Give the fire-and-forget a moment to complete
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(_logger.ReceivedCalls());
    }

    [Fact]
    public async Task SafeFireAndForget_FailedTask_LogsError()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetException(new InvalidOperationException("test error"));

        // SafeFireAndForget is async void, so the logger call is the only observable completion signal.
        var logged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _logger.When(l => l.Log(
                LogLevel.Error,
                Arg.Any<EventId>(),
                Arg.Any<object>(),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>()))
            .Do(_ => logged.TrySetResult());

        tcs.Task.SafeFireAndForget(_logger);

        await logged.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        _logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Is<Exception>(ex => ex.Message == "test error"),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task SafeFireAndForget_OperationCanceled_IsSuppressed()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetException(new OperationCanceledException());

        tcs.Task.SafeFireAndForget(_logger);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // OperationCanceledException should be silently suppressed
        Assert.Empty(_logger.ReceivedCalls());
    }

    [Fact]
    public async Task SafeFireAndForget_SlowTask_DoesNotBlock()
    {
        var started = false;
        var blocking = new TaskCompletionSource();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task SlowOperation()
        {
            started = true;
            await blocking.Task;
            finished.TrySetResult();
        }

        SlowOperation().SafeFireAndForget(_logger);

        // Should return immediately without blocking
        Assert.True(started);
        Assert.False(finished.Task.IsCompleted);

        blocking.SetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}

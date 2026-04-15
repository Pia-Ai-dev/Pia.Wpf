using FluentAssertions;
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
        await Task.Delay(50);

        _logger.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SafeFireAndForget_FailedTask_LogsError()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetException(new InvalidOperationException("test error"));

        tcs.Task.SafeFireAndForget(_logger);

        await Task.Delay(50);

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

        await Task.Delay(50);

        // OperationCanceledException should be silently suppressed
        _logger.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SafeFireAndForget_SlowTask_DoesNotBlock()
    {
        var started = false;
        var completed = false;

        async Task SlowOperation()
        {
            started = true;
            await Task.Delay(200);
            completed = true;
        }

        SlowOperation().SafeFireAndForget(_logger);

        // Should return immediately without blocking
        started.Should().BeTrue();
        completed.Should().BeFalse();

        await Task.Delay(300);
        completed.Should().BeTrue();
    }
}

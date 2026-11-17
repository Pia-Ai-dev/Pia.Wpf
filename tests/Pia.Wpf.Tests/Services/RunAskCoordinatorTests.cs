using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public sealed class RunAskCoordinatorTests
{
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly IChatAttendance _attendance = Substitute.For<IChatAttendance>();

    private RunAskCoordinator Broker(TimeSpan timeout) => new(_attendance, _runs, NullLogger<RunAskCoordinator>.Instance, timeout: timeout);

    private static RunToolQuestion Question(Guid runId, Guid chatId) =>
        new(Guid.NewGuid(), runId, chatId, null, "write_file", Guid.NewGuid(), "a.txt");

    [Fact]
    public async Task AttendanceIsCheckedPerCall_SoSwitchingAwayLaterDoesNotEndAWait()
    {
        var runId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        _attendance.IsAttended(chatId).Returns(true);
        var broker = Broker(TimeSpan.FromSeconds(30));
        var question = Question(runId, chatId);

        var asked = broker.AskAsync(question, null, TestContext.Current.CancellationToken);
        _attendance.IsAttended(chatId).Returns(false);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(asked.IsCompleted);

        Assert.True(broker.Answer(runId, question.RequestId, ToolDecision.AllowOnce));
        Assert.Equal(ToolDecision.AllowOnce, await asked);

        Assert.Null(await broker.AskAsync(Question(runId, chatId), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAnswerForAnotherQuestion_IsIgnored()
    {
        var runId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        _attendance.IsAttended(chatId).Returns(true);
        var broker = Broker(TimeSpan.FromMilliseconds(150));

        var asked = broker.AskAsync(Question(runId, chatId), null, TestContext.Current.CancellationToken);

        Assert.False(broker.Answer(runId, Guid.NewGuid(), ToolDecision.AlwaysAllow));
        Assert.Null(await asked);
    }

    [Fact]
    public async Task AFaultingAttendanceCheck_Parks()
    {
        _attendance.IsAttended(Arg.Any<Guid>()).Returns(_ => throw new InvalidOperationException());

        Assert.Null(await Broker(TimeSpan.FromSeconds(30))
            .AskAsync(Question(Guid.NewGuid(), Guid.NewGuid()), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AThrowingChangedSubscriber_LeavesNoPendingEntry_AndRestartsTheClock()
    {
        var runId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        _attendance.IsAttended(chatId).Returns(true);
        var broker = Broker(TimeSpan.FromSeconds(30));
        var thrown = false;
        broker.Changed += (_, _) =>
        {
            if (thrown) return;
            thrown = true;
            throw new InvalidOperationException("subscriber");
        };
        bool? clock = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            broker.AskAsync(Question(runId, chatId), running => clock = running, TestContext.Current.CancellationToken));

        Assert.Null(broker.GetPending(runId));
        Assert.True(clock);
        _runs.Received().SetWorkClockRunning(runId, true);
    }

    [Fact]
    public void TheDefaultWaitIsTenMinutes() => Assert.Equal(TimeSpan.FromMinutes(10), RunAskCoordinator.DefaultTimeout);
}

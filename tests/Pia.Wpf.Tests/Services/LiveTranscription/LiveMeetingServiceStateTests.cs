using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class LiveMeetingServiceStateTests
{
    [Fact]
    public async Task StopAsync_FromIdle_DoesNotRaiseStateChanged()
    {
        var sut = CreateSut();
        var observed = new List<LiveMeetingState>();
        sut.StateChanged += (_, s) => observed.Add(s);

        await sut.StopAsync();

        Assert.Empty(observed);
    }

    [Fact]
    public async Task StopAsync_FromIdle_ObserverSeesNoTransitions_Synchronously()
    {
        // Regression: previously SetStateLocked dispatched via Task.Run, so even a
        // no-op call could schedule work that races with the test assertion. We
        // assert that the moment StopAsync returns, every observed state has
        // already been delivered — no thread-pool deferral.
        var sut = CreateSut();
        var observed = new List<LiveMeetingState>();
        sut.StateChanged += (_, s) => observed.Add(s);

        await sut.StopAsync();

        // Give thread-pool work a chance to run and corrupt the result, if any.
        await Task.Delay(50);

        Assert.Empty(observed);
    }

    [Fact]
    public void TransitionState_RaisesEvent_OnCallingThread_NotThreadPool()
    {
        var sut = CreateSut();
        int? observedThreadId = null;
        sut.StateChanged += (_, _) => observedThreadId = Environment.CurrentManagedThreadId;

        var callerThreadId = Environment.CurrentManagedThreadId;

        var setMethod = typeof(LiveMeetingService).GetMethod(
            "TransitionState",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(setMethod);

        setMethod!.Invoke(sut, new object[] { LiveMeetingState.Starting });

        Assert.Equal(callerThreadId, observedThreadId);
    }

    private static LiveMeetingService CreateSut()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var http = Substitute.For<System.Net.Http.IHttpClientFactory>();
        var loggers = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        return new LiveMeetingService(settings, http, loggers);
    }
}

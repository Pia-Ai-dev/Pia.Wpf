using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Pia.Services.MeetingAttendee;
using Xunit;

namespace Pia.Tests.Services.MeetingAttendee;

/// <summary>
/// Exercises the <see cref="MeetingAttendeeService"/> state machine with a substituted
/// <see cref="IMeetingSession"/> and a hand-rolled fake <see cref="IAudioCaptureSource"/>. No real
/// browser, audio device, model, or sherpa engine is constructed — every IO seam is injected via the
/// internal test constructor. Covers: the happy transition sequence (with and without lobby), the
/// natural-end auto-stop path, the join-failure error + cleanup path, and dispose ordering.
/// </summary>
public sealed class MeetingAttendeeServiceStateTests
{
    private const string MeetingUrl = "https://teams.microsoft.com/l/meetup-join/abc";

    // ---- happy path -----------------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_NoLobby_TransitionsProvisioningJoiningAttending()
    {
        var fx = new Fixture();

        await fx.Service.StartAsync(MeetingUrl);

        Assert.Equal(MeetingAttendeeState.Attending, fx.Service.State);
        Assert.Equal(
            new[]
            {
                MeetingAttendeeState.ProvisioningBrowser,
                MeetingAttendeeState.Joining,
                MeetingAttendeeState.Attending,
            },
            fx.Observed);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_WhenLobbyRaisedDuringJoin_TransitionsThroughInLobby()
    {
        var fx = new Fixture();
        // Raise EnteredLobby from inside JoinAsync — the orchestrator subscribes before calling join.
        fx.Session
            .When(s => s.JoinAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => fx.Session.EnteredLobby += Raise.Event<EventHandler>(fx.Session, EventArgs.Empty));

        await fx.Service.StartAsync(MeetingUrl);

        Assert.Equal(
            new[]
            {
                MeetingAttendeeState.ProvisioningBrowser,
                MeetingAttendeeState.Joining,
                MeetingAttendeeState.InLobby,
                MeetingAttendeeState.Attending,
            },
            fx.Observed);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_JoinsWithUsersAssistantDisplayName()
    {
        var fx = new Fixture();
        fx.Settings.GetSettingsAsync().Returns(new AppSettings { SyncUserDisplayName = "Alex" });

        await fx.Service.StartAsync(MeetingUrl);

        await fx.Session.Received(1).JoinAsync(MeetingUrl, "Alex's assistant", Arg.Any<CancellationToken>());

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_StartsAudioSourceAndBuildsEngineWithThemSpeaker()
    {
        var fx = new Fixture();

        await fx.Service.StartAsync(MeetingUrl);

        Assert.True(fx.AudioSource.Started);
        Assert.True(fx.EngineBuilt);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_Twice_Throws()
    {
        var fx = new Fixture();
        await fx.Service.StartAsync(MeetingUrl);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Service.StartAsync(MeetingUrl));

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_NullOrWhitespaceUrl_Throws()
    {
        var fx = new Fixture();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fx.Service.StartAsync("  "));
    }

    // ---- natural end ----------------------------------------------------------------------------

    [Fact]
    public async Task WhenMeetingEnds_AutoStopsToIdleAndDisposes()
    {
        var fx = new Fixture();
        await fx.Service.StartAsync(MeetingUrl);

        // The background watch loop is awaiting WaitForEndAsync; completing it triggers auto-stop.
        fx.MeetingEnded.SetResult();

        await WaitForStateAsync(fx.Service, MeetingAttendeeState.Idle);

        Assert.Equal(MeetingAttendeeState.Idle, fx.Service.State);
        await fx.Session.Received(1).LeaveAsync();
        await fx.Session.Received(1).DisposeAsync();
        Assert.True(fx.AudioSource.Stopped);
        Assert.True(fx.AudioSource.Disposed);

        await fx.Service.DisposeAsync();
    }

    // ---- error path -----------------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_WhenJoinThrows_TransitionsToErrorAndDisposesSession()
    {
        var fx = new Fixture();
        fx.Session
            .JoinAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("never admitted"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Service.StartAsync(MeetingUrl));

        Assert.Equal(MeetingAttendeeState.Error, fx.Service.State);
        // The session is disposed on the failure path...
        await fx.Session.Received(1).DisposeAsync();
        // ...and the post-join resources were never created.
        Assert.False(fx.AudioSourceFactoryRan);
        Assert.False(fx.EngineBuilt);
        Assert.Equal(MeetingAttendeeState.Error, fx.Observed[^1]);
    }

    [Fact]
    public async Task StartAsync_WhenProvisionThrows_TransitionsToError_NoSession()
    {
        var fx = new Fixture { ProvisionThrows = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Service.StartAsync(MeetingUrl));

        Assert.Equal(MeetingAttendeeState.Error, fx.Service.State);
        Assert.False(fx.SessionFactoryRan);
    }

    [Fact]
    public async Task AfterError_CanStartAgain()
    {
        var fx = new Fixture();
        fx.Session
            .JoinAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Service.StartAsync(MeetingUrl));
        Assert.Equal(MeetingAttendeeState.Error, fx.Service.State);

        // Recover: clear the join failure and start cleanly.
        fx.Session.ClearSubstitute(ClearOptions.CallActions);
        fx.Session.JoinAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await fx.Service.StartAsync(MeetingUrl);
        Assert.Equal(MeetingAttendeeState.Attending, fx.Service.State);

        await fx.Service.DisposeAsync();
    }

    // ---- stop / dispose ordering ----------------------------------------------------------------

    [Fact]
    public async Task StopAsync_DisposesInOrder_EngineThenSourceThenSessionThenTranscriptionEngine()
    {
        var order = new List<string>();
        var fx = new Fixture(order);
        await fx.Service.StartAsync(MeetingUrl);
        order.Clear(); // ignore start-time activity; assert only teardown order

        await fx.Service.StopAsync();

        // Filter out the pre-dispose stop markers ("source-stop", "session-leave"); assert only the
        // DisposeAllAsync ordering: engine service → audio source → meeting session → transcription engine.
        var disposeOrder = order.Where(t => t is "engine" or "source" or "session" or "transcription").ToArray();
        Assert.Equal(
            new[] { "engine", "source", "session", "transcription" },
            disposeOrder);
        Assert.Equal(MeetingAttendeeState.Idle, fx.Service.State);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_FromIdle_DoesNotRaiseStateChanged()
    {
        var fx = new Fixture();

        await fx.Service.StopAsync();

        Assert.Empty(fx.Observed);
    }

    [Fact]
    public async Task StopAsync_StopsAudioBeforeLeavingMeeting()
    {
        var order = new List<string>();
        var fx = new Fixture(order);
        await fx.Service.StartAsync(MeetingUrl);
        order.Clear();

        await fx.Service.StopAsync();

        var sourceStopIdx = order.IndexOf("source-stop");
        var leaveIdx = order.IndexOf("session-leave");
        Assert.True(sourceStopIdx >= 0 && leaveIdx >= 0);
        Assert.True(sourceStopIdx < leaveIdx, "audio capture must stop before leaving the meeting");

        await fx.Service.DisposeAsync();
    }

    // ---- per-process decision (pure) ------------------------------------------------------------

    [Fact]
    public void UsePerProcessLoopback_TrueOnlyWhenFlagSetAndPidKnown()
    {
        var session = Substitute.For<IMeetingSession>();
        session.BrowserProcessId.Returns(1234);

        Assert.True(MeetingAttendeeService.UsePerProcessLoopback(
            new AppSettings { MeetingAttendeeUseProcessLoopback = true }, session));
        Assert.False(MeetingAttendeeService.UsePerProcessLoopback(
            new AppSettings { MeetingAttendeeUseProcessLoopback = false }, session));

        session.BrowserProcessId.Returns((int?)null);
        Assert.False(MeetingAttendeeService.UsePerProcessLoopback(
            new AppSettings { MeetingAttendeeUseProcessLoopback = true }, session));
    }

    [Theory]
    [InlineData(null, "Pia's assistant")]
    [InlineData("", "Pia's assistant")]
    [InlineData("   ", "Pia's assistant")]
    [InlineData("Alex", "Alex's assistant")]
    [InlineData("  Sam  ", "Sam's assistant")]
    public void BuildDisplayName_FormatsUsersAssistant(string? input, string expected)
    {
        Assert.Equal(expected, MeetingAttendeeService.BuildDisplayName(input));
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static async Task WaitForStateAsync(IMeetingAttendeeService service, MeetingAttendeeState target)
    {
        for (var i = 0; i < 200 && service.State != target; i++)
        {
            await Task.Delay(10);
        }
        Assert.Equal(target, service.State);
    }

    /// <summary>
    /// A test rig that wires the orchestrator's internal seam constructor to fully synchronous fakes,
    /// records each dispose step into an ordering list, and exposes the substituted session.
    /// </summary>
    private sealed class Fixture
    {
        private readonly List<string>? _order;

        public ISettingsService Settings { get; } = Substitute.For<ISettingsService>();
        public IMeetingSession Session { get; } = Substitute.For<IMeetingSession>();
        public FakeAudioSource AudioSource { get; }
        public List<MeetingAttendeeState> Observed { get; } = new();
        public TaskCompletionSource MeetingEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ProvisionThrows { get; init; }
        public bool SessionFactoryRan { get; private set; }
        public bool AudioSourceFactoryRan { get; private set; }
        public bool EngineBuilt { get; private set; }

        public MeetingAttendeeService Service { get; }

        public Fixture(List<string>? order = null)
        {
            _order = order;
            AudioSource = new FakeAudioSource(order);

            Settings.GetSettingsAsync().Returns(new AppSettings());

            Session.JoinAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            // Models the documented contract: WaitForEndAsync completes either when the meeting ends
            // (MeetingEnded.SetResult) OR when the supplied token cancels (StopAsync/DisposeAsync). A
            // fake that ignored the token would deadlock DisposeAsync while it awaits the watch loop.
            Session.WaitForEndAsync(Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var token = ci.Arg<CancellationToken>();
                    return token.CanBeCanceled
                        ? Task.WhenAny(MeetingEnded.Task, Task.Delay(Timeout.Infinite, token))
                        : MeetingEnded.Task;
                });
            Session.LeaveAsync().Returns(_ =>
            {
                order?.Add("session-leave");
                return Task.CompletedTask;
            });
            Session.DisposeAsync().Returns(_ =>
            {
                order?.Add("session");
                return ValueTask.CompletedTask;
            });

            var transcriptionEngine = Substitute.For<ITranscriptionEngine>();
            transcriptionEngine.DisposeAsync().Returns(_ =>
            {
                order?.Add("transcription");
                return ValueTask.CompletedTask;
            });

            Service = new MeetingAttendeeService(
                Settings,
                NullLoggerFactory.Instance,
                provisionChromium: (_, ct) =>
                {
                    if (ProvisionThrows) throw new InvalidOperationException("provision failed");
                    return Task.FromResult(@"C:\fake\chrome.exe");
                },
                createTranscription: _ => Task.FromResult(("silero.onnx", transcriptionEngine)),
                sessionFactory: _ =>
                {
                    SessionFactoryRan = true;
                    return Session;
                },
                audioSourceFactory: (_, _) =>
                {
                    AudioSourceFactoryRan = true;
                    return AudioSource;
                },
                engineServiceFactory: (_, _, _, _, _) =>
                {
                    EngineBuilt = true;
                    return Task.FromResult<IAsyncDisposable>(new RecordingDisposable(order, "engine"));
                });

            Service.StateChanged += (_, s) => Observed.Add(s);
        }
    }

    private sealed class RecordingDisposable : IAsyncDisposable
    {
        private readonly List<string>? _order;
        private readonly string _tag;
        public RecordingDisposable(List<string>? order, string tag) { _order = order; _tag = tag; }
        public ValueTask DisposeAsync()
        {
            _order?.Add(_tag);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAudioSource : IAudioCaptureSource
    {
        private readonly List<string>? _order;
        private readonly Channel<float[]> _channel = Channel.CreateUnbounded<float[]>();

        public FakeAudioSource(List<string>? order) => _order = order;

        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }

        public int SampleRate => 16000;
        public bool IsRunning => Started && !Stopped;
        public ChannelReader<float[]> Reader => _channel.Reader;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Stopped = true;
            _order?.Add("source-stop");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _order?.Add("source");
            return ValueTask.CompletedTask;
        }
    }
}

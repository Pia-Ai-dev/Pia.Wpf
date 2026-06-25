using System.Net.Http;
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

        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);

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

        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);

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

        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);

        await fx.Session.Received(1).JoinAsync(MeetingUrl, "Alex's assistant", Arg.Any<CancellationToken>());

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_WhenMeetingAttendeeDisplayNameSet_JoinsWithThatName_Trimmed()
    {
        var fx = new Fixture();
        // An explicit (user-edited, persisted) display name overrides the auto-built "{user}'s assistant"
        // and is trimmed before use.
        fx.Settings.GetSettingsAsync().Returns(new AppSettings
        {
            SyncUserDisplayName = "Alex",
            MeetingAttendeeDisplayName = "  Conference bot  ",
        });

        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);

        await fx.Session.Received(1).JoinAsync(MeetingUrl, "Conference bot", Arg.Any<CancellationToken>());

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_StartsAudioSourceAndBuildsEngineWithThemSpeaker()
    {
        var fx = new Fixture();

        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);

        Assert.True(fx.AudioSource.Started);
        Assert.True(fx.EngineBuilt);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_Twice_Throws()
    {
        var fx = new Fixture();
        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken));

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_NullOrWhitespaceUrl_Throws()
    {
        var fx = new Fixture();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fx.Service.StartAsync("  ", TestContext.Current.CancellationToken));
    }

    // ---- natural end ----------------------------------------------------------------------------

    [Fact]
    public async Task WhenMeetingEnds_AutoStopsToIdleAndDisposes()
    {
        var fx = new Fixture();
        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);

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

        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken));

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

        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken));

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

        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken));
        Assert.Equal(MeetingAttendeeState.Error, fx.Service.State);

        // Recover: clear the join failure and start cleanly.
        fx.Session.ClearSubstitute(ClearOptions.CallActions);
        fx.Session.JoinAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);
        Assert.Equal(MeetingAttendeeState.Attending, fx.Service.State);

        await fx.Service.DisposeAsync();
    }

    // ---- stop / dispose ordering ----------------------------------------------------------------

    [Fact]
    public async Task StopAsync_DisposesInOrder_EngineThenSourceThenSessionThenTranscriptionEngine()
    {
        var order = new List<string>();
        var fx = new Fixture(order);
        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);
        order.Clear(); // ignore start-time activity; assert only teardown order

        await fx.Service.StopAsync(TestContext.Current.CancellationToken);

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

        await fx.Service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Empty(fx.Observed);
    }

    [Fact]
    public async Task StopAsync_StopsAudioBeforeLeavingMeeting()
    {
        var order = new List<string>();
        var fx = new Fixture(order);
        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);
        order.Clear();

        await fx.Service.StopAsync(TestContext.Current.CancellationToken);

        var sourceStopIdx = order.IndexOf("source-stop");
        var leaveIdx = order.IndexOf("session-leave");
        Assert.True(sourceStopIdx >= 0 && leaveIdx >= 0);
        Assert.True(sourceStopIdx < leaveIdx, "audio capture must stop before leaving the meeting");

        await fx.Service.DisposeAsync();
    }

    // ---- degrade-to-null (BLOCKING-ISSUE #1 guard) ----------------------------------------------

    [Fact]
    public async Task StartAsync_WhenSpeakerIdNull_ReachesAttendingNotError()
    {
        // Seam-level guard: the production createTranscription closure degrades a failed speaker-model
        // setup to a null SpeakerId. The default fixture returns exactly that degraded 3-tuple
        // ("silero.onnx", engine, null), so a null SpeakerId must drive StartAsync to Attending (single-
        // bubble behavior) — NOT into the :233 catch that disposes and transitions to Error.
        var fx = new Fixture();

        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);

        Assert.Equal(MeetingAttendeeState.Attending, fx.Service.State);
        Assert.True(fx.EngineBuilt);
        Assert.DoesNotContain(MeetingAttendeeState.Error, fx.Observed);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task TryCreateSpeakerIdentificationAsync_WhenEnsureThrows_ReturnsNull_NotThrows()
    {
        // Closure-level guard for the actual try/catch: when EnableMeetingDiarization is true but the
        // ensure path throws (here: CreateClient throws before any HTTP), the helper must DEGRADE to null,
        // not propagate. EnsureSpeakerEmbeddingAsync short-circuits if the ~27 MB model already exists on
        // disk (it would then construct a real service and CreateClient is never reached) — skip in that
        // case so the test deterministically exercises the catch only when the download is actually attempted.
        if (LiveTranscriptionModels.IsSpeakerEmbeddingAvailable())
            Assert.Skip("Speaker-embedding model is cached on disk; the ensure path short-circuits before the throw.");

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => throw new InvalidOperationException("no network"));

        var result = await MeetingAttendeeService.TryCreateSpeakerIdentificationAsync(
            httpClientFactory,
            NullLoggerFactory.Instance,
            new AppSettings { EnableMeetingDiarization = true },
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryCreateSpeakerIdentificationAsync_WhenDiarizationDisabled_ReturnsNull_NoDownload()
    {
        // The gate lives inside the helper: with diarization off, it returns null without touching the
        // IHttpClientFactory at all (no download attempt).
        var httpClientFactory = Substitute.For<IHttpClientFactory>();

        var result = await MeetingAttendeeService.TryCreateSpeakerIdentificationAsync(
            httpClientFactory,
            NullLoggerFactory.Instance,
            new AppSettings { EnableMeetingDiarization = false },
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        httpClientFactory.DidNotReceive().CreateClient(Arg.Any<string>());
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

    [Fact]
    public void RenameSpeaker_IsSafeNoOp_WhenSpeakerIdNull()
    {
        // Before StartAsync (and after a degrade-to-null), _speakerId is null: RenameSpeaker must be a
        // silent no-op, never throw. The freshly-built Fixture service has not started, so _speakerId
        // is null.
        var fixture = new Fixture();

        var ex = Record.Exception(() => fixture.Service.RenameSpeaker("Speaker 2", "Marco"));

        Assert.Null(ex);
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
                // Degraded shape: a null SpeakerId is the production degrade-to-null result; the orchestrator
                // must treat it as a normal, non-fatal path. A bare null gives the tuple no inferable type,
                // so the element type is annotated explicitly.
                createTranscription: (_, _) => Task.FromResult<(string SileroPath, ITranscriptionEngine Engine, ISpeakerIdentificationService? SpeakerId)>(
                    ("silero.onnx", transcriptionEngine, null)),
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
                engineServiceFactory: (_, _, _, _, _, _) =>
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

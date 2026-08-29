using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Pia.Services.MeetingAttendee;
using Pia.Tests.TestInfrastructure;
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

        await fx.Session.Received(1).JoinAsync(MeetingUrl, "Alex's assistant (AI notetaker)", Arg.Any<CancellationToken>());

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

        await fx.Session.Received(1).JoinAsync(MeetingUrl, "Conference bot (AI notetaker)", Arg.Any<CancellationToken>());

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

    // ---- roster snapshots -----------------------------------------------------------------------

    [Fact]
    public async Task RosterSnapshots_AccumulateObservedAttendees_ExcludingBotName()
    {
        var fx = new Fixture();
        // Bot joins as "Alex's assistant"; roster also lists it, which must be filtered out.
        fx.Settings.GetSettingsAsync().Returns(new AppSettings
        {
            SyncUserDisplayName = "Alex",
            MeetingAttendeeRosterSnapshotMinutes = 1,
        });
        fx.Session.GetAttendeeNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(
                new[] { "Marco Altmann", "Jane Doe", "Alex's assistant" }));

        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);

        // The first snapshot fires immediately on the background roster loop; wait for it to land.
        await WaitForAttendeesAsync(fx.Service, 2);

        var attendees = fx.Service.ObservedAttendees;
        Assert.Contains("Marco Altmann", attendees);
        Assert.Contains("Jane Doe", attendees);
        Assert.DoesNotContain("Alex's assistant", attendees);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task RosterSnapshots_Disabled_WhenIntervalNotPositive()
    {
        var fx = new Fixture();
        fx.Settings.GetSettingsAsync().Returns(new AppSettings { MeetingAttendeeRosterSnapshotMinutes = 0 });
        fx.Session.GetAttendeeNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(new[] { "Marco Altmann" }));

        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);
        // Give any (erroneously-started) loop a chance to run before asserting it didn't.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(fx.Service.ObservedAttendees);
        await fx.Session.DidNotReceive().GetAttendeeNamesAsync(Arg.Any<CancellationToken>());

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task ObservedAttendees_RetainedAfterStop_ResetOnNextStart()
    {
        var fx = new Fixture();
        fx.Settings.GetSettingsAsync().Returns(new AppSettings { MeetingAttendeeRosterSnapshotMinutes = 1 });
        fx.Session.GetAttendeeNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(new[] { "Marco Altmann" }));

        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);
        await WaitForAttendeesAsync(fx.Service, 1);
        await fx.Service.StopAsync(TestContext.Current.CancellationToken);
        // Retained after stop, until the next start (the post-meeting summary still needs it).
        Assert.Contains("Marco Altmann", fx.Service.ObservedAttendees);

        // A new meeting with a different roster: the start clears the prior names (synchronously, before
        // the first new snapshot), so the old name must be gone once the new one lands.
        fx.Session.GetAttendeeNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(new[] { "Sven K." }));

        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);
        await WaitForAttendeesAsync(fx.Service, 1);

        Assert.Contains("Sven K.", fx.Service.ObservedAttendees);
        Assert.DoesNotContain("Marco Altmann", fx.Service.ObservedAttendees);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task RosterSnapshots_PushTheRosterSizeToTheDiarizer_AsASpeakerCeiling()
    {
        var speakerId = new FakeSpeakerId();
        var fx = new Fixture { SpeakerId = speakerId };
        fx.Settings.GetSettingsAsync().Returns(new AppSettings
        {
            SyncUserDisplayName = "Alex",
            MeetingAttendeeRosterSnapshotMinutes = 1,
        });
        fx.Session.GetAttendeeNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(
                new[] { "Marco Altmann", "Jane Doe", "Sven K.", "Alex's assistant" }));

        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);
        await WaitForExpectedSpeakersAsync(speakerId);

        // The bot's own row is excluded from the union, so the ceiling is the 3 real participants.
        Assert.Contains(3, speakerId.ExpectedSpeakers);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task RosterSnapshots_Disabled_LeaveTheDiarizerUnconstrained()
    {
        var speakerId = new FakeSpeakerId();
        var fx = new Fixture { SpeakerId = speakerId };
        fx.Settings.GetSettingsAsync().Returns(new AppSettings { MeetingAttendeeRosterSnapshotMinutes = 0 });

        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(speakerId.ExpectedSpeakers);

        await fx.Service.DisposeAsync();
    }

    [Theory]
    [InlineData("Marco Altmann", "Marco Altmann")]
    [InlineData("  Marco Altmann  ", "Marco Altmann")]
    [InlineData("Marco, Organizer", "Marco")]
    [InlineData("Marco\nMuted\nOrganizer", "Marco")]
    [InlineData("Jane (Guest)", "Jane")]
    [InlineData("Alex's assistant (You)", "Alex's assistant")]
    [InlineData("Marco, Organizer, Muted", "Marco")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void CleanAttendeeName_StripsStatusSuffixes(string? raw, string expected)
    {
        Assert.Equal(expected, MeetingAttendeeService.CleanAttendeeName(raw));
    }

    [Fact]
    public async Task RosterSnapshots_ExcludeBot_EvenWithSelfSuffix()
    {
        // Teams renders the self row as "<name> (You)"; after cleaning it collapses to the bot's plain
        // display name and must be excluded just like an exact match.
        var fx = new Fixture();
        fx.Settings.GetSettingsAsync().Returns(new AppSettings
        {
            SyncUserDisplayName = "Alex",
            MeetingAttendeeRosterSnapshotMinutes = 1,
        });
        fx.Session.GetAttendeeNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(
                new[] { "Alex's assistant (You)", "Marco, Organizer" }));

        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);
        await WaitForAttendeesAsync(fx.Service, 1);

        var attendees = fx.Service.ObservedAttendees;
        Assert.Contains("Marco", attendees);
        Assert.DoesNotContain("Alex's assistant", attendees);
        Assert.DoesNotContain("Alex's assistant (You)", attendees);

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

    // ---- silent-capture decision (pure) ---------------------------------------------------------

    [Fact]
    public void UseSilentBrowserCapture_TrueWhenHidden()
    {
        // Hidden (ShowBrowserWindow=false) ⇒ silent in-browser capture. Unlike the retired per-process
        // loopback, this needs no browser PID.
        Assert.True(MeetingAttendeeService.UseSilentBrowserCapture(
            new AppSettings { MeetingAttendeeShowBrowserWindow = false }));
        // Window shown ⇒ audible endpoint loopback.
        Assert.False(MeetingAttendeeService.UseSilentBrowserCapture(
            new AppSettings { MeetingAttendeeShowBrowserWindow = true }));
    }

    // ---- launch-spec resolution (Phase 0) -------------------------------------------------------

    [Fact]
    public async Task ResolveLaunchSpecAsync_Bundled_ProvisionsChromiumExecutableSpec()
    {
        var fx = new Fixture();

        var spec = await fx.Service.ResolveLaunchSpecAsync(
            new AppSettings { MeetingBrowserSelection = MeetingBrowserSelection.BundledChromium },
            TestContext.Current.CancellationToken);

        // Bundled launches by ExecutablePath (the provisioned chrome.exe), never by Channel; its match
        // path is that same unique exe, and the process to scan is "chrome".
        Assert.Equal(@"C:\fake\chrome.exe", spec.ExecutablePath);
        Assert.Equal(@"C:\fake\chrome.exe", spec.MatchExecutablePath);
        Assert.Null(spec.Channel);
        Assert.Equal("chrome", spec.ProcessName);
        Assert.False(spec.ShowWindow);
    }

    [Fact]
    public async Task StartAsync_SilentCaptureFailure_DegradesToLoopback_ForTheOverlaysSession()
    {
        var fx = new Fixture { SilentSourceThrows = true };

        await fx.Service.StartAsync("https://teams.microsoft.com/l/meetup-join/x",
            TestContext.Current.CancellationToken);

        // Asked for silent, it failed, so it asked again for the audible endpoint loopback: a hidden but
        // audible meeting beats a silent untranscribed one when only one meeting can be running.
        Assert.Equal([true, false], fx.AudioSourceRequests);
        Assert.Equal(MeetingAttendeeState.Attending, fx.Service.State);
    }

    [Fact]
    public async Task StartAsync_SilentCaptureFailure_FailsABackgroundSessionInsteadOfDegrading()
    {
        var fx = new Fixture { SilentSourceThrows = true };
        fx.Service.SilentCaptureOnly = true;

        await Assert.ThrowsAnyAsync<Exception>(() => fx.Service.StartAsync(
            "https://teams.microsoft.com/l/meetup-join/x", TestContext.Current.CancellationToken));

        // Exactly one attempt, and never the loopback one: that path records the whole system mix, so a
        // second concurrent meeting would land in this transcript too.
        Assert.Equal([true], fx.AudioSourceRequests);
        Assert.Equal(MeetingAttendeeState.Error, fx.Service.State);
    }

    [Fact]
    public async Task ResolveLaunchSpecAsync_BackgroundSession_StaysHidden_EvenWhenTheUserAsksForAWindow()
    {
        var fx = new Fixture();
        fx.Service.SilentCaptureOnly = true;

        var spec = await fx.Service.ResolveLaunchSpecAsync(
            new AppSettings
            {
                MeetingBrowserSelection = MeetingBrowserSelection.BundledChromium,
                MeetingAttendeeShowBrowserWindow = true,
            },
            TestContext.Current.CancellationToken);

        // A shown window is an AUDIBLE meeting captured off endpoint loopback, which is exactly what a
        // background session must never do — with a second meeting running it records both.
        Assert.False(spec.ShowWindow);
    }

    [Fact]
    public async Task ResolveLaunchSpecAsync_SystemChrome_UsesChromeChannel_NoProvision()
    {
        var fx = new Fixture();

        var spec = await fx.Service.ResolveLaunchSpecAsync(
            new AppSettings
            {
                MeetingBrowserSelection = MeetingBrowserSelection.SystemChrome,
                MeetingAttendeeShowBrowserWindow = true,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("chrome", spec.Channel);
        Assert.Null(spec.ExecutablePath);
        Assert.Equal("chrome", spec.ProcessName);
        Assert.True(spec.ShowWindow);
        // MatchExecutablePath is resolved from the live registry (App Paths) and so is environment-
        // dependent; not asserted here.
    }

    [Fact]
    public async Task ResolveLaunchSpecAsync_SystemEdge_UsesMsedgeChannel()
    {
        var fx = new Fixture();

        var spec = await fx.Service.ResolveLaunchSpecAsync(
            new AppSettings { MeetingBrowserSelection = MeetingBrowserSelection.SystemEdge },
            TestContext.Current.CancellationToken);

        Assert.Equal("msedge", spec.Channel);
        Assert.Null(spec.ExecutablePath);
        Assert.Equal("msedge", spec.ProcessName);
    }

    [Fact]
    public async Task ResolveLaunchSpecAsync_SystemDefault_WhenResolverReturnsBundled_LaunchesBundled()
    {
        // The default (test) resolver returns bundled, so SystemDefault provisions and launches by
        // ExecutablePath, not a Channel.
        var fx = new Fixture();

        var spec = await fx.Service.ResolveLaunchSpecAsync(
            new AppSettings { MeetingBrowserSelection = MeetingBrowserSelection.SystemDefault },
            TestContext.Current.CancellationToken);

        Assert.Equal(@"C:\fake\chrome.exe", spec.ExecutablePath);
        Assert.Null(spec.Channel);
    }

    [Fact]
    public async Task ResolveLaunchSpecAsync_SystemDefault_UsesResolverResult()
    {
        // SystemDefault delegates to the IDefaultBrowserResolver; an Edge result drives the msedge channel.
        var resolver = Substitute.For<IDefaultBrowserResolver>();
        resolver.ResolveChromiumSelectionOrBundled().Returns(MeetingBrowserSelection.SystemEdge);
        var fx = new Fixture(resolver: resolver);

        var spec = await fx.Service.ResolveLaunchSpecAsync(
            new AppSettings { MeetingBrowserSelection = MeetingBrowserSelection.SystemDefault },
            TestContext.Current.CancellationToken);

        Assert.Equal("msedge", spec.Channel);
        Assert.Null(spec.ExecutablePath);
    }

    // ---- channel-launch fallback (Phase 1) ------------------------------------------------------

    [Fact]
    public async Task StartAsync_WhenChannelBrowserFailsToLaunch_FallsBackToBundled_ReachesAttending()
    {
        // A system browser (Chrome channel) that fails to LAUNCH must degrade once to bundled Chromium
        // rather than failing the join. The failed session is disposed; the bundled session joins.
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync()
            .Returns(new AppSettings { MeetingBrowserSelection = MeetingBrowserSelection.SystemChrome });

        var failed = CreateJoinableSession();
        failed.JoinAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new BrowserLaunchException("launch failed", new InvalidOperationException()));
        var good = CreateJoinableSession();

        var specs = new List<BrowserLaunchSpec>();
        var queue = new Queue<IMeetingSession>([failed, good]);

        var transcriptionEngine = Substitute.For<ITranscriptionEngine>();
        var audioSource = new FakeAudioSource(null);

        var service = new MeetingAttendeeService(
            settings,
            NullLoggerFactory.Instance,
            provisionChromium: (_, _) => Task.FromResult(@"C:\fake\chrome.exe"),
            createTranscription: (_, _) => Task.FromResult<(string SileroPath, ITranscriptionEngine Engine, ISpeakerIdentificationService? SpeakerId)>(
                ("silero.onnx", transcriptionEngine, null)),
            sessionFactory: spec =>
            {
                specs.Add(spec);
                return queue.Dequeue();
            },
            audioSourceFactory: (_, _) => audioSource,
            engineServiceFactory: (_, _, _, _, _, _, _) =>
                Task.FromResult<IAsyncDisposable>(new RecordingDisposable(null, "engine")));

        await service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);

        Assert.Equal(MeetingAttendeeState.Attending, service.State);
        // First spec was the system Chrome channel; the fallback rebuilt with bundled (ExecutablePath set).
        Assert.Equal("chrome", specs[0].Channel);
        Assert.Equal(@"C:\fake\chrome.exe", specs[1].ExecutablePath);
        Assert.Null(specs[1].Channel);
        // The failed channel session was disposed; the bundled session is the live one.
        await failed.Received(1).DisposeAsync();
        await good.Received(1).JoinAsync(MeetingUrl, Arg.Any<string>(), Arg.Any<CancellationToken>());

        await service.DisposeAsync();
    }

    // ---- silent-capture dispose-then-degrade fallback -------------------------------------------

    [Fact]
    public async Task StartAsync_WhenSilentCaptureFailsToStart_DisposesItAndDegradesToEndpoint()
    {
        // Hidden window ⇒ silent in-browser capture is selected; when its StartAsync throws (e.g. the
        // in-page hook captured no remote track within the no-audio timeout), the orchestrator must
        // dispose that source FIRST (which unmutes the meeting), then start the audible endpoint
        // loopback, and still reach Attending ("hidden but audible").
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { MeetingAttendeeShowBrowserWindow = false });

        var session = CreateJoinableSession();

        var silent = new FakeAudioSource(null, throwOnStart: true);
        var endpoint = new FakeAudioSource(null);
        var transcriptionEngine = Substitute.For<ITranscriptionEngine>();

        var service = new MeetingAttendeeService(
            settings,
            NullLoggerFactory.Instance,
            provisionChromium: (_, _) => Task.FromResult(@"C:\fake\chrome.exe"),
            createTranscription: (_, _) => Task.FromResult<(string SileroPath, ITranscriptionEngine Engine, ISpeakerIdentificationService? SpeakerId)>(
                ("silero.onnx", transcriptionEngine, null)),
            sessionFactory: _ => session,
            // First call (useSilentCapture=true) returns the throwing source; the degrade call (false) the endpoint.
            audioSourceFactory: (_, useSilentCapture) => useSilentCapture ? silent : endpoint,
            engineServiceFactory: (_, _, _, _, _, _, _) =>
                Task.FromResult<IAsyncDisposable>(new RecordingDisposable(null, "engine")));

        await service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);

        Assert.Equal(MeetingAttendeeState.Attending, service.State);
        Assert.True(silent.Disposed);       // failed silent source disposed before reassigning (unmutes)
        Assert.True(endpoint.Started);      // degraded to the audible endpoint loopback
        Assert.True(endpoint.IsRunning);

        await service.DisposeAsync();
    }

    /// <summary>A session substitute that joins immediately and honours the WaitForEnd token so dispose
    /// never hangs (mirrors <see cref="Fixture"/>'s session wiring).</summary>
    private static IMeetingSession CreateJoinableSession()
    {
        var session = Substitute.For<IMeetingSession>();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.JoinAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        session.WaitForEndAsync(Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var token = ci.Arg<CancellationToken>();
                return token.CanBeCanceled
                    ? Task.WhenAny(ended.Task, Task.Delay(Timeout.Infinite, token))
                    : ended.Task;
            });
        session.LeaveAsync().Returns(Task.CompletedTask);
        session.DisposeAsync().Returns(ValueTask.CompletedTask);
        return session;
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

    [Theory]
    [InlineData("Alex's assistant", "Alex's assistant (AI notetaker)")]
    [InlineData("  Conference bot  ", "Conference bot (AI notetaker)")]
    [InlineData("Conference bot (AI notetaker)", "Conference bot (AI notetaker)")]
    [InlineData("Conference bot (ai NOTETAKER)", "Conference bot (ai NOTETAKER)")]
    [InlineData("", "Pia's assistant (AI notetaker)")]
    public void WithAiSuffix_AppendsTheSuffixExactlyOnce(string input, string expected)
    {
        Assert.Equal(expected, MeetingAttendeeService.WithAiSuffix(input, MeetingAttendeeService.DefaultAiSuffix));
    }

    [Fact]
    public void WithAiSuffix_ShortensALongName_SoTheSuffixSurvivesTheTeamsCap()
    {
        var name = MeetingAttendeeService.WithAiSuffix(new string('x', 80), MeetingAttendeeService.DefaultAiSuffix);

        Assert.True(name.Length <= MeetingAttendeeService.TeamsDisplayNameMaxLength, name);
        Assert.EndsWith("… (AI notetaker)", name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RosterSnapshots_ExcludeBot_WhenTeamsShowsTheSuffixedName()
    {
        // Teams renders the bot's own row as "<name> (AI notetaker) (You)"; the cleaner keeps the inner
        // parenthetical, and a row without "(You)" loses the suffix instead — both must still be the bot.
        var fx = new Fixture();
        fx.Settings.GetSettingsAsync().Returns(new AppSettings
        {
            SyncUserDisplayName = "Alex",
            MeetingAttendeeRosterSnapshotMinutes = 1,
        });
        fx.Session.GetAttendeeNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(
                new[] { "Alex's assistant (AI notetaker) (You)", "Alex's assistant (AI notetaker)", "Jane Doe" }));

        await fx.Service.StartAsync(MeetingUrl, TestContext.Current.CancellationToken);
        await WaitForAttendeesAsync(fx.Service, 1);

        Assert.Equal(new[] { "Jane Doe" }, fx.Service.ObservedAttendees);

        await fx.Service.DisposeAsync();
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
        await Eventually.TrueAsync(() => service.State == target, $"the service to reach {target}",
            TestContext.Current.CancellationToken);
        Assert.Equal(target, service.State);
    }

    private static async Task WaitForAttendeesAsync(IMeetingAttendeeService service, int atLeast)
    {
        await Eventually.TrueAsync(() => service.ObservedAttendees.Count >= atLeast,
            $"at least {atLeast} observed attendees", TestContext.Current.CancellationToken);
        Assert.True(service.ObservedAttendees.Count >= atLeast,
            $"Expected at least {atLeast} observed attendees, saw {service.ObservedAttendees.Count}.");
    }

    private static async Task WaitForExpectedSpeakersAsync(FakeSpeakerId speakerId)
    {
        await Eventually.TrueAsync(() => speakerId.ExpectedSpeakers.Count > 0, "an expected-speaker hint",
            TestContext.Current.CancellationToken);
        Assert.NotEmpty(speakerId.ExpectedSpeakers);
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

        /// <summary>Null models the production degrade-to-null; set it to observe diarizer wiring.</summary>
        public ISpeakerIdentificationService? SpeakerId { get; init; }

        public bool SessionFactoryRan { get; private set; }
        public bool AudioSourceFactoryRan { get; private set; }

        /// <summary>Makes the in-browser tap fail to start, which is what triggers the loopback degrade.</summary>
        public bool SilentSourceThrows { get; init; }

        /// <summary>The useSilentCapture argument of every audio source asked for, in order.</summary>
        public List<bool> AudioSourceRequests { get; } = new();
        public bool EngineBuilt { get; private set; }

        public MeetingAttendeeService Service { get; }

        public Fixture(List<string>? order = null, IDefaultBrowserResolver? resolver = null)
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
                // Degraded shape by default: a null SpeakerId is the production degrade-to-null result; the
                // orchestrator must treat it as a normal, non-fatal path. A bare null gives the tuple no
                // inferable type, so the element type is annotated explicitly.
                createTranscription: (_, _) => Task.FromResult<(string SileroPath, ITranscriptionEngine Engine, ISpeakerIdentificationService? SpeakerId)>(
                    ("silero.onnx", transcriptionEngine, SpeakerId)),
                sessionFactory: _ =>
                {
                    SessionFactoryRan = true;
                    return Session;
                },
                audioSourceFactory: (_, useSilentCapture) =>
                {
                    AudioSourceFactoryRan = true;
                    AudioSourceRequests.Add(useSilentCapture);
                    return SilentSourceThrows && useSilentCapture
                        ? new FakeAudioSource(order, throwOnStart: true)
                        : AudioSource;
                },
                engineServiceFactory: (_, _, _, _, _, _, _) =>
                {
                    EngineBuilt = true;
                    return Task.FromResult<IAsyncDisposable>(new RecordingDisposable(order, "engine"));
                },
                defaultBrowserResolver: resolver);

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

    /// <summary>Records the roster-derived speaker ceilings the orchestrator pushes. Pushes arrive on
    /// the background roster loop, hence the concurrent queue. <see cref="SpeakersReassigned"/> has
    /// empty accessors to dodge CS0067 — this fake never diarizes anything.</summary>
    private sealed class FakeSpeakerId : ISpeakerIdentificationService
    {
        private readonly ConcurrentQueue<int> _expectedSpeakers = new();

        public IReadOnlyCollection<int> ExpectedSpeakers => _expectedSpeakers.ToArray();

        public event EventHandler<string>? SpeakerRegistered { add { } remove { } }
        public event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned { add { } remove { } }

        public string IdentifyOrRegister(float[] segmentSamples, int sampleRate) => "Speaker 1";

        public (string Label, float[] Embedding) IdentifyOrRegisterWithEmbedding(float[] segmentSamples, int sampleRate)
            => ("Speaker 1", Array.Empty<float>());

        public SpeakerSegmentResult IdentifyOrRegisterSegment(float[] segmentSamples, int sampleRate)
            => new(0, "Speaker 1");

        public bool Rename(string oldLabel, string newLabel) => false;

        public void Reset() { }

        public void SetExpectedSpeakers(int count) => _expectedSpeakers.Enqueue(count);

        public void Dispose() { }
    }

    private sealed class FakeAudioSource : IAudioCaptureSource
    {
        private readonly List<string>? _order;
        private readonly bool _throwOnStart;
        private readonly Channel<float[]> _channel = Channel.CreateUnbounded<float[]>();

        public FakeAudioSource(List<string>? order, bool throwOnStart = false)
        {
            _order = order;
            _throwOnStart = throwOnStart;
        }

        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }

        public int SampleRate => 16000;
        public bool IsRunning => Started && !Stopped;
        public ChannelReader<float[]> Reader => _channel.Reader;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            // Models a per-process loopback activation failure (e.g. Windows < 20348) so the orchestrator's
            // dispose-then-degrade fallback can be exercised.
            if (_throwOnStart)
                throw new PlatformNotSupportedException("per-process loopback unsupported");
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

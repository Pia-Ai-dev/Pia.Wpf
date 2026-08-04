using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Consent;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Exercises <see cref="DirectTranscriptionService"/>'s state machine, its fresh-raw-channel-per-start
/// behaviour (D2 regression), and its teardown ordering, entirely through the internal seam
/// constructor — no real audio device, model, or native diarizer is ever constructed. Mirrors the
/// <c>MeetingAttendeeServiceStateTests</c> fixture pattern (that file belongs to another module and is
/// off-limits to edit; this module's fakes live in <c>DirectTranscriptionTestDoubles.cs</c>).
/// </summary>
public sealed class DirectTranscriptionServiceTests
{
    [Fact]
    public async Task PrepareStartStopEndSession_ProducesExpectedStateSequence()
    {
        var fx = new Fixture();

        await fx.Service.PrepareAsync(TestContext.Current.CancellationToken);
        await fx.Service.StartAsync(TestContext.Current.CancellationToken);
        await fx.Service.StopAsync(TestContext.Current.CancellationToken);
        await fx.Service.EndSessionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new[]
            {
                DirectTranscriptionState.Preparing,
                DirectTranscriptionState.Prepared,
                DirectTranscriptionState.Starting,
                DirectTranscriptionState.Running,
                DirectTranscriptionState.Stopping,
                DirectTranscriptionState.Prepared,
                DirectTranscriptionState.Idle,
            },
            fx.Observed);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task SpeakerModelFailure_Throws_AndLeavesStateError()
    {
        var fx = new Fixture { CreateTranscriptionThrows = true };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fx.Service.PrepareAsync(TestContext.Current.CancellationToken));

        Assert.Equal(DirectTranscriptionState.Error, fx.Service.State);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public void StartAsync_UsesTheManualDiarizer_EvenWhenMeetingSmartSpeakerDetectionIsTrue()
    {
        // The production create-transcription closure is not independently invokable (it lives inside
        // the public constructor and needs real network/model IO), so the "always manual, never
        // adaptive" decision is pinned as its own pure function instead — see the doc comment on
        // ShouldUseAdaptiveDiarizer for why this, and not a branch inlined at the call site, is what
        // production code actually calls.
        var settings = new AppSettings { MeetingSmartSpeakerDetection = true };

        Assert.False(DirectTranscriptionService.ShouldUseAdaptiveDiarizer(settings));
    }

    [Fact]
    public async Task StopThenStart_ProducesUtterancesAgain_D2Regression()
    {
        var fx = new Fixture();
        await fx.Service.StartAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(fx.MicSink);
        await fx.MicSink!.WriteAsync(
            new TranscriptUtterance(TranscriptSpeaker.You, "first", DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        var readFirst = await fx.Service.Utterances.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("first", readFirst.Text);

        await fx.Service.StopAsync(TestContext.Current.CancellationToken);
        await fx.Service.StartAsync(TestContext.Current.CancellationToken);

        // The old D2 bug: the raw channel was a readonly field completed by the first Stop, so this
        // second write went into an already-closed writer and nothing ever arrived.
        Assert.NotNull(fx.MicSink);
        await fx.MicSink!.WriteAsync(
            new TranscriptUtterance(TranscriptSpeaker.You, "second", DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        var readSecond = await fx.Service.Utterances.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("second", readSecond.Text);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task ConsentSurvivesAStopResume_ButNotEndSession()
    {
        var fx = new Fixture();

        await fx.Service.StartAsync(TestContext.Current.CancellationToken);
        await fx.Service.StopAsync(TestContext.Current.CancellationToken);
        fx.Consent.Received(1).ResetSession(); // only the initial PrepareAsync so far

        await fx.Service.StartAsync(TestContext.Current.CancellationToken);
        await fx.Service.StopAsync(TestContext.Current.CancellationToken);
        fx.Consent.Received(1).ResetSession(); // still just once: Stop/Resume preserves consent

        await fx.Service.EndSessionAsync(TestContext.Current.CancellationToken);
        fx.Consent.Received(2).ResetSession(); // EndSessionAsync resets the session

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task PublicChannelReader_IsTheSameInstanceAcrossStartStopCycles()
    {
        var fx = new Fixture();
        var readerBefore = fx.Service.Utterances;

        await fx.Service.StartAsync(TestContext.Current.CancellationToken);
        await fx.Service.StopAsync(TestContext.Current.CancellationToken);
        await fx.Service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Same(readerBefore, fx.Service.Utterances);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_DisposesEnginesBeforeCompletingTheRawChannel()
    {
        var fx = new Fixture();
        // Models the real engine's behaviour: its trailing-segment sink write happens INSIDE
        // DisposeAsync. If the raw channel were completed before this dispose runs, the write would
        // throw ChannelClosedException and the utterance would be silently lost.
        fx.MicEngineOnDispose = () => fx.MicSink!.WriteAsync(
            new TranscriptUtterance(TranscriptSpeaker.You, "trailing", DateTimeOffset.UtcNow),
            CancellationToken.None).AsTask();

        await fx.Service.StartAsync(TestContext.Current.CancellationToken);
        await fx.Service.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(fx.Service.Utterances.TryRead(out var trailing));
        Assert.Equal("trailing", trailing.Text);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task DisposeOrder_DiarizerIsDisposedLast()
    {
        var fx = new Fixture();
        await fx.Service.PrepareAsync(TestContext.Current.CancellationToken);

        await fx.Service.EndSessionAsync(TestContext.Current.CancellationToken);

        var engineIndex = fx.Order.IndexOf("transcription-engine");
        var speakerIdIndex = fx.Order.IndexOf("speaker-id");
        Assert.True(engineIndex >= 0, "the shared transcription engine must have been disposed");
        Assert.True(speakerIdIndex >= 0, "the diarizer must have been disposed");
        Assert.True(engineIndex < speakerIdIndex, "the diarizer must be disposed strictly after the shared engine");

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_FromIdle_RaisesNothing_AndIsSafe()
    {
        var fx = new Fixture();

        await fx.Service.StopAsync(TestContext.Current.CancellationToken);
        // Regression guard: a state machine that dispatched transitions via Task.Run could pass this
        // assertion by coincidence if checked too early.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(fx.Observed);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentStopAsync_DisposesEachResourceExactlyOnce()
    {
        var fx = new Fixture();
        await fx.Service.StartAsync(TestContext.Current.CancellationToken);

        await Task.WhenAll(
            fx.Service.StopAsync(TestContext.Current.CancellationToken),
            fx.Service.StopAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, fx.Order.Count(tag => tag == "mic-engine"));
        Assert.Equal(1, fx.Order.Count(tag => tag == "loopback-engine"));
        Assert.Equal(1, fx.Order.Count(tag => tag == "mic-source"));
        Assert.Equal(1, fx.Order.Count(tag => tag == "loopback-source"));

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task SpeakerRegistered_RegistersTheSpeakerAsUnknown_AndAuditsSpeakerDetected()
    {
        var fx = new Fixture();
        var observedLabels = new List<string>();
        fx.Service.SpeakerRegistered += (_, label) => observedLabels.Add(label);

        await fx.Service.PrepareAsync(TestContext.Current.CancellationToken);
        fx.SpeakerId.RaiseSpeakerRegistered("Speaker 1");

        Assert.Contains("Speaker 1", observedLabels);
        fx.Consent.Received(1).GetOrCreate("Speaker 1");
        Assert.Contains(
            fx.AuditEvents,
            e => e.EventType == ConsentAuditEventTypes.SpeakerDetected && e.SpeakerLabel == "Speaker 1");

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_AuditsTheBatchedDropCounters()
    {
        var fx = new Fixture();
        fx.Classifier.Classify(Arg.Any<string>(), Arg.Any<TargetSpeechLanguage>())
            .Returns(NamedConsentResult.NoConsent("en"));

        await fx.Service.StartAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(fx.LoopbackSink);
        await fx.LoopbackSink!.WriteAsync(
            new TranscriptUtterance(TranscriptSpeaker.Them, "unlabeled", DateTimeOffset.UtcNow, SpeakerLabel: null),
            TestContext.Current.CancellationToken);
        await fx.LoopbackSink!.WriteAsync(
            new TranscriptUtterance(TranscriptSpeaker.Them, "no consent here", DateTimeOffset.UtcNow, SpeakerLabel: "Speaker 1"),
            TestContext.Current.CancellationToken);

        // StopAsync awaits the forward-loop task to full completion, which only happens after every
        // already-buffered raw-channel item (both writes above) has been processed — no manual wait
        // needed before asserting the batched counters below.
        await fx.Service.StopAsync(TestContext.Current.CancellationToken);

        var stopped = Assert.Single(fx.AuditEvents, e => e.EventType == ConsentAuditEventTypes.SessionStopped);
        Assert.NotNull(stopped.Details);
        Assert.Equal(1, (int)stopped.Details!["droppedUnlabeledLoopback"]!);
        Assert.Equal(1, (int)stopped.Details!["droppedUnconsented"]!);

        await fx.Service.DisposeAsync();
    }

    // -------------------------------------------------------------------------------------------
    // RenameSpeaker: the composite (consent map + diarizer + samples) rename must be all-or-nothing
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task RenameSpeaker_OnSuccess_RenamesBothTheConsentMapAndTheDiarizer()
    {
        var fx = new Fixture(useRealConsentManager: true);
        await fx.Service.PrepareAsync(TestContext.Current.CancellationToken);
        fx.SpeakerId.RaiseSpeakerRegistered("Speaker 2");

        Assert.True(fx.Service.RenameSpeaker("Speaker 2", "Bob"));

        Assert.Contains(("Speaker 2", "Bob"), fx.SpeakerId.Renames);
        Assert.True(fx.RealConsent!.TryGet("Bob", out _));
        Assert.False(fx.RealConsent!.TryGet("Speaker 2", out _));

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task RenameSpeaker_OntoAnAlreadyKnownLabel_IsRefused_AndDoesNotTouchTheDiarizer()
    {
        // THE consent-bypass this guards: renaming an unconsented cluster onto an already-GRANTED label
        // used to succeed in the diarizer (which had no collision check) and fail in the consent map, with
        // no rollback. Every later segment of the unconsented person then arrived carrying the granted
        // label, the gate read Granted for it, and their speech was transcribed into the visible
        // transcript, the saved Markdown and the voice statistics under someone else's consent record.
        var fx = new Fixture(useRealConsentManager: true);
        await fx.Service.PrepareAsync(TestContext.Current.CancellationToken);
        fx.SpeakerId.RaiseSpeakerRegistered("Marco");     // stands in for an already-granted speaker
        fx.SpeakerId.RaiseSpeakerRegistered("Speaker 2"); // an unconsented cluster
        fx.RealConsent!.Grant("Marco", "Marco", Evidence("Speaker 1", "Marco"));
        fx.SpeakerId.Renames.Clear();

        Assert.False(fx.Service.RenameSpeaker("Speaker 2", "Marco"));

        // Nothing was mutated on either side: the diarizer was never asked, and both keys are unchanged.
        Assert.Empty(fx.SpeakerId.Renames);
        Assert.Equal(ConsentState.Granted, fx.RealConsent!.CurrentState("Marco"));
        Assert.Equal(ConsentState.Unknown, fx.RealConsent!.CurrentState("Speaker 2"));

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task RenameSpeaker_WhenTheDiarizerRefuses_RollsTheConsentMapBack()
    {
        var fx = new Fixture(useRealConsentManager: true);
        await fx.Service.PrepareAsync(TestContext.Current.CancellationToken);
        fx.SpeakerId.RaiseSpeakerRegistered("Speaker 2");
        fx.SpeakerId.RenameSucceeds = false;

        Assert.False(fx.Service.RenameSpeaker("Speaker 2", "Bob"));

        // The two sides must never diverge: a consent entry keyed "Bob" with no diarizer label pointing at
        // it would be dead, and a diarizer label with no consent entry silently drops that speaker.
        Assert.True(fx.RealConsent!.TryGet("Speaker 2", out _));
        Assert.False(fx.RealConsent!.TryGet("Bob", out _));

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task RenameSpeaker_RejectsBlankAndIdenticalLabels()
    {
        var fx = new Fixture(useRealConsentManager: true);
        await fx.Service.PrepareAsync(TestContext.Current.CancellationToken);
        fx.SpeakerId.RaiseSpeakerRegistered("Speaker 2");

        Assert.False(fx.Service.RenameSpeaker("Speaker 2", "   "));
        Assert.False(fx.Service.RenameSpeaker("Speaker 2", "Speaker 2"));
        Assert.Empty(fx.SpeakerId.Renames);

        await fx.Service.DisposeAsync();
    }

    // -------------------------------------------------------------------------------------------
    // RevokeSpeaker
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task RevokeSpeaker_AuditsUnderTheGrantsLabel_NeverTheExtractedName()
    {
        // After a grant-time rename the consent-map key IS the extracted personal name, so a revoke driven
        // from the UI carries that name. It must not reach the plaintext JSONL audit trail, nor become the
        // evidence FILE NAME (DPAPI protects the contents, not the name) — and keying the revocation by the
        // grant's own label is also what keeps the Art. 7 record correlatable with the grant it revokes.
        var fx = new Fixture(useRealConsentManager: true);
        await fx.Service.PrepareAsync(TestContext.Current.CancellationToken);
        fx.RealConsent!.Grant("Anna", "Anna", Evidence("Speaker 2", "Anna"));

        fx.Service.RevokeSpeaker("Anna");

        var revoked = Assert.Single(fx.AuditEvents, e => e.EventType == ConsentAuditEventTypes.ConsentRevoked);
        Assert.Equal("Speaker 2", revoked.SpeakerLabel);
        await fx.EvidenceStore.Received(1).SaveRevocationAsync(
            Arg.Any<string>(), "Speaker 2", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task RevokeSpeaker_OnANonGrantedLabel_IsASilentNoOp_AndCreatesNoEntry()
    {
        var fx = new Fixture(useRealConsentManager: true);
        await fx.Service.PrepareAsync(TestContext.Current.CancellationToken);

        fx.Service.RevokeSpeaker("Never Detected");

        Assert.DoesNotContain(fx.AuditEvents, e => e.EventType == ConsentAuditEventTypes.ConsentRevoked);
        // No phantom entry keyed by a (possibly personal) UI label may be created by a failed revoke.
        Assert.DoesNotContain(fx.RealConsent!.Snapshot(), e => e.SpeakerLabel == "Never Detected");

        await fx.Service.DisposeAsync();
    }

    // -------------------------------------------------------------------------------------------
    // Session lifecycle
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task RePrepareAfterAFailedStart_DisposesThePreviousSessionsNatives()
    {
        // A failed StartAsync leaves the session in Error with its shared engine and native diarizer still
        // alive (teardown-run deliberately does not touch them). The retry routes through PrepareAsync,
        // which must release them before provisioning new ones — otherwise each Start-failure/retry cycle
        // leaked one sherpa recognizer + one native diarizer that no teardown path could reach again.
        var fx = new Fixture { MicSourceThrowsOnStart = true };

        await Assert.ThrowsAnyAsync<Exception>(
            () => fx.Service.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DirectTranscriptionState.Error, fx.Service.State);
        var firstEngine = fx.TranscriptionEngine;
        var firstSpeakerId = fx.SpeakerId;
        Assert.False(firstEngine.Disposed);
        Assert.False(firstSpeakerId.Disposed);

        fx.MicSourceThrowsOnStart = false;
        fx.NewNativesPerCreate = true;
        await fx.Service.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(firstEngine.Disposed, "the previous session's shared engine must be disposed on re-prepare");
        Assert.True(firstSpeakerId.Disposed, "the previous session's diarizer must be disposed on re-prepare");
        Assert.NotSame(firstEngine, fx.TranscriptionEngine);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task RePrepare_RaisesConsentSessionReset_SoConsumersCanDropStaleChips()
    {
        var fx = new Fixture();
        var resets = 0;
        fx.Service.ConsentSessionReset += (_, _) => resets++;

        await fx.Service.PrepareAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, resets);

        await fx.Service.EndSessionAsync(TestContext.Current.CancellationToken);
        await fx.Service.PrepareAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, resets);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task StopWhileStarting_DoesNotLeaveTheServiceReportingRunning()
    {
        // A stop arriving mid-start used to tear down the sources and complete the raw channel while the
        // start kept building over them and then unconditionally reported Running — a session that said
        // "Listening" and produced nothing at all until the next stop/resume.
        var fx = new Fixture();
        await fx.Service.PrepareAsync(TestContext.Current.CancellationToken);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.MicEngineFactoryGate = gate.Task;

        var start = fx.Service.StartAsync(TestContext.Current.CancellationToken);
        // The start is now parked inside the mic-engine factory. Stop must not interleave with it.
        var stop = fx.Service.StopAsync(TestContext.Current.CancellationToken);
        gate.SetResult();

        try { await start; } catch (OperationCanceledException) { /* a stop-cancelled start */ }
        await stop;

        // Back to the state the session was already in — never Running, and never Running at any point.
        Assert.Equal(DirectTranscriptionState.Prepared, fx.Service.State);
        Assert.DoesNotContain(DirectTranscriptionState.Running, fx.Observed);
        // And the half-built run really was unwound: an orphaned, still-recording microphone that no
        // teardown path can reach is the actual harm this guards against.
        Assert.Contains("mic-source", fx.Order);

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task EndSessionAsync_DrainsThePublicChannel_SoNothingCrossesIntoTheNextSession()
    {
        // The public channel outlives every session (its reader must stay stable across stop/resume), and
        // the view model cancels its consumer on stop without draining. An undelivered trailing utterance
        // would therefore be handed to the NEXT session's transcript — carrying the previous session's
        // speaker label, after the consent map had already been reset.
        var fx = new Fixture();
        await fx.Service.StartAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(fx.MicSink);
        await fx.MicSink!.WriteAsync(
            new TranscriptUtterance(TranscriptSpeaker.You, "left over", DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        await fx.Service.StopAsync(TestContext.Current.CancellationToken);
        await fx.Service.EndSessionAsync(TestContext.Current.CancellationToken);

        Assert.False(fx.Service.Utterances.TryRead(out _), "nothing may survive a session end");

        await fx.Service.DisposeAsync();
    }

    [Fact]
    public async Task EndSessionAsync_WhilePreparing_AbortsThePrepare_AndDoesNotWaitItOut()
    {
        // Two requirements at once. (a) The session teardown MUST NOT run while a prepare is still
        // assigning: a completing prepare would write a live sherpa recognizer and a native ONNX diarizer
        // into a session nothing will ever tear down again. (b) It must not simply WAIT for it either —
        // EndSessionAsync is reached from a synchronous Dispose on the UI thread, so waiting out a
        // first-run model download would freeze the window. The prepare is therefore cancelled, then
        // awaited.
        var fx = new Fixture { BlockCreateTranscription = true };

        var prepare = fx.Service.PrepareAsync(CancellationToken.None);
        await WaitForStateAsync(fx, DirectTranscriptionState.Preparing);

        // No timeout wrapper: if this ever hangs, it is the defect, and xunit's session timeout reports it.
        await fx.Service.EndSessionAsync(CancellationToken.None);

        Assert.Equal(DirectTranscriptionState.Idle, fx.Service.State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);

        await fx.Service.DisposeAsync();
    }

    private static async Task WaitForStateAsync(Fixture fx, DirectTranscriptionState expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (fx.Service.State != expected)
        {
            Assert.True(DateTime.UtcNow < deadline, $"state never reached {expected}; last was {fx.Service.State}");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static ConsentEvidence Evidence(string speakerLabel, string extractedName) => new(
        speakerLabel,
        extractedName,
        "My name is X and I accept that this is recorded by Pia.",
        "en",
        NamedConsentClassifier.CrispConfidence,
        DateTimeOffset.UtcNow,
        "fake-stt");

    /// <summary>
    /// Wires the internal seam constructor to fully synchronous fakes, records dispose steps into a
    /// shared ordering list, and exposes the raw-channel sink each engine-service factory call was
    /// handed (so tests can push utterances straight into the gate, exactly like the real engines do).
    /// </summary>
    private sealed class Fixture
    {
        public List<string> Order { get; } = new();
        public List<DirectTranscriptionState> Observed { get; } = new();
        public List<AuditEvent> AuditEvents { get; } = new();

        public ISettingsService Settings { get; } = Substitute.For<ISettingsService>();
        public INamedConsentClassifier Classifier { get; } = Substitute.For<INamedConsentClassifier>();
        public IConsentAuditLog AuditLog { get; } = Substitute.For<IConsentAuditLog>();
        public IConsentEvidenceStore EvidenceStore { get; } = Substitute.For<IConsentEvidenceStore>();

        /// <summary>Substituted consent manager (the default). Null when <see cref="UseRealConsentManager"/>.</summary>
        public IConsentStateManager Consent { get; }

        /// <summary>
        /// The REAL consent manager, when <see cref="UseRealConsentManager"/> was set. Rename/revoke tests
        /// need actual state-machine behaviour (collision refusal, the Granted-only revoke guard) rather
        /// than a scripted double.
        /// </summary>
        public ConsentStateManager? RealConsent { get; }

        public FakeSpeakerIdentificationService SpeakerId { get; private set; }
        public FakeTranscriptionEngine TranscriptionEngine { get; private set; }

        public bool CreateTranscriptionThrows { get; init; }

        /// <summary>Makes the mic capture source fail to start, modelling an absent or exclusively-held device.</summary>
        public bool MicSourceThrowsOnStart { get; set; }

        /// <summary>Parks create-transcription until its token is cancelled, modelling a first-run model
        /// download that takes far longer than a user is willing to wait at window close.</summary>
        public bool BlockCreateTranscription { get; set; }

        /// <summary>When set, each create-transcription call hands out FRESH natives, so a test can assert
        /// the previous session's instances were disposed rather than overwritten.</summary>
        public bool NewNativesPerCreate { get; set; }

        /// <summary>When set, the mic engine-service factory awaits this before returning — a hook for
        /// parking a start mid-construction.</summary>
        public Task? MicEngineFactoryGate { get; set; }

        public ChannelWriter<TranscriptUtterance>? MicSink { get; private set; }
        public ChannelWriter<TranscriptUtterance>? LoopbackSink { get; private set; }
        public Func<Task>? MicEngineOnDispose { get; set; }
        public Func<Task>? LoopbackEngineOnDispose { get; set; }

        public DirectTranscriptionService Service { get; }

        /// <param name="useRealConsentManager">
        /// A constructor parameter rather than an <c>init</c> property on purpose: <c>init</c> setters run
        /// AFTER the constructor body, so the field this decides could not be read here.
        /// </param>
        public Fixture(bool useRealConsentManager = false)
        {
            SpeakerId = new FakeSpeakerIdentificationService(Order, "speaker-id");
            TranscriptionEngine = new FakeTranscriptionEngine(Order, "transcription-engine");

            if (useRealConsentManager)
            {
                RealConsent = new ConsentStateManager(
                    NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
                Consent = RealConsent;
            }
            else
            {
                Consent = Substitute.For<IConsentStateManager>();
            }

            Settings.GetSettingsAsync().Returns(new AppSettings());
            AuditLog.When(a => a.Append(Arg.Any<AuditEvent>())).Do(ci => AuditEvents.Add(ci.Arg<AuditEvent>()));
            AuditLog.DisposeAsync().Returns(_ =>
            {
                Order.Add("audit-log");
                return ValueTask.CompletedTask;
            });

            Service = new DirectTranscriptionService(
                Settings,
                NullLoggerFactory.Instance,
                Consent,
                Classifier,
                AuditLog,
                EvidenceStore,
                createTranscription: async ct =>
                {
                    if (CreateTranscriptionThrows)
                        throw new InvalidOperationException("speaker model failed");
                    if (BlockCreateTranscription)
                        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                    if (NewNativesPerCreate)
                    {
                        SpeakerId = new FakeSpeakerIdentificationService(Order, "speaker-id");
                        TranscriptionEngine = new FakeTranscriptionEngine(Order, "transcription-engine");
                    }
                    return ("silero.onnx", (ITranscriptionEngine)TranscriptionEngine,
                        (ISpeakerIdentificationService)SpeakerId, "fake-stt");
                },
                micSourceFactory: () => new FakeAudioSource(Order, "mic-source", throwOnStart: MicSourceThrowsOnStart),
                loopbackSourceFactory: () => new FakeAudioSource(Order, "loopback-source"),
                engineServiceFactory: async (speaker, source, vadPath, engine, sink, speakerId, ct) =>
                {
                    if (speaker == TranscriptSpeaker.You)
                    {
                        MicSink = sink;
                        if (MicEngineFactoryGate is not null)
                            await MicEngineFactoryGate.ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        return new RecordingDisposable(Order, "mic-engine", MicEngineOnDispose);
                    }
                    LoopbackSink = sink;
                    ct.ThrowIfCancellationRequested();
                    return new RecordingDisposable(Order, "loopback-engine", LoopbackEngineOnDispose);
                });

            Service.StateChanged += (_, s) => Observed.Add(s);
        }
    }
}

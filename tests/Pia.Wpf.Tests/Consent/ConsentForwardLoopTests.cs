using System.IO;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services.Consent;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Consent;

/// <summary>
/// Drives <see cref="ConsentForwardLoop.ProcessAsync"/> directly — this is THE privacy boundary, so
/// every test here measures what does, and does not, reach the sink, the audit log, or the evidence
/// store. Uses a REAL <see cref="ConsentStateManager"/> (actual state-machine behaviour, not a scripted
/// double) and substituted classifier/audit/evidence dependencies.
/// </summary>
public sealed class ConsentForwardLoopTests
{
    private const string SessionId = "session-1";
    private const string SttModelId = "fake-stt";

    private static ConsentSessionContext Context(TargetSpeechLanguage hint = TargetSpeechLanguage.EN)
        => new(SessionId, SttModelId, hint);

    private static TranscriptUtterance Mic(string text, double duration = 1.0)
        => new(TranscriptSpeaker.You, text, DateTimeOffset.UtcNow, SpeakerLabel: null, SegmentId: null, DurationSeconds: duration);

    private static TranscriptUtterance Loopback(string label, string text, double duration = 1.0)
        => new(TranscriptSpeaker.Them, text, DateTimeOffset.UtcNow, SpeakerLabel: label, SegmentId: 1, DurationSeconds: duration);

    private static TranscriptUtterance LoopbackUnlabeled(string text, double duration = 1.0)
        => new(TranscriptSpeaker.Them, text, DateTimeOffset.UtcNow, SpeakerLabel: null, SegmentId: null, DurationSeconds: duration);

    private sealed class Fixture
    {
        public ConsentStateManager Consent { get; } =
            new(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);

        public INamedConsentClassifier Classifier { get; } = Substitute.For<INamedConsentClassifier>();
        public IConsentAuditLog AuditLog { get; } = Substitute.For<IConsentAuditLog>();
        public IConsentEvidenceStore EvidenceStore { get; } = Substitute.For<IConsentEvidenceStore>();
        public List<AuditEvent> AuditEvents { get; } = new();
        public Channel<TranscriptUtterance> Sink { get; } = Channel.CreateUnbounded<TranscriptUtterance>();
        public List<ConsentStateChangedEventArgs> ConsentChangedEvents { get; } = new();
        public ConsentForwardLoop Loop { get; }

        public Fixture(EchoDetector? echoDetector = null)
        {
            AuditLog.When(a => a.Append(Arg.Any<AuditEvent>())).Do(ci => AuditEvents.Add(ci.Arg<AuditEvent>()));
            EvidenceStore
                .SaveGrantAsync(Arg.Any<string>(), Arg.Any<ConsentEvidence>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            Loop = new ConsentForwardLoop(
                Consent, Classifier, AuditLog, EvidenceStore, NullLogger<ConsentForwardLoop>.Instance,
                echoDetector);
            Loop.SpeakerConsentChanged += (_, e) => ConsentChangedEvents.Add(e);
        }

        public bool RenameViaConsentManager(string oldLabel, string newLabel) => Consent.Rename(oldLabel, newLabel);

        public Task<ConsentGateOutcome> ProcessAsync(
            TranscriptUtterance utterance,
            TargetSpeechLanguage hint = TargetSpeechLanguage.EN,
            Func<string, string, bool>? renameOverride = null)
            => Loop.ProcessAsync(
                Context(hint), utterance, Sink.Writer, renameOverride ?? RenameViaConsentManager,
                TestContext.Current.CancellationToken);

        public bool TryReadEmitted(out TranscriptUtterance utterance) => Sink.Reader.TryRead(out utterance!);
    }

    [Fact]
    public async Task MicUtterance_WithNullLabel_IsAlwaysEmitted()
    {
        var fx = new Fixture();
        var utterance = Mic("hello there, general conversation");

        var outcome = await fx.ProcessAsync(utterance);

        Assert.Equal(ConsentGateOutcome.EmitMic, outcome);
        Assert.True(fx.TryReadEmitted(out var emitted));
        Assert.Equal(utterance, emitted);
        // The You-first ordering means the classifier is never even consulted for mic speech.
        fx.Classifier.DidNotReceive().Classify(Arg.Any<string>(), Arg.Any<TargetSpeechLanguage>());
    }

    [Fact]
    public async Task LoopbackUtterance_WithNullLabel_IsDropped_D1Regression()
    {
        var fx = new Fixture();
        var utterance = LoopbackUnlabeled("some unattributable loopback speech");

        var outcome = await fx.ProcessAsync(utterance);

        Assert.Equal(ConsentGateOutcome.DropUnlabeled, outcome);
        Assert.False(fx.TryReadEmitted(out _));
        Assert.Equal(1, fx.Loop.DroppedUnlabeledCount);
        fx.Classifier.DidNotReceive().Classify(Arg.Any<string>(), Arg.Any<TargetSpeechLanguage>());
    }

    [Fact]
    public async Task LoopbackUtterance_WithWhitespaceLabel_IsDropped_D1Regression()
    {
        var fx = new Fixture();
        var utterance = Loopback("   ", "some unattributable loopback speech");

        var outcome = await fx.ProcessAsync(utterance);

        Assert.Equal(ConsentGateOutcome.DropUnlabeled, outcome);
        Assert.False(fx.TryReadEmitted(out _));
        fx.Classifier.DidNotReceive().Classify(Arg.Any<string>(), Arg.Any<TargetSpeechLanguage>());
    }

    [Fact]
    public async Task UnconsentedSpeaker_NonConsentText_IsDropped_AndCounterIncrements()
    {
        var fx = new Fixture();
        fx.Classifier.Classify("just chatting about the weather", TargetSpeechLanguage.EN)
            .Returns(NamedConsentResult.NoConsent("en"));

        var outcome = await fx.ProcessAsync(Loopback("Speaker 1", "just chatting about the weather"));

        Assert.Equal(ConsentGateOutcome.DropUnconsented, outcome);
        Assert.False(fx.TryReadEmitted(out _));
        Assert.Equal(1, fx.Loop.DroppedUnconsentedCount);
        Assert.Equal(ConsentState.Unknown, fx.Consent.CurrentState("Speaker 1"));
    }

    [Fact]
    public async Task ConsentSentence_Grants_RenamesSpeaker_EmitsTheConsentUtteranceWithTheNewLabel_AndWritesEvidence()
    {
        var fx = new Fixture();
        var sentence = "My name is Alice and I accept that this meeting gets recorded by Pia.";
        fx.Classifier.Classify(sentence, TargetSpeechLanguage.EN)
            .Returns(new NamedConsentResult(true, "Alice", "en", NamedConsentClassifier.CrispConfidence));

        var outcome = await fx.ProcessAsync(Loopback("Speaker 1", sentence));

        Assert.Equal(ConsentGateOutcome.EmitConsentGrant, outcome);
        Assert.True(fx.TryReadEmitted(out var emitted));
        Assert.Equal("Alice", emitted.SpeakerLabel);
        Assert.Equal(sentence, emitted.Text);
        Assert.Equal(ConsentState.Granted, fx.Consent.CurrentState("Alice"));
        await fx.EvidenceStore.Received(1).SaveGrantAsync(
            SessionId,
            Arg.Is<ConsentEvidence>(e => e.SpeakerLabel == "Speaker 1" && e.ExtractedName == "Alice"),
            Arg.Any<CancellationToken>());
        Assert.Contains(fx.ConsentChangedEvents, e => e.SpeakerLabel == "Alice" && e.NewState == ConsentState.Granted);
    }

    [Fact]
    public async Task AfterGrant_SubsequentUtterancesOfThatSpeakerPass()
    {
        var fx = new Fixture();
        var sentence = "My name is Bob and I accept that this call is recorded by Pia.";
        fx.Classifier.Classify(sentence, TargetSpeechLanguage.EN)
            .Returns(new NamedConsentResult(true, "Bob", "en", NamedConsentClassifier.CrispConfidence));
        await fx.ProcessAsync(Loopback("Speaker 1", sentence));
        Assert.True(fx.TryReadEmitted(out _)); // drain the grant utterance itself

        var outcome = await fx.ProcessAsync(Loopback("Bob", "and then we discussed the budget"));

        Assert.Equal(ConsentGateOutcome.EmitConsented, outcome);
        Assert.True(fx.TryReadEmitted(out var emitted));
        Assert.Equal("and then we discussed the budget", emitted.Text);
    }

    [Fact]
    public async Task OtherSpeakersRemainGated_AfterAnotherSpeakerConsents()
    {
        var fx = new Fixture();
        var sentence = "My name is Carol and I accept that this call is recorded by Pia.";
        fx.Classifier.Classify(sentence, TargetSpeechLanguage.EN)
            .Returns(new NamedConsentResult(true, "Carol", "en", NamedConsentClassifier.CrispConfidence));
        await fx.ProcessAsync(Loopback("Speaker 1", sentence));
        fx.TryReadEmitted(out _);

        fx.Classifier.Classify("hi everyone", TargetSpeechLanguage.EN).Returns(NamedConsentResult.NoConsent("en"));
        var outcome = await fx.ProcessAsync(Loopback("Speaker 2", "hi everyone"));

        Assert.Equal(ConsentGateOutcome.DropUnconsented, outcome);
        Assert.Equal(ConsentState.Unknown, fx.Consent.CurrentState("Speaker 2"));
        Assert.Equal(ConsentState.Granted, fx.Consent.CurrentState("Carol"));
    }

    [Fact]
    public async Task RevokedSpeaker_IsDropped_AndIsNotReclassified()
    {
        var fx = new Fixture();
        var sentence = "My name is Dave and I accept that this call is recorded by Pia.";
        fx.Classifier.Classify(sentence, TargetSpeechLanguage.EN)
            .Returns(new NamedConsentResult(true, "Dave", "en", NamedConsentClassifier.CrispConfidence));
        await fx.ProcessAsync(Loopback("Speaker 1", sentence));
        fx.TryReadEmitted(out _);
        fx.Consent.Revoke("Dave");
        fx.Classifier.ClearReceivedCalls();

        var outcome = await fx.ProcessAsync(Loopback("Dave", sentence));

        Assert.Equal(ConsentGateOutcome.DropRevoked, outcome);
        Assert.False(fx.TryReadEmitted(out _));
        // A revoked speaker repeating the consent sentence must NOT be reclassified — the classifier
        // must not even be consulted, or a repeat would silently resurrect them.
        fx.Classifier.DidNotReceive().Classify(Arg.Any<string>(), Arg.Any<TargetSpeechLanguage>());
    }

    [Fact]
    public async Task BelowThresholdConfidence_IsDropped()
    {
        var fx = new Fixture();
        fx.Classifier.Classify("maybe consent-ish phrasing", TargetSpeechLanguage.EN)
            .Returns(new NamedConsentResult(true, "Eve", "en", NamedConsentClassifier.GrantConfidenceThreshold - 0.01f)); // one hair below threshold

        var outcome = await fx.ProcessAsync(Loopback("Speaker 1", "maybe consent-ish phrasing"));

        Assert.Equal(ConsentGateOutcome.DropUnconsented, outcome);
        Assert.Equal(ConsentState.Unknown, fx.Consent.CurrentState("Speaker 1"));
        await fx.EvidenceStore.DidNotReceive()
            .SaveGrantAsync(Arg.Any<string>(), Arg.Any<ConsentEvidence>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClassifierThrows_UtteranceIsDropped_AndLoopContinues()
    {
        var fx = new Fixture();
        fx.Classifier.Classify("a garbled segment", TargetSpeechLanguage.EN)
            .Throws(new InvalidOperationException("boom"));

        var firstOutcome = await fx.ProcessAsync(Loopback("Speaker 1", "a garbled segment"));
        Assert.Equal(ConsentGateOutcome.DropUnconsented, firstOutcome);
        Assert.False(fx.TryReadEmitted(out _));

        // Fail-closed does not mean the gate is broken afterwards: the next (mic) utterance still
        // reaches the sink normally.
        var second = Mic("still working fine");
        var secondOutcome = await fx.ProcessAsync(second);
        Assert.Equal(ConsentGateOutcome.EmitMic, secondOutcome);
        Assert.True(fx.TryReadEmitted(out var emitted));
        Assert.Equal(second, emitted);
    }

    [Fact]
    public async Task EvidenceWriteFails_GrantIsStillRecorded_AndAuditContainsEvidenceWriteFailed()
    {
        var fx = new Fixture();
        var sentence = "My name is Frank and I accept that this call is recorded by Pia.";
        fx.Classifier.Classify(sentence, TargetSpeechLanguage.EN)
            .Returns(new NamedConsentResult(true, "Frank", "en", NamedConsentClassifier.CrispConfidence));
        fx.EvidenceStore
            .SaveGrantAsync(Arg.Any<string>(), Arg.Any<ConsentEvidence>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk full"));

        var outcome = await fx.ProcessAsync(Loopback("Speaker 1", sentence));

        // The grant still stands — a disk/DPAPI failure must not silently discard consent.
        Assert.Equal(ConsentGateOutcome.EmitConsentGrant, outcome);
        Assert.Equal(ConsentState.Granted, fx.Consent.CurrentState("Frank"));
        Assert.True(fx.TryReadEmitted(out _));
        Assert.Contains(fx.AuditEvents, e => e.EventType == ConsentAuditEventTypes.EvidenceWriteFailed);
    }

    [Fact]
    public async Task RenameRefused_KeepsTheDiarizerLabel_ButStillGrants()
    {
        var fx = new Fixture();
        var sentence = "My name is Grace and I accept that this call is recorded by Pia.";
        fx.Classifier.Classify(sentence, TargetSpeechLanguage.EN)
            .Returns(new NamedConsentResult(true, "Grace", "en", NamedConsentClassifier.CrispConfidence));

        var outcome = await fx.ProcessAsync(Loopback("Speaker 1", sentence), renameOverride: (_, _) => false);

        Assert.Equal(ConsentGateOutcome.EmitConsentGrant, outcome);
        Assert.True(fx.TryReadEmitted(out var emitted));
        Assert.Equal("Speaker 1", emitted.SpeakerLabel);
        Assert.Equal(ConsentState.Granted, fx.Consent.CurrentState("Speaker 1"));

        // The event must report the AUTHORITATIVE consent-map key, not the extracted name: a consumer that
        // keyed its UI off the name would end up pointing at a key the consent map does not have, making a
        // later revoke from that UI a silent no-op. The name still rides along as display text, and the
        // detection label as OriginalSpeakerLabel so a consumer can find the row it already created.
        var granted = Assert.Single(fx.ConsentChangedEvents, e => e.NewState == ConsentState.Granted);
        Assert.Equal("Speaker 1", granted.SpeakerLabel);
        Assert.Equal("Speaker 1", granted.OriginalSpeakerLabel);
        Assert.Equal("Grace", granted.ExtractedName);
        Assert.Equal(ConsentState.Unknown, fx.Consent.CurrentState("Grace"));
    }

    [Fact]
    public async Task RenameAccepted_ReportsTheNewLabelAsTheKey_AndTheOldOneAsTheOriginal()
    {
        var fx = new Fixture();
        var sentence = "My name is Heidi and I accept that this call is recorded by Pia.";
        fx.Classifier.Classify(sentence, TargetSpeechLanguage.EN)
            .Returns(new NamedConsentResult(true, "Heidi", "en", NamedConsentClassifier.CrispConfidence));

        await fx.ProcessAsync(Loopback("Speaker 1", sentence));

        var granted = Assert.Single(fx.ConsentChangedEvents, e => e.NewState == ConsentState.Granted);
        Assert.Equal("Heidi", granted.SpeakerLabel);
        Assert.Equal("Speaker 1", granted.OriginalSpeakerLabel);
        Assert.Equal(ConsentState.Granted, fx.Consent.CurrentState("Heidi"));
    }

    [Fact]
    public async Task RunAsync_SurvivesAnUtteranceThatThrowsOutsideProcessAsyncsOwnCatches()
    {
        // The classifier throw is swallowed INSIDE ProcessAsync, so it never reaches RunAsync's
        // per-utterance try/catch. This drives the loop for real with an audit-log double that throws on
        // the grant path — a throw site that sits outside every inner catch — and asserts the loop keeps
        // going. Without that per-utterance catch, one such throw would end the forward loop for the whole
        // run: the raw channel would keep filling and NOTHING would ever be emitted again, including every
        // microphone utterance, while the session still reported Running.
        var fx = new Fixture();
        var sentence = "My name is Ida and I accept that this call is recorded by Pia.";
        fx.Classifier.Classify(sentence, TargetSpeechLanguage.EN)
            .Returns(new NamedConsentResult(true, "Ida", "en", NamedConsentClassifier.CrispConfidence));
        fx.AuditLog
            .When(a => a.Append(Arg.Is<AuditEvent>(e => e.EventType == ConsentAuditEventTypes.ConsentGranted)))
            .Do(_ => throw new InvalidOperationException("poison audit append"));

        var raw = Channel.CreateUnbounded<TranscriptUtterance>();
        await raw.Writer.WriteAsync(Loopback("Speaker 1", sentence), TestContext.Current.CancellationToken);
        await raw.Writer.WriteAsync(Mic("still alive"), TestContext.Current.CancellationToken);
        raw.Writer.TryComplete();

        await fx.Loop.RunAsync(
            Context(), raw.Reader, fx.Sink.Writer, fx.RenameViaConsentManager, TestContext.Current.CancellationToken);

        // The grant itself still took effect (it happens before the audit append) …
        Assert.Equal(ConsentState.Granted, fx.Consent.CurrentState("Ida"));
        // … and the loop drained the rest of the channel: the mic utterance still reached the sink.
        var emitted = new List<TranscriptUtterance>();
        while (fx.TryReadEmitted(out var u)) emitted.Add(u);
        Assert.Contains(emitted, u => u.Text == "still alive");
    }

    [Fact]
    public async Task RemoveSamplesFor_PurgesOnlyThatSpeakersMeasuredSpeech()
    {
        // Revocation removes a speaker's bubbles and journal entries from the transcript, so their measured
        // speech must be removable too — otherwise their NAME, utterance count and speaking time survive in
        // the voice-stats flyout and in the YAML front matter of the file the user saves, and every other
        // speaker's share stays diluted by speech the saved document no longer contains.
        var fx = new Fixture();
        var sentence = "My name is Anna and I accept that this call is recorded by Pia.";
        fx.Classifier.Classify(sentence, TargetSpeechLanguage.EN)
            .Returns(new NamedConsentResult(true, "Anna", "en", NamedConsentClassifier.CrispConfidence));

        await fx.ProcessAsync(Mic("local speech", duration: 2.0));
        await fx.ProcessAsync(Loopback("Speaker 1", sentence, duration: 4.0));
        await fx.ProcessAsync(Loopback("Anna", "and here is some more talking", duration: 6.0));
        Assert.Equal(3, fx.Loop.VoiceSamples.Count);

        var removed = fx.Loop.RemoveSamplesFor("Anna");

        Assert.Equal(2, removed);
        var remaining = Assert.Single(fx.Loop.VoiceSamples);
        Assert.Equal(TranscriptSpeaker.You, remaining.Speaker);
        Assert.Null(remaining.SpeakerLabel);
    }

    [Fact]
    public async Task RenameSamples_ReKeysExistingSamples_SoOnePersonStaysOneRow()
    {
        // Without this, a mid-session rename split one speaker across two statistics rows with halved
        // totals and halved shares, while the transcript body attributed all of it to the new name.
        var fx = new Fixture();
        var sentence = "I accept that this is recorded by Pia, no name given.";
        fx.Classifier.Classify(sentence, TargetSpeechLanguage.EN)
            .Returns(new NamedConsentResult(true, null, "en", NamedConsentClassifier.CrispConfidence));

        await fx.ProcessAsync(Loopback("Speaker 2", sentence, duration: 30.0));
        await fx.ProcessAsync(Loopback("Speaker 2", "first minute of talking", duration: 30.0));

        Assert.True(fx.RenameViaConsentManager("Speaker 2", "Bob"));
        fx.Loop.RenameSamples("Speaker 2", "Bob");

        await fx.ProcessAsync(Loopback("Bob", "second minute of talking", duration: 60.0));

        Assert.Equal(3, fx.Loop.VoiceSamples.Count);
        Assert.All(fx.Loop.VoiceSamples, s => Assert.Equal("Bob", s.SpeakerLabel));
    }

    [Fact]
    public async Task AuditLog_NeverReceivesTheUtteranceText_OrTheExtractedName()
    {
        var fx = new Fixture();
        var name = "CanaryNameHelga";
        var sentence = $"My name is {name} and I accept that this call is recorded by Pia.";
        fx.Classifier.Classify(sentence, TargetSpeechLanguage.EN)
            .Returns(new NamedConsentResult(true, name, "en", NamedConsentClassifier.CrispConfidence));

        await fx.ProcessAsync(Loopback("Speaker 1", sentence));

        Assert.NotEmpty(fx.AuditEvents); // non-vacuity: something was actually audited
        foreach (var evt in fx.AuditEvents)
        {
            Assert.DoesNotContain(name, evt.SpeakerLabel ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(sentence, evt.EventType, StringComparison.Ordinal);
            if (evt.Details is null) continue;
            foreach (var value in evt.Details.Values)
            {
                var text = value?.ToString() ?? string.Empty;
                Assert.DoesNotContain(name, text, StringComparison.Ordinal);
                Assert.DoesNotContain(sentence, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task VoiceSamples_AreRecordedOnlyForEmittedUtterances()
    {
        var fx = new Fixture();
        fx.Classifier.Classify("no consent in this one", TargetSpeechLanguage.EN)
            .Returns(NamedConsentResult.NoConsent("en"));

        await fx.ProcessAsync(Mic("hello", duration: 2.0));
        await fx.ProcessAsync(Loopback("Speaker 1", "no consent in this one", duration: 3.0));

        var samples = fx.Loop.VoiceSamples;
        Assert.Single(samples);
        Assert.Equal(TranscriptSpeaker.You, samples[0].Speaker);
        Assert.Equal(2.0, samples[0].DurationSeconds);
    }

    // ---- Loudspeaker echo -------------------------------------------------------------------------
    //
    // The microphone side is attributed by device, never by voice, so the far end coming back in off the
    // speakers lands under "you" - past this gate, and under a null label revoke can never reach.

    private static readonly DateTimeOffset EchoT0 = new(2026, 9, 1, 9, 15, 0, TimeSpan.Zero);

    private const string ConsentSentence =
        "Mein Name ist Ilkin Kotsch und ich bin damit einverstanden, dass Pia aufzeichnet.";

    private static TranscriptUtterance DatedMic(string text, double atSecond, double lengthSeconds)
        => new(
            TranscriptSpeaker.You, text, EchoT0.AddSeconds(atSecond + lengthSeconds),
            DurationSeconds: lengthSeconds,
            SpeechStart: EchoT0.AddSeconds(atSecond),
            SpeechEnd: EchoT0.AddSeconds(atSecond + lengthSeconds));

    private static TranscriptUtterance DatedLoopback(string label, string text, double atSecond, double lengthSeconds)
        => new(
            TranscriptSpeaker.Them, text, EchoT0.AddSeconds(atSecond + lengthSeconds),
            SpeakerLabel: label,
            SegmentId: 1,
            DurationSeconds: lengthSeconds,
            SpeechStart: EchoT0.AddSeconds(atSecond),
            SpeechEnd: EchoT0.AddSeconds(atSecond + lengthSeconds));

    /// <summary>Marks the far end as having talked across a stretch, the way the loopback VAD would.</summary>
    private static EchoDetector SuppressorHearing(double fromSecond, double toSecond, TimeSpan? holdFor = null)
    {
        var suppressor = new EchoDetector(holdFor: holdFor);
        suppressor.NoteRemoteSpeaking(true, EchoT0.AddSeconds(fromSecond));
        suppressor.NoteRemoteSpeaking(false, EchoT0.AddSeconds(toSecond));
        return suppressor;
    }

    [Fact]
    public async Task EchoedMicUtterance_IsDroppedInsteadOfEmittedAsLocalSpeech()
    {
        var suppressor = SuppressorHearing(5, 11);
        var fx = new Fixture(suppressor);
        suppressor.NoteRemoteUtterance(DatedLoopback("Ilkin Kotsch", ConsentSentence, 5, 6));

        var outcome = await fx.ProcessAsync(DatedMic(ConsentSentence, 5, 6));

        Assert.Equal(ConsentGateOutcome.DropEcho, outcome);
        Assert.False(fx.TryReadEmitted(out _));
        Assert.Equal(1, fx.Loop.DroppedEchoCount);
        // A dropped echo must not inflate the local speaker's share of the meeting either.
        Assert.Empty(fx.Loop.VoiceSamples);
    }

    [Fact]
    public async Task LocalSpeechOverTheFarEnd_StillReachesTheTranscript()
    {
        var suppressor = SuppressorHearing(5, 11);
        var fx = new Fixture(suppressor);
        suppressor.NoteRemoteUtterance(DatedLoopback("Ilkin Kotsch", ConsentSentence, 5, 6));

        var bargeIn = DatedMic("Warte kurz, das habe ich nicht verstanden.", 6, 3);
        var outcome = await fx.ProcessAsync(bargeIn);

        Assert.Equal(ConsentGateOutcome.EmitMic, outcome);
        Assert.True(fx.TryReadEmitted(out var emitted));
        Assert.Equal(bargeIn, emitted);
    }

    [Fact]
    public async Task RunLoop_ResolvesAHeldEchoOnceTheLoopbackTextArrives_WithoutDeadlocking()
    {
        // The mic decode wins the race, so the echo reaches the gate before the text that explains it.
        // The loop is the only reader of this channel: waiting inline would block the very utterance
        // being waited for, so this is the case that pins "park, do not await".
        var suppressor = SuppressorHearing(5, 11, holdFor: TimeSpan.FromSeconds(30));
        var fx = new Fixture(suppressor);
        var raw = Channel.CreateUnbounded<TranscriptUtterance>();

        fx.Classifier.Classify(ConsentSentence, TargetSpeechLanguage.DE)
            .Returns(new NamedConsentResult(true, "Ilkin Kotsch", "de", NamedConsentClassifier.CrispConfidence));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var loop = fx.Loop.RunAsync(
            Context(TargetSpeechLanguage.DE), raw.Reader, fx.Sink.Writer, fx.RenameViaConsentManager, cts.Token);

        await raw.Writer.WriteAsync(DatedMic(ConsentSentence, 5, 6), cts.Token);
        await raw.Writer.WriteAsync(DatedLoopback("Speaker 1", ConsentSentence, 5, 6), cts.Token);
        raw.Writer.Complete();
        await loop;

        var emitted = new List<TranscriptUtterance>();
        while (fx.TryReadEmitted(out var utterance)) emitted.Add(utterance);

        Assert.Equal([TranscriptSpeaker.Them], emitted.Select(u => u.Speaker));
        Assert.Equal(1, fx.Loop.DroppedEchoCount);
    }

    [Fact]
    public async Task RunLoop_ReleasesAHeldMicUtteranceWhenNoLoopbackTextEverExplainsIt()
    {
        var suppressor = SuppressorHearing(5, 11, holdFor: TimeSpan.FromMilliseconds(200));
        var fx = new Fixture(suppressor);
        var raw = Channel.CreateUnbounded<TranscriptUtterance>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var loop = fx.Loop.RunAsync(
            Context(), raw.Reader, fx.Sink.Writer, fx.RenameViaConsentManager, cts.Token);

        var spoken = DatedMic("Da muss irgendwie eine Pause sein zwischen.", 5, 6);
        await raw.Writer.WriteAsync(spoken, cts.Token);

        // Nothing else is ever written: only the loop's own timed flush can release this.
        var emitted = await fx.Sink.Reader.ReadAsync(cts.Token);

        raw.Writer.Complete();
        await loop;

        Assert.Equal(spoken, emitted);
        Assert.Equal(0, fx.Loop.DroppedEchoCount);
    }

    [Fact]
    public async Task RunLoop_EmitsStillHeldMicSpeechWhenTheSessionStops()
    {
        var suppressor = SuppressorHearing(5, 11, holdFor: TimeSpan.FromMinutes(5));
        var fx = new Fixture(suppressor);
        var raw = Channel.CreateUnbounded<TranscriptUtterance>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var loop = fx.Loop.RunAsync(
            Context(), raw.Reader, fx.Sink.Writer, fx.RenameViaConsentManager, cts.Token);

        var spoken = DatedMic("Okay, das hat mir schon mal geholfen.", 5, 6);
        await raw.Writer.WriteAsync(spoken, cts.Token);
        raw.Writer.Complete();
        await loop;

        Assert.True(fx.TryReadEmitted(out var emitted));
        Assert.Equal(spoken, emitted);
    }
}

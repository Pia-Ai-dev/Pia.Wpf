using System.Net.Http;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Consent;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class MultiSpeakerStrategyBIntegrationTests
{
    private static LiveMeetingService NewService(
        out IConsentStateManager mgr,
        out IConsentAuditLog audit,
        out PerSpeakerRingBufferRegistry buffers,
        out ITtsService tts)
    {
        mgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        var classifier = new RuleBasedConsentClassifier();
        var gate = new ConsentGate(mgr, NullLogger<ConsentGate>.Instance);
        audit = new InMemoryAuditLog();
        buffers = new PerSpeakerRingBufferRegistry(perSpeakerCapacity: 16000, totalCapacity: 64000);
        tts = Substitute.For<ITtsService>();
        tts.SpeakAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var settings = Substitute.For<ISettingsService>();
        var http = Substitute.For<IHttpClientFactory>();
        return new LiveMeetingService(
            settings, http, NullLoggerFactory.Instance,
            mgr, classifier, gate, audit, tts, buffers);
    }

    [Fact]
    public async Task NewSpeaker_DoesNotBlock_GrantedSpeakerUtterances()
    {
        var sut = NewService(out var mgr, out _, out _, out var tts);

        // Speaker 1 is already GRANTED; Speaker 2 is unknown and triggers a slow TTS prompt.
        mgr.GetOrCreate("Speaker 1");
        mgr.MarkPrompted("Speaker 1");
        mgr.RecordClassification("Speaker 1",
            new ConsentClassification(ConsentDecision.Grant, 0.95f),
            "ja", "h", "p", "stt");

        var slowTts = new TaskCompletionSource();
        tts.SpeakAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => slowTts.Task);

        // Send Speaker 2 (unknown) first — its consent flow should fire-and-forget.
        await sut.ProcessUtteranceAsync(
            new TranscriptUtterance(TranscriptSpeaker.Them, "hallo", DateTimeOffset.UtcNow, "Speaker 2", TranscriptChannel.Regular),
            CancellationToken.None);

        // Then Speaker 1's granted utterance — must NOT be blocked behind the TTS prompt.
        await sut.ProcessUtteranceAsync(
            new TranscriptUtterance(TranscriptSpeaker.Them, "hello there", DateTimeOffset.UtcNow, "Speaker 1", TranscriptChannel.Regular),
            CancellationToken.None);

        var read = await sut.Utterances.ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        Assert.Equal("Speaker 1", read.SpeakerLabel);
        Assert.Equal("hello there", read.Text);

        // Speaker 2's utterance must NOT have leaked through.
        Assert.False(sut.Utterances.TryRead(out _));

        slowTts.SetResult();
    }

    [Fact]
    public async Task OnGrant_DiscardsPreConsentBuffer_AndAuditsDiscard()
    {
        var sut = NewService(out var mgr, out var audit, out var buffers, out _);
        mgr.GetOrCreate("Speaker 7");
        mgr.MarkPrompted("Speaker 7");

        // Simulate audio captured during PROMPTED.
        buffers.Append("Speaker 7", new float[] { 1, 2, 3, 4, 5 });
        Assert.Equal(5, buffers.Count("Speaker 7"));

        mgr.RecordClassification("Speaker 7",
            new ConsentClassification(ConsentDecision.Grant, 0.95f),
            "ja", "h", "p", "stt");

        Assert.Equal(0, buffers.Count("Speaker 7"));
        var memo = (InMemoryAuditLog)audit;
        Assert.Contains(memo.Events, e => e.EventType == "PRE_CONSENT_BUFFER_DISCARDED" && e.SpeakerLabel == "Speaker 7");

        await sut.DisposeAsync();
    }

    [Fact]
    public async Task NonGrantedSpeaker_UtteranceDropped_ByPostSttDefense()
    {
        var sut = NewService(out var mgr, out var audit, out _, out _);
        mgr.GetOrCreate("Speaker 9");
        mgr.MarkPrompted("Speaker 9"); // Prompted, not Granted.

        await sut.ProcessUtteranceAsync(
            new TranscriptUtterance(TranscriptSpeaker.Them, "leaked", DateTimeOffset.UtcNow, "Speaker 9", TranscriptChannel.Regular),
            CancellationToken.None);

        Assert.False(sut.Utterances.TryRead(out _));
        var memo = (InMemoryAuditLog)audit;
        Assert.Contains(memo.Events, e => e.EventType == "DROPPED_TRANSCRIPT_NO_CONSENT" && e.SpeakerLabel == "Speaker 9");

        await sut.DisposeAsync();
    }

    private sealed class InMemoryAuditLog : IConsentAuditLog
    {
        private readonly List<AuditEvent> _events = new();
        public IReadOnlyList<AuditEvent> Events { get { lock (_events) return _events.ToArray(); } }
        public void Append(AuditEvent evt) { lock (_events) _events.Add(evt); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

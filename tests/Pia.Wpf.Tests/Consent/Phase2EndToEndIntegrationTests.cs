using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Consent;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

/// <summary>
/// Phase-2 end-to-end: two simulated speakers grant at different moments. Speaker 1 produces
/// utterances throughout. Speaker 2's pre-grant transcripts are dropped; post-grant utterances
/// flow. The audit log forms an intact hash chain that verifies against the session public key.
/// </summary>
public sealed class Phase2EndToEndIntegrationTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "pia-phase2-e2e-" + Guid.NewGuid().ToString("N"));

    public Phase2EndToEndIntegrationTests() { Directory.CreateDirectory(_tmpDir); }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    [Fact]
    public async Task TwoSpeakers_StaggeredGrant_TranscriptsRouteCorrectly_ChainVerifies()
    {
        var manifestPath = Path.Combine(_tmpDir, "manifest.json");
        var logPath = Path.Combine(_tmpDir, "audit.jsonl");
        var dpapi = new DpapiHelper(NullLogger<DpapiHelper>.Instance);
        var signer = AuditChainSigner.LoadOrCreate(manifestPath, dpapi);
        await using var audit = new HashChainedAuditLog(logPath, signer, NullLogger<HashChainedAuditLog>.Instance);

        var mgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        var classifier = new RuleBasedConsentClassifier();
        var gate = new ConsentGate(mgr, NullLogger<ConsentGate>.Instance);
        var buffers = new PerSpeakerRingBufferRegistry(perSpeakerCapacity: 16000, totalCapacity: 64000);
        var filter = new PostSttDefenseFilter(mgr, audit, NullLogger<PostSttDefenseFilter>.Instance);
        var tts = Substitute.For<ITtsService>();
        tts.SpeakAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var settings = Substitute.For<ISettingsService>();
        var http = Substitute.For<IHttpClientFactory>();
        await using var sut = new LiveMeetingService(
            settings, http, NullLoggerFactory.Instance,
            mgr, classifier, gate, audit, tts, buffers, filter);

        // 1. Speaker 1 first sighting → consent flow starts; first utterance dropped.
        await sut.ProcessUtteranceAsync(NewUtt("Speaker 1", "guten tag", TranscriptChannel.Regular), default);
        await WaitForState(mgr, "Speaker 1", ConsentState.Prompted);

        // 2. Speaker 1 says "ja" on the consent classification channel → granted.
        await sut.ProcessUtteranceAsync(NewUtt("Speaker 1", "ja", TranscriptChannel.ConsentClassification), default);
        Assert.Equal(ConsentState.Granted, mgr.CurrentState("Speaker 1"));

        // 3. Speaker 2 first sighting → consent flow starts; their utterance must NOT flow.
        await sut.ProcessUtteranceAsync(NewUtt("Speaker 2", "vielleicht", TranscriptChannel.Regular), default);
        await WaitForState(mgr, "Speaker 2", ConsentState.Prompted);

        // 4. Speaker 1 keeps talking — must flow through unaffected.
        await sut.ProcessUtteranceAsync(NewUtt("Speaker 1", "alles klar", TranscriptChannel.Regular), default);
        var first = await sut.Utterances.ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        Assert.Equal("Speaker 1", first.SpeakerLabel);
        Assert.Equal("alles klar", first.Text);

        // 5. Speaker 2 replies "ja" → granted; pre-consent buffer is discarded (audit event).
        await sut.ProcessUtteranceAsync(NewUtt("Speaker 2", "ja", TranscriptChannel.ConsentClassification), default);
        Assert.Equal(ConsentState.Granted, mgr.CurrentState("Speaker 2"));

        // 6. Speaker 2 post-grant utterance flows.
        await sut.ProcessUtteranceAsync(NewUtt("Speaker 2", "danke", TranscriptChannel.Regular), default);
        var second = await sut.Utterances.ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        Assert.Equal("Speaker 2", second.SpeakerLabel);

        // Allow async audit drain to flush.
        await audit.DisposeAsync();

        var (ok, brokenIdx) = HashChainedAuditLog.Verify(logPath, signer.PublicKeyBase64);
        Assert.True(ok, $"chain broken at index {brokenIdx}");
    }

    private static TranscriptUtterance NewUtt(string label, string text, TranscriptChannel ch)
        => new(TranscriptSpeaker.Them, text, DateTimeOffset.UtcNow, label, ch);

    private static async Task WaitForState(IConsentStateManager mgr, string label, ConsentState target)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (mgr.CurrentState(label) == target) return;
            await Task.Delay(20);
        }
        Assert.Equal(target, mgr.CurrentState(label));
    }
}

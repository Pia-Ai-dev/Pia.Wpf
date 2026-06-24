using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Services.Consent;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

/// <summary>
/// Regression for the "loopback bubble stays empty" bug shipped on the consent branch.
///
/// The pre-STT consent gate drops every segment for an Unknown speaker. The first-seen
/// detection that triggers the consent flow (and transitions Unknown -> Prompted) used to
/// live in <c>ProcessUtteranceAsync</c>, which only runs on emitted utterances. Result:
/// dropped → never emitted → flow never starts → speaker stays Unknown forever → all
/// loopback transcript text vanishes. The fix moves the trigger upstream onto
/// <c>ISpeakerIdentificationService.SpeakerRegistered</c> so it fires the moment
/// diarization registers a new label, regardless of gate decisions.
/// </summary>
public sealed class ConsentDeadlockRegressionTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "pia-consent-deadlock-" + Guid.NewGuid().ToString("N"));

    public ConsentDeadlockRegressionTests() { Directory.CreateDirectory(_tmpDir); }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    [Fact]
    public async Task SpeakerRegistration_StartsConsentFlow_WithoutNeedingAnEmittedUtterance()
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

        await using var sut = new LiveMeetingService(
            Substitute.For<ISettingsService>(),
            Substitute.For<IHttpClientFactory>(),
            NullLoggerFactory.Instance,
            mgr, classifier, gate, audit, tts, buffers, filter);

        // Sanity: the speaker has not been seen, so the gate must drop everything.
        Assert.Equal(ConsentState.Unknown, mgr.CurrentState("Speaker 1"));
        Assert.Equal(GateDecision.Drop, gate.Evaluate("Speaker 1"));

        // Act: simulate what SpeakerIdentificationService.SpeakerRegistered does — fire
        // the consent bootstrap for a brand-new label, without any utterance ever reaching
        // ProcessUtteranceAsync (which is what would happen in the wild because the gate
        // drops the first segment before it can be emitted).
        await sut.BeginConsentForNewSpeakerAsync("Speaker 1", default);

        // StartConsentFlowAsync fires the actual TTS + MarkPrompted on a background Task,
        // so wait briefly for the state to settle. The transition itself is the assertion.
        await WaitForState(mgr, "Speaker 1", ConsentState.Prompted);

        // Assert: the consent flow ran end-to-end, transitioned the speaker out of Unknown,
        // and the gate now routes their next utterance to the consent classifier instead of
        // silently dropping it. This is the contract that breaks the chicken-and-egg
        // deadlock in the live engine.
        Assert.Equal(GateDecision.PassToConsentClassifier, gate.Evaluate("Speaker 1"));
        await tts.Received().SpeakAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

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

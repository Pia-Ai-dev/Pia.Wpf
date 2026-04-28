using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services.Consent;
using Pia.Services.Consent.Biometric;
using Pia.Services.Interfaces;
using Pia.Wpf.Tests.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Biometric;

public sealed class BiometricConsentServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly FakeAuditLog _audit = new();
    private readonly FakeTts _tts = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly FakeSecurityMode _security;
    private readonly ConsentStateManager _consentMgr;
    private readonly EncryptedFileBiometricConsentStore _store;
    private readonly CosineSimilarityBiometricMatcher _matcher;

    public BiometricConsentServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PiaBcs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "store.bin");
        _security = new FakeSecurityMode(SecurityProfile.Standard);
        _consentMgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, _clock);
        _store = new EncryptedFileBiometricConsentStore(
            _filePath, NullLogger<EncryptedFileBiometricConsentStore>.Instance);
        _matcher = new CosineSimilarityBiometricMatcher(
            _store, _audit, _clock, NullLogger<CosineSimilarityBiometricMatcher>.Instance);
    }

    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    private sealed class FakeAuditLog : IConsentAuditLog
    {
        public readonly List<AuditEvent> Events = new();
        public void Append(AuditEvent ev) { lock (Events) Events.Add(ev); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTts : ITtsService
    {
        public readonly List<string> Spoken = new();
        public bool IsReady => true;
        public bool IsPlaying => false;
        public bool HasVoiceLoaded => true;
        public bool HasFillers => false;
        public event EventHandler<bool>? IsPlayingChanged;
        public Task SpeakAsync(string text, CancellationToken ct = default) { Spoken.Add(text); return Task.CompletedTask; }
        public void Stop() { }
        public Task InitializeAsync(IProgress<TtsDownloadProgress>? p = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TtsVoice>> GetAvailableVoicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TtsVoice>>(Array.Empty<TtsVoice>());
        public Task DownloadVoiceAsync(string key, IProgress<TtsDownloadProgress>? p = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetVoiceAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task PreGenerateFillersAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PlayFillerAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SpeakChunkedAsync(IAsyncEnumerable<string> s, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeSecurityMode : ISecurityModeProvider
    {
        public SecurityProfile Current { get; set; }
        public event EventHandler<SecurityProfileChangedEventArgs>? ProfileChanged;
        public FakeSecurityMode(SecurityProfile p) { Current = p; }
        public Task SetModeAsync(SecurityMode mode, CancellationToken ct = default)
        {
            var old = Current;
            Current = SecurityProfile.ForMode(mode);
            ProfileChanged?.Invoke(this, new SecurityProfileChangedEventArgs(old, Current));
            return Task.CompletedTask;
        }
    }

    private sealed class StubClassifier : IConsentClassifier
    {
        public ConsentClassification Result { get; set; } = new(ConsentDecision.Grant, 0.95f);
        public Task<ConsentClassification> ClassifyAsync(string text, string prompt, CancellationToken ct = default)
            => Task.FromResult(Result);
    }

    private static float[] Norm(params float[] v)
    {
        var len = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / len).ToArray();
    }

    private BiometricConsentService Make(
        IConsentClassifier? classifier = null,
        Func<string, CancellationToken, Task<string>>? capture = null)
        => new(_store, _matcher, classifier ?? new StubClassifier(), _consentMgr, _security, _audit, _tts, _clock,
            NullLogger<BiometricConsentService>.Instance, capture);

    [Fact]
    public async Task TryMatchExisting_NoEntries_ReturnsNoMatch()
    {
        var svc = Make();
        var result = await svc.TryMatchExistingAsync("S1", Norm(0.1f, 0.2f, 0.3f));
        Assert.Equal(BiometricMatchOutcome.NoMatch, result);
    }

    [Fact]
    public async Task TryMatchExisting_FreshEntry_ReusesConsent_AndAudits()
    {
        var emb = Norm(0.5f, 0.5f, 0.5f, 0.5f);
        var entry = await _store.AddAsync("Alice", emb, _clock.GetUtcNow(),
            _clock.GetUtcNow().AddMonths(12), "ev", "h");

        var svc = Make();
        var result = await svc.TryMatchExistingAsync("S1", emb);

        Assert.Equal(BiometricMatchOutcome.MatchedAndReused, result);
        Assert.Equal(ConsentState.Granted, _consentMgr.CurrentState("S1"));
        Assert.True(_consentMgr.TryGet("S1", out var cs));
        Assert.Equal(entry.Id, cs.BiometricMatchSource);
        Assert.Contains(_audit.Events, e => e.EventType == "BIOMETRIC_MATCH_REUSED_CONSENT");
    }

    [Fact]
    public async Task TryMatchExisting_ExpiredEntry_DeletesAndFallsThrough()
    {
        var emb = Norm(1, 0, 0, 0);
        var entry = await _store.AddAsync("Alice", emb, _clock.GetUtcNow(),
            _clock.GetUtcNow().AddDays(30), "ev", "h");

        _clock.Advance(TimeSpan.FromDays(31));
        var svc = Make();
        var result = await svc.TryMatchExistingAsync("S1", emb);

        Assert.Equal(BiometricMatchOutcome.MatchedButExpired, result);
        Assert.Empty(await _store.GetAllAsync());
        Assert.Contains(_audit.Events, e => e.EventType == "BIOMETRIC_ENTRY_EXPIRED");
        Assert.NotEqual(ConsentState.Granted, _consentMgr.CurrentState("S1"));
    }

    [Fact]
    public async Task OfferOptIn_FlagOff_ReturnsSkipped()
    {
        _security.Current = SecurityProfile.Strict; // AllowBiometricPersistenceByDefault = false
        var svc = Make(capture: (_, _) => Task.FromResult("ja"));

        var emb = Norm(0.1f, 0.2f);
        var result = await svc.OfferOptInAsync("S1", emb, "ev");
        Assert.Equal(BiometricOptInOutcome.Skipped, result);
        Assert.Empty(await _store.GetAllAsync());
        Assert.DoesNotContain(_audit.Events, e => e.EventType == "BIOMETRIC_PROMPTED");
    }

    [Fact]
    public async Task OfferOptIn_GrantReply_PersistsAndAudits()
    {
        var classifier = new StubClassifier { Result = new(ConsentDecision.Grant, 0.95f) };
        var svc = Make(classifier, capture: (_, _) => Task.FromResult("ja"));

        var emb = Norm(0.1f, 0.2f, 0.3f, 0.4f);
        var result = await svc.OfferOptInAsync("S1", emb, "ev");

        Assert.Equal(BiometricOptInOutcome.Granted, result);
        var entries = await _store.GetAllAsync();
        Assert.Single(entries);
        Assert.Contains(_audit.Events, e => e.EventType == "BIOMETRIC_PROMPTED");
        Assert.Contains(_audit.Events, e => e.EventType == "BIOMETRIC_CONSENT_GRANTED");
        // The retention period (12 months default) is encoded into the prompt text.
        Assert.Contains(_tts.Spoken, t => t.Contains("zwölf Monate"));
    }

    [Fact]
    public async Task OfferOptIn_DenyReply_DoesNotPersist()
    {
        var classifier = new StubClassifier { Result = new(ConsentDecision.Deny, 0.9f) };
        var svc = Make(classifier, capture: (_, _) => Task.FromResult("nein"));

        var result = await svc.OfferOptInAsync("S1", Norm(0.1f, 0.2f), "ev");
        Assert.Equal(BiometricOptInOutcome.Denied, result);
        Assert.Empty(await _store.GetAllAsync());
        Assert.Contains(_audit.Events, e => e.EventType == "BIOMETRIC_CONSENT_DENIED");
    }

    [Fact]
    public async Task OfferOptIn_AmbiguousReply_DoesNotPersist()
    {
        var classifier = new StubClassifier { Result = new(ConsentDecision.Ambiguous, 0.5f) };
        var svc = Make(classifier, capture: (_, _) => Task.FromResult("vielleicht"));

        var result = await svc.OfferOptInAsync("S1", Norm(0.1f, 0.2f), "ev");
        Assert.Equal(BiometricOptInOutcome.Ambiguous, result);
        Assert.Empty(await _store.GetAllAsync());
        Assert.Contains(_audit.Events, e => e.EventType == "BIOMETRIC_CONSENT_AMBIGUOUS");
    }

    [Fact]
    public async Task TwoMeetingScenario_SecondMeeting_SkipsRegularConsent()
    {
        // First meeting: grant + biometric opt-in.
        var classifier = new StubClassifier { Result = new(ConsentDecision.Grant, 0.95f) };
        var svc1 = Make(classifier, capture: (_, _) => Task.FromResult("ja"));
        var emb = Norm(0.4f, 0.5f, 0.6f, 0.7f);
        var optIn = await svc1.OfferOptInAsync("Speaker1", emb, "ev1");
        Assert.Equal(BiometricOptInOutcome.Granted, optIn);

        // Second "meeting": fresh consent state, same speaker label, same embedding.
        var consentMgr2 = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, _clock);
        var svc2 = new BiometricConsentService(_store, _matcher, classifier, consentMgr2, _security, _audit, _tts, _clock,
            NullLogger<BiometricConsentService>.Instance);
        var match = await svc2.TryMatchExistingAsync("Speaker1", emb);
        Assert.Equal(BiometricMatchOutcome.MatchedAndReused, match);
        Assert.Equal(ConsentState.Granted, consentMgr2.CurrentState("Speaker1"));
        Assert.Contains(_audit.Events, e => e.EventType == "BIOMETRIC_MATCH_REUSED_CONSENT");
    }
}

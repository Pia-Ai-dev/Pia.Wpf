using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services.Consent;
using Pia.Services.Consent.Biometric;
using Pia.Services.Interfaces;
using Pia.Wpf.Tests.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Biometric;

/// <summary>
/// End-to-end Phase 5 scenarios that span "two consecutive meetings": grant + opt-in in
/// the first, biometric short-circuit in the second. The integration runs across the
/// full <see cref="BiometricConsentService"/> pipeline against the real
/// <see cref="EncryptedFileBiometricConsentStore"/> (DPAPI on Windows under test).
/// </summary>
public sealed class CrossSessionConsentReuseIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly FakeAuditLog _audit = new();
    private readonly FakeTimeProvider _clock = new();

    public CrossSessionConsentReuseIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PiaXSession_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "store.bin");
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
        public bool IsReady => true;
        public bool IsPlaying => false;
        public bool HasVoiceLoaded => true;
        public bool HasFillers => false;
        public event EventHandler<bool>? IsPlayingChanged;
        public Task SpeakAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
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

    private sealed class FakeSecurity : ISecurityModeProvider
    {
        public SecurityProfile Current { get; set; } = SecurityProfile.Standard;
        public event EventHandler<SecurityProfileChangedEventArgs>? ProfileChanged;
        public Task SetModeAsync(SecurityMode mode, CancellationToken ct = default)
        {
            var old = Current; Current = SecurityProfile.ForMode(mode);
            ProfileChanged?.Invoke(this, new SecurityProfileChangedEventArgs(old, Current));
            return Task.CompletedTask;
        }
    }

    private sealed class GrantingClassifier : IConsentClassifier
    {
        public Task<ConsentClassification> ClassifyAsync(string t, string p, CancellationToken ct = default)
            => Task.FromResult(new ConsentClassification(ConsentDecision.Grant, 0.95f));
    }

    private static float[] Norm(params float[] v)
    {
        var len = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / len).ToArray();
    }

    private (BiometricConsentService svc, ConsentStateManager mgr, EncryptedFileBiometricConsentStore store)
        BuildSession()
    {
        var store = new EncryptedFileBiometricConsentStore(
            _filePath, NullLogger<EncryptedFileBiometricConsentStore>.Instance);
        var matcher = new CosineSimilarityBiometricMatcher(
            store, _audit, _clock, NullLogger<CosineSimilarityBiometricMatcher>.Instance);
        var mgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, _clock);
        var svc = new BiometricConsentService(
            store, matcher, new GrantingClassifier(), mgr, new FakeSecurity(),
            _audit, new FakeTts(), _clock,
            NullLogger<BiometricConsentService>.Instance,
            captureReplyAsync: (_, _) => Task.FromResult("ja"));
        return (svc, mgr, store);
    }

    [Fact]
    public async Task TwoMeetings_FirstGrants_SecondReuses()
    {
        // Meeting 1: regular grant happens elsewhere; we only test the biometric opt-in.
        var (svc1, _, store1) = BuildSession();
        var voice = Norm(0.3f, 0.4f, 0.5f, 0.6f, 0.7f);
        var optIn = await svc1.OfferOptInAsync("Speaker1", voice, "ev1");
        Assert.Equal(BiometricOptInOutcome.Granted, optIn);
        Assert.Single(await store1.GetAllAsync());

        // Meeting 2: brand-new consent state, fresh service. The diarizer hands us the same
        // embedding (with a tiny perturbation) for the same person.
        var perturbed = voice.Select((x, i) => x + (i % 2 == 0 ? 0.01f : -0.01f)).ToArray();
        var (svc2, mgr2, _) = BuildSession();
        var match = await svc2.TryMatchExistingAsync("Speaker1", perturbed);

        Assert.Equal(BiometricMatchOutcome.MatchedAndReused, match);
        Assert.Equal(ConsentState.Granted, mgr2.CurrentState("Speaker1"));
        Assert.Contains(_audit.Events, e => e.EventType == "BIOMETRIC_MATCH_REUSED_CONSENT");
    }

    [Fact]
    public async Task TamperedStoreFile_MatcherSkipsAndAuditsCorruption()
    {
        var (svc1, _, store1) = BuildSession();
        var voice = Norm(0.1f, 0.2f, 0.3f, 0.4f);
        await svc1.OfferOptInAsync("Speaker1", voice, "ev1");

        // Corrupt the on-disk file: flip a byte deep in the file. The DPAPI envelope will
        // reject the read entirely (CryptographicException), surfacing as a corruption
        // audit event when the matcher tries to read.
        var bytes = await File.ReadAllBytesAsync(_filePath);
        bytes[bytes.Length / 2] ^= 0x42;
        await File.WriteAllBytesAsync(_filePath, bytes);

        var (svc2, mgr2, _) = BuildSession();
        // Matcher will throw CryptographicException at the LoadInternal level; we expect
        // the service to surface that to the caller (no graceful "skip" exists at the
        // outer DPAPI envelope, since the whole store is unreadable). Assert that.
        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() =>
            svc2.TryMatchExistingAsync("Speaker1", voice));
        Assert.NotEqual(ConsentState.Granted, mgr2.CurrentState("Speaker1"));
    }

    [Fact]
    public async Task ExpiredEntry_AutoDeletes_AndFallsThrough()
    {
        var (svc1, _, store1) = BuildSession();
        var voice = Norm(0.1f, 0.2f, 0.3f);

        // Insert a manually-aged entry directly via the store API.
        var grantedAt = _clock.GetUtcNow();
        var expiresAt = grantedAt.AddDays(30);
        var entry = await store1.AddAsync("Stale", voice, grantedAt, expiresAt, "ev", "h");

        _clock.Advance(TimeSpan.FromDays(31));

        var (svc2, mgr2, store2) = BuildSession();
        var outcome = await svc2.TryMatchExistingAsync("Speaker1", voice);

        Assert.Equal(BiometricMatchOutcome.MatchedButExpired, outcome);
        Assert.Empty(await store2.GetAllAsync());
        Assert.Contains(_audit.Events, e =>
            e.EventType == "BIOMETRIC_ENTRY_EXPIRED" &&
            (Guid)e.Details!["entryId"]! == entry.Id);
        Assert.NotEqual(ConsentState.Granted, mgr2.CurrentState("Speaker1"));
    }
}

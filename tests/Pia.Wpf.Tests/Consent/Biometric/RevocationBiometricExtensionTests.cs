using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Pia.Services.Consent.Biometric;
using Pia.Services.Consent.Revocation;
using Pia.Wpf.Tests.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Biometric;

public sealed class RevocationBiometricExtensionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly FakeAuditLog _audit = new();
    private readonly FakeTimeProvider _clock = new();

    public RevocationBiometricExtensionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PiaRevBio_" + Guid.NewGuid().ToString("N"));
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

    private sealed class FakeBlocklist : IBlocklistFilter
    {
        public void BlockSpeaker(string s) { }
        public bool ShouldDrop(float[] e) => false;
    }

    private static float[] Norm(params float[] v)
    {
        var len = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / len).ToArray();
    }

    [Fact]
    public async Task Revoke_RemovesMatchingBiometricEntry_AndAudits()
    {
        var store = new EncryptedFileBiometricConsentStore(
            _filePath, NullLogger<EncryptedFileBiometricConsentStore>.Instance);
        var matcher = new CosineSimilarityBiometricMatcher(
            store, _audit, _clock, NullLogger<CosineSimilarityBiometricMatcher>.Instance);
        var alice = Norm(0.5f, 0.5f, 0.5f, 0.5f);
        var aliceEntry = await store.AddAsync("Alice", alice, _clock.GetUtcNow(),
            _clock.GetUtcNow().AddMonths(12), "ev", "h");
        await store.AddAsync("Bob", Norm(1, 0, 0, 0), _clock.GetUtcNow(),
            _clock.GetUtcNow().AddMonths(12), "ev", "h");

        var consentMgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, _clock);
        var s = consentMgr.GetOrCreate("Speaker1");
        s.State = ConsentState.Granted;
        s.Embedding = alice; // session embedding matches Alice

        var rev = new RevocationService(
            consentMgr, new FakeBlocklist(), new NoOpTranscriptStore(), new NoOpSummaryStore(),
            Array.Empty<IProviderDeletionClient>(), _audit, _clock,
            NullLogger<RevocationService>.Instance, store, matcher);

        await rev.RevokeAsync("Speaker1", CancellationToken.None);

        var remaining = await store.GetAllAsync();
        Assert.Single(remaining);
        Assert.NotEqual(aliceEntry.Id, remaining[0].Id);
        Assert.Contains(_audit.Events, e =>
            e.EventType == "BIOMETRIC_ENTRY_REVOKED" &&
            (Guid)e.Details!["entryId"]! == aliceEntry.Id);
    }

    [Fact]
    public async Task Revoke_NoMatch_LeavesStoreUntouched()
    {
        var store = new EncryptedFileBiometricConsentStore(
            _filePath, NullLogger<EncryptedFileBiometricConsentStore>.Instance);
        var matcher = new CosineSimilarityBiometricMatcher(
            store, _audit, _clock, NullLogger<CosineSimilarityBiometricMatcher>.Instance);
        await store.AddAsync("Bob", Norm(1, 0, 0, 0), _clock.GetUtcNow(),
            _clock.GetUtcNow().AddMonths(12), "ev", "h");

        var consentMgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, _clock);
        var s = consentMgr.GetOrCreate("Speaker1");
        s.Embedding = Norm(0, 1, 0, 0); // orthogonal → no match

        var rev = new RevocationService(
            consentMgr, new FakeBlocklist(), new NoOpTranscriptStore(), new NoOpSummaryStore(),
            Array.Empty<IProviderDeletionClient>(), _audit, _clock,
            NullLogger<RevocationService>.Instance, store, matcher);

        await rev.RevokeAsync("Speaker1", CancellationToken.None);

        Assert.Single(await store.GetAllAsync());
        Assert.DoesNotContain(_audit.Events, e => e.EventType == "BIOMETRIC_ENTRY_REVOKED");
    }
}

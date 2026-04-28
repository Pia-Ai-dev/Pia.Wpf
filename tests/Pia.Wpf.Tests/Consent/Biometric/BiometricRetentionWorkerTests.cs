using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Pia.Services.Consent.Biometric;
using Pia.Wpf.Tests.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Biometric;

public sealed class BiometricRetentionWorkerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly FakeAuditLog _audit = new();

    public BiometricRetentionWorkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PiaRet_" + Guid.NewGuid().ToString("N"));
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

    private (EncryptedFileBiometricConsentStore store, BiometricRetentionWorker worker, FakeTimeProvider clock) Make()
    {
        var store = new EncryptedFileBiometricConsentStore(
            _filePath, NullLogger<EncryptedFileBiometricConsentStore>.Instance);
        var clock = new FakeTimeProvider();
        var worker = new BiometricRetentionWorker(
            store, _audit, clock, NullLogger<BiometricRetentionWorker>.Instance);
        return (store, worker, clock);
    }

    [Fact]
    public async Task Sweep_NoExpiredEntries_RemovesNothing()
    {
        var (store, worker, clock) = Make();
        var now = clock.GetUtcNow();
        await store.AddAsync("Alice", new[] { 0.1f }, now, now.AddMonths(12), "ev", "h");

        var removed = await worker.SweepAsync();
        Assert.Equal(0, removed);
        Assert.Single(await store.GetAllAsync());
        Assert.Empty(_audit.Events);
    }

    [Fact]
    public async Task Sweep_RemovesOnlyPastExpiry()
    {
        var (store, worker, clock) = Make();
        var now = clock.GetUtcNow();
        var fresh = await store.AddAsync("Fresh", new[] { 0.1f }, now, now.AddMonths(12), "ev", "h");
        var stale = await store.AddAsync("Stale", new[] { 0.2f }, now.AddYears(-2), now.AddMonths(-1), "ev", "h");

        var removed = await worker.SweepAsync();
        Assert.Equal(1, removed);
        var remaining = await store.GetAllAsync();
        Assert.Single(remaining);
        Assert.Equal(fresh.Id, remaining[0].Id);
        Assert.Contains(_audit.Events, e =>
            e.EventType == "BIOMETRIC_ENTRY_EXPIRED" &&
            (Guid)e.Details!["entryId"]! == stale.Id);
    }

    [Fact]
    public async Task Sweep_AfterClockAdvance_RemovesNewlyExpired()
    {
        var (store, worker, clock) = Make();
        var now = clock.GetUtcNow();
        var entry = await store.AddAsync("X", new[] { 0.1f }, now, now.AddDays(30), "ev", "h");

        Assert.Equal(0, await worker.SweepAsync());

        clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, await worker.SweepAsync());
        Assert.Empty(await store.GetAllAsync());
    }
}

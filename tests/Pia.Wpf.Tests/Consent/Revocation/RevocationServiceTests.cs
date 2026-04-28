using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Pia.Services.Consent.Revocation;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Revocation;

public sealed class RevocationServiceTests
{
    private sealed class FakeAuditLog : IConsentAuditLog
    {
        public readonly List<AuditEvent> Events = new();
        public void Append(AuditEvent ev) { lock (Events) Events.Add(ev); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeBlocklist : IBlocklistFilter
    {
        public List<string> Blocked { get; } = new();
        public void BlockSpeaker(string speakerLabel) => Blocked.Add(speakerLabel);
        public bool ShouldDrop(float[] embedding) => false;
        public void Reset() => Blocked.Clear();
    }

    private sealed class FakeTranscriptStore : IPersistedTranscriptStore
    {
        public bool Redact { get; set; } = true;
        public List<string> Redacted { get; } = new();
        public Task<bool> RedactSpeakerAsync(string s, CancellationToken ct)
        { Redacted.Add(s); return Task.FromResult(Redact); }
    }

    private sealed class FakeSummaryStore : ICachedSummaryStore
    {
        public bool Deleted { get; set; } = true;
        public int Calls { get; private set; }
        public Task<bool> DeleteCurrentSummaryAsync(CancellationToken ct)
        { Calls++; return Task.FromResult(Deleted); }
    }

    private sealed class FakeProviderClient : IProviderDeletionClient
    {
        public string ProviderId { get; init; } = "fake";
        public bool SupportsDeletion { get; init; } = true;
        public bool ShouldThrow { get; init; }
        public List<string> Requested { get; } = new();
        public Task RequestDeletionAsync(string speakerLabel, CancellationToken ct)
        {
            if (ShouldThrow) throw new InvalidOperationException("boom");
            Requested.Add(speakerLabel);
            return Task.CompletedTask;
        }
    }

    private static (RevocationService sut, FakeAuditLog audit, ConsentStateManager mgr, FakeBlocklist bl,
        FakeTranscriptStore ts, FakeSummaryStore ss) Build(params IProviderDeletionClient[] providers)
    {
        var mgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        var bl = new FakeBlocklist();
        var ts = new FakeTranscriptStore();
        var ss = new FakeSummaryStore();
        var audit = new FakeAuditLog();
        var sut = new RevocationService(
            mgr, bl, ts, ss, providers, audit, TimeProvider.System,
            NullLogger<RevocationService>.Instance);
        return (sut, audit, mgr, bl, ts, ss);
    }

    [Fact]
    public async Task Revoke_TransitionsState_AndBlocks_AndAuditsRevocation()
    {
        var (sut, audit, mgr, bl, ts, ss) = Build();
        mgr.GetOrCreate("Speaker 1").State = ConsentState.Granted;

        var ev = await sut.RevokeAsync("Speaker 1", CancellationToken.None);

        Assert.Equal(ConsentState.Revoked, mgr.CurrentState("Speaker 1"));
        Assert.Contains("Speaker 1", bl.Blocked);
        Assert.Contains("Speaker 1", ts.Redacted);
        Assert.Equal(1, ss.Calls);
        Assert.True(ev.TranscriptRedacted);
        Assert.True(ev.SummaryDeleted);
        Assert.Contains(audit.Events, e => e.EventType == "REVOCATION" && e.SpeakerLabel == "Speaker 1");
    }

    [Fact]
    public async Task Revoke_ProviderWithoutDeletion_AuditsOutstanding()
    {
        var p = new FakeProviderClient { ProviderId = "p-no-delete", SupportsDeletion = false };
        var (sut, audit, mgr, _, _, _) = Build(p);

        var ev = await sut.RevokeAsync("S1", CancellationToken.None);

        Assert.Empty(p.Requested);
        Assert.Contains("p-no-delete", ev.ProvidersDeletionOutstanding);
        Assert.Contains(audit.Events,
            e => e.EventType == "OUTSTANDING_PROVIDER_DELETION"
              && e.Details!["provider"]!.Equals("p-no-delete"));
    }

    [Fact]
    public async Task Revoke_ProviderDeletionThrows_AuditsOutstanding()
    {
        var p = new FakeProviderClient { ProviderId = "p-fail", ShouldThrow = true };
        var (sut, audit, _, _, _, _) = Build(p);

        var ev = await sut.RevokeAsync("S1", CancellationToken.None);

        Assert.Contains("p-fail", ev.ProvidersDeletionOutstanding);
        Assert.DoesNotContain("p-fail", ev.ProvidersDeletionRequested);
        Assert.Contains(audit.Events,
            e => e.EventType == "OUTSTANDING_PROVIDER_DELETION"
              && e.Details!.ContainsKey("reason"));
    }

    [Fact]
    public async Task Revoke_ProviderWithDeletion_RequestsAndRecords()
    {
        var p = new FakeProviderClient { ProviderId = "p-ok" };
        var (sut, _, _, _, _, _) = Build(p);

        var ev = await sut.RevokeAsync("S1", CancellationToken.None);

        Assert.Contains("S1", p.Requested);
        Assert.Contains("p-ok", ev.ProvidersDeletionRequested);
        Assert.Empty(ev.ProvidersDeletionOutstanding);
    }

    [Fact]
    public async Task Revoke_OriginalConsentEvidenceIsPreserved()
    {
        var (sut, _, mgr, _, _, _) = Build();
        var entry = mgr.GetOrCreate("S1");
        entry.State = ConsentState.Granted;
        entry.Evidence = new ConsentEvidence("ja klar", 0.95f, DateTimeOffset.UtcNow, "h", "p", "stt-id");

        await sut.RevokeAsync("S1", CancellationToken.None);

        // Evidence remains as nachweis that consent did once exist.
        Assert.NotNull(entry.Evidence);
        Assert.Equal("ja klar", entry.Evidence!.TranscriptText);
    }

    [Fact]
    public async Task Revoke_EmptyLabel_Throws()
    {
        var (sut, _, _, _, _, _) = Build();
        await Assert.ThrowsAsync<ArgumentException>(() => sut.RevokeAsync("", CancellationToken.None));
    }
}

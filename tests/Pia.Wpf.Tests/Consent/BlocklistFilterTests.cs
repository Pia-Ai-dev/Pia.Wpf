using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class BlocklistFilterTests
{
    private sealed class FakeAuditLog : IConsentAuditLog
    {
        public readonly List<AuditEvent> Events = new();
        public void Append(AuditEvent ev) { lock (Events) Events.Add(ev); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void BlockSpeaker_WithStoredEmbedding_DropsMatchingEmbedding()
    {
        var mgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        var blocklist = new VoiceEmbeddingBlocklist(0.85f);
        var audit = new FakeAuditLog();
        var sut = new BlocklistFilter(blocklist, mgr, audit, NullLogger<BlocklistFilter>.Instance);

        var emb = new[] { 1f, 0f, 0f };
        mgr.SetEmbedding("Speaker 1", emb);
        sut.BlockSpeaker("Speaker 1");

        Assert.True(sut.ShouldDrop(emb));
        Assert.Contains(audit.Events, e => e.EventType == "DENIED_SPEAKER_BLOCKED");
    }

    [Fact]
    public void BlockSpeaker_NoEmbedding_IsNoOp()
    {
        var mgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        var blocklist = new VoiceEmbeddingBlocklist();
        var audit = new FakeAuditLog();
        var sut = new BlocklistFilter(blocklist, mgr, audit, NullLogger<BlocklistFilter>.Instance);

        sut.BlockSpeaker("Unknown");
        Assert.Equal(0, blocklist.Count);
    }

    [Fact]
    public void Reset_ClearsBlockedEntries()
    {
        var mgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        var blocklist = new VoiceEmbeddingBlocklist();
        var sut = new BlocklistFilter(blocklist, mgr, new FakeAuditLog(), NullLogger<BlocklistFilter>.Instance);

        var emb = new[] { 1f, 0f, 0f };
        mgr.SetEmbedding("Speaker 1", emb);
        sut.BlockSpeaker("Speaker 1");
        sut.Reset();

        Assert.False(sut.ShouldDrop(emb));
    }
}

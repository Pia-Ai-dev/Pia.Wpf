using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

/// <summary>
/// Strategy A (Pause & Re-Consent): exercise the four branches by driving the consent
/// state manager directly. Engine pause/resume is asserted through a fake registered
/// engine that records <c>PauseAsync</c> / <c>ResumeAsync</c> calls — we do not need a
/// real <see cref="Pia.Services.LiveTranscription.LiveTranscriptionEngineService"/>.
/// </summary>
public sealed class StrategyAOrchestratorTests
{
    private sealed class FakeAuditLog : IConsentAuditLog
    {
        public readonly List<AuditEvent> Events = new();
        public void Append(AuditEvent ev) { lock (Events) Events.Add(ev); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeBlocklist : IBlocklistFilter
    {
        public readonly List<string> Blocked = new();
        public void BlockSpeaker(string speakerLabel) { lock (Blocked) Blocked.Add(speakerLabel); }
        public bool ShouldDrop(float[] embedding) => false;
    }

    private static StrategyAOrchestrator Build(
        out ConsentStateManager mgr,
        out FakeAuditLog audit,
        out FakeBlocklist blocklist)
    {
        mgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        audit = new FakeAuditLog();
        blocklist = new FakeBlocklist();
        return new StrategyAOrchestrator(mgr, audit, NullLogger<StrategyAOrchestrator>.Instance, blocklist);
    }

    [Fact]
    public async Task NewSpeakerJoined_AppendsPausedEvent()
    {
        using var sut = Build(out _, out var audit, out _);
        await sut.OnNewSpeakerJoinedAsync("Speaker 1");

        Assert.Contains(audit.Events, e => e.EventType == "STRATEGY_A_PAUSED" && e.SpeakerLabel == "Speaker 1");
    }

    [Fact]
    public async Task Granted_ResumesAndDoesNotBlock()
    {
        using var sut = Build(out var mgr, out var audit, out var blocklist);
        await sut.OnNewSpeakerJoinedAsync("Speaker 1");

        mgr.RecordClassification("Speaker 1",
            new ConsentClassification(ConsentDecision.Grant, 1.0f),
            "ja", "hash", "prompt", "stt");

        await Task.Delay(50);
        Assert.Contains(audit.Events, e => e.EventType == "STRATEGY_A_RESUMED" && e.Details?["outcome"] as string == "Granted");
        Assert.Empty(blocklist.Blocked);
    }

    [Fact]
    public async Task Denied_ResumesAndBlocks()
    {
        using var sut = Build(out var mgr, out var audit, out var blocklist);
        await sut.OnNewSpeakerJoinedAsync("Speaker 1");

        mgr.RecordClassification("Speaker 1",
            new ConsentClassification(ConsentDecision.Deny, 1.0f),
            "nein", "hash", "prompt", "stt");

        await Task.Delay(50);
        Assert.Contains(audit.Events, e => e.EventType == "STRATEGY_A_RESUMED" && e.Details?["outcome"] as string == "Denied");
        Assert.Contains("Speaker 1", blocklist.Blocked);
    }

    [Fact]
    public async Task Timeout_ResumesAndBlocks()
    {
        var clock = new FakeTimeProvider();
        var mgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, clock)
        {
            PromptTimeout = TimeSpan.FromSeconds(1),
        };
        var audit = new FakeAuditLog();
        var blocklist = new FakeBlocklist();
        using var sut = new StrategyAOrchestrator(mgr, audit, NullLogger<StrategyAOrchestrator>.Instance, blocklist);

        mgr.MarkPrompted("Speaker 1");
        await sut.OnNewSpeakerJoinedAsync("Speaker 1");

        clock.Advance(TimeSpan.FromSeconds(2));
        mgr.SweepTimeouts();

        await Task.Delay(50);
        Assert.Contains(audit.Events, e => e.EventType == "STRATEGY_A_RESUMED" && e.Details?["outcome"] as string == "Timeout");
        Assert.Contains("Speaker 1", blocklist.Blocked);
    }

    [Fact]
    public async Task Revoked_ResumesAndBlocks()
    {
        using var sut = Build(out var mgr, out var audit, out var blocklist);
        await sut.OnNewSpeakerJoinedAsync("Speaker 1");

        mgr.Revoke("Speaker 1");

        await Task.Delay(50);
        Assert.Contains(audit.Events, e => e.EventType == "STRATEGY_A_RESUMED");
        Assert.Contains("Speaker 1", blocklist.Blocked);
    }
}

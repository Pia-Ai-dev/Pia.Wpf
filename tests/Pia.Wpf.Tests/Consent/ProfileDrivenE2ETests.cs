using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services.Consent;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

/// <summary>
/// One end-to-end test per security profile. Runs the same new-speaker sequence and
/// asserts the orchestrator wired up by <see cref="ConsentOrchestratorFactory"/> behaves
/// per spec §7: Strict pauses (Strategy A); Standard / Permissive flow without pause
/// (Strategy B). Cloud-call gating is verified at the profile level (AllowEuCloud /
/// AllowNonEuCloud) rather than via an HTTP fake — Phase 4 owns the actual transport.
/// </summary>
public sealed class ProfileDrivenE2ETests
{
    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Settings { get; } = new();
        public event EventHandler<AppSettings>? SettingsChanged;
        public Task<AppSettings> GetSettingsAsync() => Task.FromResult(Settings);
        public Task SaveSettingsAsync(AppSettings settings) { SettingsChanged?.Invoke(this, settings); return Task.CompletedTask; }
        public Task SaveDraftAsync(string? draftText) => Task.CompletedTask;
        public Task<string?> GetDraftAsync() => Task.FromResult<string?>(null);
    }

    private sealed class FakeAuditLog : IConsentAuditLog
    {
        public readonly List<AuditEvent> Events = new();
        public void Append(AuditEvent ev) { lock (Events) Events.Add(ev); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (IConsentOrchestrator orch, FakeAuditLog audit, ConsentStateManager mgr) Build(SecurityMode mode)
    {
        var settings = new FakeSettingsService();
        settings.Settings.SecurityMode = mode;
        var provider = new SecurityModeProvider(settings, NullLogger<SecurityModeProvider>.Instance);
        // Drain async ctor.
        for (var i = 0; i < 20 && provider.Current.Mode != mode; i++) Thread.Sleep(10);

        var mgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        var audit = new FakeAuditLog();
        var blocklist = new VoiceEmbeddingBlocklist();
        var blocklistFilter = new BlocklistFilter(blocklist, mgr, audit, NullLogger<BlocklistFilter>.Instance);
        var factory = new ConsentOrchestratorFactory(provider, mgr, audit, NullLoggerFactory.Instance, blocklistFilter);
        return (factory.CreateForCurrentProfile(), audit, mgr);
    }

    [Fact]
    public async Task Strict_NewSpeaker_PausesAndAuditsStrategyA()
    {
        var (orch, audit, _) = Build(SecurityMode.Strict);
        await orch.OnNewSpeakerJoinedAsync("Speaker 1");

        Assert.IsType<StrategyAOrchestrator>(orch);
        Assert.Contains(audit.Events, e => e.EventType == "STRATEGY_A_PAUSED");
        Assert.False(SecurityProfile.Strict.AllowEuCloud);
        Assert.False(SecurityProfile.Strict.AllowNonEuCloud);
    }

    [Fact]
    public async Task Standard_NewSpeaker_DoesNotPause_AndAllowsEuOnly()
    {
        var (orch, audit, _) = Build(SecurityMode.Standard);
        await orch.OnNewSpeakerJoinedAsync("Speaker 1");

        Assert.IsType<StrategyBOrchestrator>(orch);
        Assert.DoesNotContain(audit.Events, e => e.EventType == "STRATEGY_A_PAUSED");
        Assert.True(SecurityProfile.Standard.AllowEuCloud);
        Assert.False(SecurityProfile.Standard.AllowNonEuCloud);
    }

    [Fact]
    public async Task Permissive_NewSpeaker_DoesNotPause_AndAllowsAllCloud()
    {
        var (orch, audit, _) = Build(SecurityMode.Permissive);
        await orch.OnNewSpeakerJoinedAsync("Speaker 1");

        Assert.IsType<StrategyBOrchestrator>(orch);
        Assert.DoesNotContain(audit.Events, e => e.EventType == "STRATEGY_A_PAUSED");
        Assert.True(SecurityProfile.Permissive.AllowEuCloud);
        Assert.True(SecurityProfile.Permissive.AllowNonEuCloud);
    }

    [Fact]
    public async Task Strict_DenyAfterJoin_ResumesAndBlocks()
    {
        var (orch, audit, mgr) = Build(SecurityMode.Strict);
        await orch.OnNewSpeakerJoinedAsync("Speaker 1");
        // The speaker's voice embedding must already be on the entry for the blocklist
        // to capture it. The engine pipeline is what populates this in production.
        mgr.SetEmbedding("Speaker 1", new[] { 1f, 0f, 0f });
        mgr.RecordClassification("Speaker 1",
            new ConsentClassification(ConsentDecision.Deny, 1.0f),
            "nein", "hash", "prompt", "stt");

        await Task.Delay(80);
        Assert.Contains(audit.Events, e => e.EventType == "STRATEGY_A_RESUMED" && e.Details?["outcome"] as string == "Denied");
    }
}

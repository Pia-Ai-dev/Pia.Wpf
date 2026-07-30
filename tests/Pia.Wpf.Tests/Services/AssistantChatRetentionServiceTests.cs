using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Batch 03's retention half: the run audit timeline ages out with the chat history it describes, on the same
/// cutoff, and a prune fault cannot take the retention timer down with it.
/// </summary>
public sealed class AssistantChatRetentionServiceTests
{
    private readonly IAssistantChatService _chats = Substitute.For<IAssistantChatService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAgentTimelineService _timeline = Substitute.For<IAgentTimelineService>();

    [Fact]
    public async Task RetentionCleanup_PrunesTheTimelineWithTheSameCutoff()
    {
        var ct = TestContext.Current.CancellationToken;
        _settings.GetSettingsAsync().Returns(new AppSettings { ChatHistoryEnabled = true, ChatHistoryRetentionDays = 30 });
        DateTime? evictCutoff = null;
        _chats.EvictOlderThanAsync(Arg.Do<DateTime>(c => evictCutoff = c), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());
        DateTime? pruneCutoff = null;
        _timeline.PruneOlderThanAsync(Arg.Do<DateTime>(c => pruneCutoff = c), Arg.Any<CancellationToken>())
            .Returns(3);

        await CreateSut().RunCleanupAsync(ct);

        // ONE call, with the SAME cutoff the chat eviction got — that is what "no new setting" means.
        await _timeline.Received(1).PruneOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        Assert.NotNull(evictCutoff);
        Assert.Equal(evictCutoff, pruneCutoff);
    }

    [Fact]
    public async Task RetentionCleanup_SkipsThePruneWhenHistoryIsDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        _settings.GetSettingsAsync().Returns(new AppSettings { ChatHistoryEnabled = false });

        await CreateSut().RunCleanupAsync(ct);

        // Same gate as the chat eviction: history off means nothing accumulates, audit table included.
        await _timeline.DidNotReceive().PruneOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _chats.DidNotReceive().EvictOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailingPruneDoesNotStopTheTimer()
    {
        var ct = TestContext.Current.CancellationToken;
        _settings.GetSettingsAsync().Returns(new AppSettings { ChatHistoryEnabled = true, ChatHistoryRetentionDays = 30 });
        _chats.EvictOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _timeline.PruneOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the store is broken"));

        // Asserted, not assumed: the outer try is what keeps the 24 h loop alive.
        await CreateSut().RunCleanupAsync(ct);
    }

    private AssistantChatRetentionService CreateSut() => new(
        _chats,
        _settings,
        // The sync service is only touched when a chat was actually evicted, and every fact here evicts
        // nothing — but it is a sealed class, so a real (fully substituted) instance is cheaper than a shim.
        new AssistantChatSyncService(
            _chats,
            Substitute.For<ICloudCapabilityService>(),
            Substitute.For<IAuthService>(),
            _settings,
            Substitute.For<System.Net.Http.IHttpClientFactory>(),
            new SyncMapper(Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance)),
            Substitute.For<ISyncClientService>(),
            NullLogger<AssistantChatSyncService>.Instance),
        _timeline,
        NullLogger<AssistantChatRetentionService>.Instance);
}

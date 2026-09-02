using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public sealed class AssistantChatRetentionServiceTests
{
    private readonly IAssistantChatService _chats = Substitute.For<IAssistantChatService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAgentTimelineService _timeline = Substitute.For<IAgentTimelineService>();
    private readonly IAgentToolExchangeStore _exchanges = Substitute.For<IAgentToolExchangeStore>();

    [Fact]
    public async Task RetentionCleanup_PrunesTheTimelineWithTheSameCutoff()
    {
        var ct = TestContext.Current.CancellationToken;
        _settings.GetSettingsAsync().Returns(new AppSettings { ChatHistoryRetentionDays = 30 });
        DateTime? evictCutoff = null;
        _chats.EvictOlderThanAsync(Arg.Do<DateTime>(c => evictCutoff = c), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());
        DateTime? pruneCutoff = null;
        _timeline.PruneOlderThanAsync(Arg.Do<DateTime>(c => pruneCutoff = c), Arg.Any<CancellationToken>())
            .Returns(3);

        await CreateSut().RunCleanupAsync(ct);

        await _timeline.Received(1).PruneOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        Assert.NotNull(evictCutoff);
        Assert.Equal(evictCutoff, pruneCutoff);
    }

    [Theory]
    [InlineData(180, 180)]
    [InlineData(730, 730)]
    // Nothing gates the sweep any more, and an out-of-range stored window clamps rather than escaping.
    [InlineData(5000, AppSettings.MaxChatHistoryRetentionDaysCap)]
    [InlineData(0, AppSettings.MinChatHistoryRetentionDays)]
    public async Task RetentionCleanup_AlwaysEvictsOnTheClampedWindow(int stored, int expectedDays)
    {
        var ct = TestContext.Current.CancellationToken;
        _settings.GetSettingsAsync().Returns(new AppSettings { ChatHistoryRetentionDays = stored });
        DateTime? evictCutoff = null;
        _chats.EvictOlderThanAsync(Arg.Do<DateTime>(c => evictCutoff = c), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());
        _timeline.PruneOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(0);

        await CreateSut().RunCleanupAsync(ct);

        Assert.NotNull(evictCutoff);
        var expected = DateTime.UtcNow - TimeSpan.FromDays(expectedDays);
        Assert.True(Math.Abs((evictCutoff.Value - expected).TotalMinutes) < 5,
            $"expected a ~{expectedDays}-day cutoff, got {evictCutoff:O}");
    }

    [Fact]
    public async Task AFailingPruneDoesNotStopTheTimer()
    {
        var ct = TestContext.Current.CancellationToken;
        _settings.GetSettingsAsync().Returns(new AppSettings { ChatHistoryRetentionDays = 30 });
        _chats.EvictOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _timeline.PruneOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the store is broken"));

        // Must not throw: the outer try is what keeps the 24 h loop alive.
        await CreateSut().RunCleanupAsync(ct);
    }

    private AssistantChatRetentionService CreateSut() => new(
        _chats,
        _settings,
        // Sealed, and unreachable unless a chat was evicted, so a real instance is cheaper than a shim.
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
        _exchanges,
        NullLogger<AssistantChatRetentionService>.Instance);
}

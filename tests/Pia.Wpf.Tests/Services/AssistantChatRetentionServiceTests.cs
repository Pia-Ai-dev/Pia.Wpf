using System.Net;
using System.Net.Http;
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
    private const string ServerUrl = "https://test.local";

    private readonly IAssistantChatService _chats = Substitute.For<IAssistantChatService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAgentTimelineService _timeline = Substitute.For<IAgentTimelineService>();
    private readonly IAgentToolExchangeStore _exchanges = Substitute.For<IAgentToolExchangeStore>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly IHttpClientFactory _clientFactory = Substitute.For<IHttpClientFactory>();
    private readonly StubHandler _handler = new();

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

    // Eviction deletes account-wide, so a device may only act on dates it has confirmed against the server.

    [Fact]
    public async Task RetentionCleanup_ConfirmsEveryCandidateBeforeDeletingAnything()
    {
        var ct = TestContext.Current.CancellationToken;
        var stale = Guid.NewGuid();
        var readElsewhere = Guid.NewGuid();
        SyncOn();
        Candidates(stale, readElsewhere);
        _handler.SetGet(stale, AppSettings.DefaultChatHistoryRetentionDays * -1);
        _handler.SetGet(readElsewhere, -1);

        await CreateSut().RunCleanupAsync(ct);

        // Only the date is applied here; the re-SELECT inside EvictOlderThanAsync is what spares the chat.
        await _chats.Received(1).ApplyRemoteAccessDateAsync(
            readElsewhere, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _chats.Received(1).EvictOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetentionCleanup_WhenTheServerCannotBeReached_DeletesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        SyncOn();
        Candidates(Guid.NewGuid());
        _handler.FailEverything = true;

        await CreateSut().RunCleanupAsync(ct);

        await _chats.DidNotReceive().EvictOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _timeline.DidNotReceive().PruneOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // The hole this closes: confirming 60 of 100 and evicting the rest deletes them on exactly the
    // unconfirmed dates the check exists to distrust.
    [Fact]
    public async Task RetentionCleanup_WhenOneCandidateFailsMidBatch_DeletesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = Guid.NewGuid();
        SyncOn();
        Candidates(first, Guid.NewGuid());
        _handler.SetGet(first, -1);

        await CreateSut().RunCleanupAsync(ct);

        await _chats.DidNotReceive().EvictOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // A 200 carrying no date is not a confirmation — schema drift, or a proxy error page with a 200.
    [Fact]
    public async Task RetentionCleanup_WhenTheAnswerCarriesNoDate_DeletesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var undated = Guid.NewGuid();
        SyncOn();
        Candidates(undated);
        _handler.SetDatelessBody(undated);

        await CreateSut().RunCleanupAsync(ct);

        await _chats.DidNotReceive().EvictOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetentionCleanup_WhenTheServerNeverHadTheChat_StillEvictsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var localOnly = Guid.NewGuid();
        SyncOn();
        Candidates(localOnly);
        _handler.SetNotFound(localOnly);

        await CreateSut().RunCleanupAsync(ct);

        await _chats.DidNotReceive().ApplyRemoteAccessDateAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _chats.Received(1).EvictOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetentionCleanup_WhenSyncIsOff_DoesNotAskTheServer()
    {
        var ct = TestContext.Current.CancellationToken;
        _settings.GetSettingsAsync().Returns(new AppSettings { ChatHistoryRetentionDays = 30 });
        _chats.EvictOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _timeline.PruneOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(0);

        await CreateSut().RunCleanupAsync(ct);

        Assert.Equal(0, _handler.Requests);
        await _chats.Received(1).EvictOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // Five seconds after launch there is often no token yet, which is precisely when the local dates are
    // least likely to reflect another device.
    [Fact]
    public async Task RetentionCleanup_WhenSyncIsOnButSignedOut_DeletesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        SyncOn(token: null);
        Candidates(Guid.NewGuid());

        await CreateSut().RunCleanupAsync(ct);

        Assert.Equal(0, _handler.Requests);
        await _chats.DidNotReceive().EvictOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    private void SyncOn(string? token = "test-token")
    {
        _settings.GetSettingsAsync().Returns(new AppSettings
        {
            ChatHistoryRetentionDays = 30,
            SyncEnabled = true,
            ServerUrl = ServerUrl,
            SyncUserId = "user-123",
        });
        _auth.GetAccessTokenAsync().Returns(token);
        _auth.IsLoggedIn.Returns(token is not null);
    }

    private void Candidates(params Guid[] ids)
    {
        _chats.GetChatIdsAccessedBeforeAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(ids.ToList().AsReadOnly());
        _chats.EvictOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _timeline.PruneOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(0);
    }

    private AssistantChatRetentionService CreateSut()
    {
        _clientFactory.CreateClient().Returns(_ => new HttpClient(_handler));
        return new(
            _chats,
            _settings,
            // Sealed, so a real instance over a stub handler is cheaper than a shim.
            new AssistantChatSyncService(
                _chats,
                Substitute.For<ICloudCapabilityService>(),
                _auth,
                _settings,
                _clientFactory,
                new SyncMapper(Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance)),
                Substitute.For<ISyncClientService>(),
                NullLogger<AssistantChatSyncService>.Instance),
            _timeline,
            _exchanges,
            NullLogger<AssistantChatRetentionService>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<Guid, (HttpStatusCode Status, string Body)> _responses = [];

        public bool FailEverything { get; set; }
        public int Requests { get; private set; }

        /// <summary>Answers for one chat with an access date <paramref name="daysFromNow"/> days out.</summary>
        public void SetGet(Guid id, int daysFromNow) =>
            _responses[id] = (HttpStatusCode.OK,
                $$"""{"id":"{{id}}","schemaVersion":1,"lastAccessedAt":"{{DateTime.UtcNow.AddDays(daysFromNow):O}}"}""");

        public void SetNotFound(Guid id) =>
            _responses[id] = (HttpStatusCode.NotFound, """{"error":"not_found"}""");

        public void SetDatelessBody(Guid id) =>
            _responses[id] = (HttpStatusCode.OK, $$"""{"id":"{{id}}","schemaVersion":1}""");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (FailEverything) throw new HttpRequestException("the server is unreachable");

            var uri = request.RequestUri!.ToString();
            foreach (var (id, (status, body)) in _responses)
            {
                if (!uri.EndsWith(id.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                });
            }

            // An id with no answer stands for the connection dropping mid-batch.
            throw new HttpRequestException("the server is unreachable");
        }
    }
}

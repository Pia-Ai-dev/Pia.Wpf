using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Pia.Shared.Models;
using Pia.Shared.Sync;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>A cycle that outlives its account must not write the old account's data into the new one's session.</summary>
public class SyncClientAccountSwitchTests
{
    private const string AccountA = "account-a";
    private const string AccountB = "account-b";
    private const string OldETag = "\"old\"";

    private static readonly DateTime Cursor = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly ITemplateService _templates = Substitute.For<ITemplateService>();
    private readonly IHistoryService _history = Substitute.For<IHistoryService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly GatedHandler _handler = new();
    private readonly AppSettings _stored;

    public SyncClientAccountSwitchTests()
    {
        _stored = new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "http://test",
            SyncUserId = AccountA,
            LastSyncTimestamp = Cursor,
            LastPullETag = OldETag,
            ManagedPersonaStoreInitialized = true,
            ClientPolicyInitialized = true,
        };
        _stored.LastPushedSettingsHash = SyncMapper.ComputeSettingsHash(_stored);
        _settingsService.GetSettingsAsync().Returns(_stored);
        _auth.IsLoggedIn.Returns(true);
        _auth.GetAccessTokenAsync().Returns("token");
        _templates.GetTemplatesAsync().Returns(Array.Empty<OptimizationTemplate>());
        _history.SearchSessionsAsync(
            Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<DateTime?>(),
            Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Array.Empty<OptimizationSession>());
        _history.GetSessionsAsync(Arg.Any<int>(), Arg.Any<int>()).Returns(Array.Empty<OptimizationSession>());
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_handler));
    }

    [Fact]
    public async Task APullAnsweredAfterTheSwitch_AppliesAndSavesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var page = _handler.GatePull(PullPage(Cursor.AddHours(1), hasMore: false, "from A"));
        using var sut = CreateSut();

        var sync = sut.SyncNowAsync(ct);
        await _handler.PullStarted(1).WaitAsync(TimeSpan.FromSeconds(10), ct);
        SwitchToAccountB();
        page.SetResult();

        Assert.Null(await sync.WaitAsync(TimeSpan.FromSeconds(10), ct));
        await _templates.DidNotReceiveWithAnyArgs().AddTemplateAsync(default!);
        Assert.Equal(Cursor, _stored.LastSyncTimestamp);
        Assert.Equal(OldETag, _stored.LastPullETag);
        await _settingsService.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
    }

    [Fact]
    public async Task ASecondPageAnsweredAfterTheSwitch_IsDropped_AndTheCursorStays()
    {
        var ct = TestContext.Current.CancellationToken;
        _handler.GatePull(PullPage(Cursor.AddHours(1), hasMore: true, "first")).SetResult();
        var second = _handler.GatePull(PullPage(Cursor.AddHours(2), hasMore: false, "second"));
        using var sut = CreateSut();

        var sync = sut.SyncNowAsync(ct);
        await _handler.PullStarted(2).WaitAsync(TimeSpan.FromSeconds(10), ct);
        SwitchToAccountB();
        second.SetResult();

        Assert.Null(await sync.WaitAsync(TimeSpan.FromSeconds(10), ct));
        await _templates.Received(1).AddTemplateAsync(Arg.Is<OptimizationTemplate>(t => t.Name == "first"));
        await _templates.DidNotReceive().AddTemplateAsync(Arg.Is<OptimizationTemplate>(t => t.Name == "second"));
        Assert.Equal(Cursor, _stored.LastSyncTimestamp);
    }

    [Fact]
    public async Task ARevokedAnswerAfterTheSwitch_LeavesTheNewAccountsKeyAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var revoked = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent($$"""{"error":"{{E2EEErrorCodes.DeviceRevoked}}"}""", Encoding.UTF8, "application/json"),
        };
        var page = _handler.GatePull(revoked);
        using var sut = CreateSut();
        var revokedRaised = false;
        sut.CurrentDeviceRevoked += (_, _) => revokedRaised = true;

        var sync = sut.SyncNowAsync(ct);
        await _handler.PullStarted(1).WaitAsync(TimeSpan.FromSeconds(10), ct);
        SwitchToAccountB();
        _stored.IsE2EEEnabled = true;
        _stored.E2EEEncryptedUmk = "umk-b";
        page.SetResult();

        Assert.Null(await sync.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.True(_stored.IsE2EEEnabled);
        Assert.Equal("umk-b", _stored.E2EEEncryptedUmk);
        Assert.Null(sut.PendingDeviceRevocation);
        Assert.False(revokedRaised);
    }

    [Fact]
    public async Task AFirstSyncPushAnsweredAfterTheSwitch_StopsBeforeThePullAndEverySave()
    {
        var ct = TestContext.Current.CancellationToken;
        _stored.LastPushedSettingsHash = null;
        var push = _handler.GatePush();
        using var sut = CreateSut();

        var migration = sut.PerformFirstSyncMigrationAsync();
        await _handler.PushStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        SwitchToAccountB();
        push.SetResult();

        await migration.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(0, _handler.PullCount);
        Assert.Null(_stored.LastPushedSettingsHash);
        await _settingsService.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
    }

    [Fact]
    public async Task WithoutASwitch_ThePageIsAppliedAndTheCursorAdvances()
    {
        var ct = TestContext.Current.CancellationToken;
        _handler.GatePull(PullPage(Cursor.AddHours(1), hasMore: false, "from A")).SetResult();
        using var sut = CreateSut();

        Assert.NotNull(await sut.SyncNowAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct));

        await _templates.Received(1).AddTemplateAsync(Arg.Any<OptimizationTemplate>());
        Assert.Equal(Cursor.AddHours(1), _stored.LastSyncTimestamp);
    }

    private void SwitchToAccountB()
    {
        _auth.LoginStateChanged += Raise.Event<EventHandler<bool>>(_auth, false);
        _stored.SyncUserId = AccountB;
        _auth.LoginStateChanged += Raise.Event<EventHandler<bool>>(_auth, true);
    }

    private static HttpResponseMessage PullPage(DateTime serverTimestamp, bool hasMore, string templateName)
    {
        var body = new SyncPullResponse { ServerTimestamp = serverTimestamp, HasMore = hasMore };
        body.Templates.Upserted.Add(new SyncTemplate { Id = Guid.NewGuid(), Name = templateName, Prompt = "p" });
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"new\"");
        return response;
    }

    private SyncClientService CreateSut() => new(
        _auth, _settingsService, _templates, Substitute.For<IProviderService>(), _history,
        Substitute.For<IMemoryService>(), new SyncMapper(Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance)),
        _httpClientFactory, NullLogger<SyncClientService>.Instance,
        new SyncDeleteTrackerService(Path.GetTempPath(), NullLogger<SyncDeleteTrackerService>.Instance),
        scheduledJobService: Substitute.For<IScheduledJobService>());

    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly Queue<(TaskCompletionSource Gate, HttpResponseMessage Response)> _pulls = new();
        private readonly List<TaskCompletionSource> _pullStarted = [];
        private TaskCompletionSource? _pushGate;
        private int _pullCount;

        public TaskCompletionSource PushStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PullCount => Volatile.Read(ref _pullCount);

        public TaskCompletionSource GatePull(HttpResponseMessage response)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pulls.Enqueue((gate, response));
            _pullStarted.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            return gate;
        }

        public TaskCompletionSource GatePush() =>
            _pushGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PullStarted(int page) => _pullStarted[page - 1].Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                PushStarted.TrySetResult();
                if (_pushGate is not null)
                    await _pushGate.Task.WaitAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"conflicts":[]}""", Encoding.UTF8, "application/json"),
                };
            }

            var index = Interlocked.Increment(ref _pullCount) - 1;
            var (gate, response) = _pulls.Dequeue();
            _pullStarted[index].TrySetResult();
            await gate.Task.WaitAsync(ct);
            return response;
        }
    }
}

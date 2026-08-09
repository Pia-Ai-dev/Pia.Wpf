namespace Pia.Tests.Services;

using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

public class AssistantChatSyncServiceTests
{
    private const string ServerUrl = "https://test.local";
    private const string UserId = "user-123";

    private readonly IAssistantChatService _chatService = Substitute.For<IAssistantChatService>();
    private readonly ICloudCapabilityService _capabilities = Substitute.For<ICloudCapabilityService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly StubHandler _handler = new();
    private readonly IHttpClientFactory _clientFactory = Substitute.For<IHttpClientFactory>();

    public AssistantChatSyncServiceTests()
    {
        _clientFactory.CreateClient().Returns(_ => new HttpClient(_handler) { BaseAddress = null });
        _settings.GetSettingsAsync().Returns(new AppSettings
        {
            ServerUrl = ServerUrl,
            SyncUserId = UserId,
        });
        _auth.GetAccessTokenAsync().Returns("test-token");
    }

    [Fact]
    public async Task StartupPull_AppliesRemoteDeletes_FromDeletedArray()
    {
        var deletedId = Guid.NewGuid();
        _handler.SetGet("/api/v1/chats",
            $@"{{""chats"":[],""deleted"":[""{deletedId}""],""hasMore"":false}}");

        var sut = CreateSut(NewPlainMapper());
        await InvokeRunStartupPullAsync(sut);

        await _chatService.Received(1).DeleteFromRemoteAsync(deletedId, Arg.Any<CancellationToken>());
        // SaveFromRemoteAsync is the path for non-deleted chats; with an empty chats[] it must NOT fire.
        await _chatService.DidNotReceive().SaveFromRemoteAsync(Arg.Any<SyncAssistantChat>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartupPull_RequestsIncludeDeletedTrue()
    {
        _handler.SetGet("/api/v1/chats",
            @"{""chats"":[],""deleted"":[],""hasMore"":false}");

        var sut = CreateSut(NewPlainMapper());
        await InvokeRunStartupPullAsync(sut);

        Assert.NotEmpty(_handler.RequestsByUri);
        var url = _handler.RequestsByUri.Keys.First();
        Assert.Contains("includeDeleted=true", url);
    }

    [Fact]
    public async Task StartupPull_StoredETag_Server304_IsNoOpAndLeavesETagUnchanged()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings
        {
            ServerUrl = ServerUrl,
            SyncUserId = UserId,
            LastChatPullETag = "\"v1\"",
        });
        _handler.SetGetSequence("/api/v1/chats", (HttpStatusCode.NotModified, "", null));

        var sut = CreateSut(NewPlainMapper());
        await InvokeRunStartupPullAsync(sut);

        await _chatService.DidNotReceive().SaveFromRemoteAsync(Arg.Any<SyncAssistantChat>(), Arg.Any<CancellationToken>());
        await _chatService.DidNotReceive().DeleteFromRemoteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _capabilities.DidNotReceive().Invalidate();
        await _settings.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
    }

    [Fact]
    public async Task StartupPull_MultiPage_EchoesETagOnFirstPageOnly_AndPersistsOnceAfterLastPage()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings
        {
            ServerUrl = ServerUrl,
            SyncUserId = UserId,
            LastChatPullETag = "\"v1\"",
        });
        _handler.SetGetSequence("/api/v1/chats",
            (HttpStatusCode.OK, @"{""chats"":[],""deleted"":[],""hasMore"":true,""nextCursor"":""abc""}", "\"v2\""),
            (HttpStatusCode.OK, @"{""chats"":[],""deleted"":[],""hasMore"":false}", null));

        var sut = CreateSut(NewPlainMapper());
        await InvokeRunStartupPullAsync(sut);

        var requests = _handler.RequestsByUri.Values.ToList();
        Assert.Equal(2, requests.Count);
        var firstPageRequest = requests.Single(r => !r.RequestUri!.ToString().Contains("cursor="));
        var secondPageRequest = requests.Single(r => r.RequestUri!.ToString().Contains("cursor="));
        Assert.NotEmpty(firstPageRequest.Headers.IfNoneMatch);
        Assert.Empty(secondPageRequest.Headers.IfNoneMatch);

        await _settings.Received(1).SaveSettingsAsync(
            Arg.Is<AppSettings>(s => s.LastChatPullETag == "\"v2\""));
    }

    [Fact]
    public async Task StartupPull_MidPaginationFailure_DoesNotPersistETag()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings
        {
            ServerUrl = ServerUrl,
            SyncUserId = UserId,
        });
        _handler.SetGetSequence("/api/v1/chats",
            (HttpStatusCode.OK, @"{""chats"":[],""deleted"":[],""hasMore"":true,""nextCursor"":""abc""}", "\"v3\""),
            (HttpStatusCode.InternalServerError, "", null));

        var sut = CreateSut(NewPlainMapper());
        await InvokeRunStartupPullAsync(sut);

        await _settings.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
    }

    [Fact]
    public async Task StartupPull_WeakStoredETag_DoesNotThrow()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings
        {
            ServerUrl = ServerUrl,
            SyncUserId = UserId,
            LastChatPullETag = "W/\"v4\"",
        });
        _handler.SetGetSequence("/api/v1/chats",
            (HttpStatusCode.OK, @"{""chats"":[],""deleted"":[],""hasMore"":false}", null));

        var sut = CreateSut(NewPlainMapper());
        await InvokeRunStartupPullAsync(sut);

        var request = _handler.RequestsByUri.Values.Single();
        var ifNoneMatch = Assert.Single(request.Headers.IfNoneMatch);
        Assert.True(ifNoneMatch.IsWeak);
    }

    [Fact]
    public async Task SendUpsert_Returns404_InvalidatesCapability()
    {
        var chat = SampleChat();
        _handler.SetPut("/api/v1/chats/" + chat.Id, HttpStatusCode.NotFound, "");

        var sut = CreateSut(NewPlainMapper());
        await InvokeSendUpsertAsync(sut, chat);

        _capabilities.Received(1).Invalidate();
    }

    [Fact]
    public async Task SendDelete_Returns404_InvalidatesCapability()
    {
        var chatId = Guid.NewGuid();
        _handler.SetDelete("/api/v1/chats/" + chatId, HttpStatusCode.NotFound, "");

        var sut = CreateSut(NewPlainMapper());
        await InvokeSendDeleteAsync(sut, chatId);

        _capabilities.Received(1).Invalidate();
    }

    [Fact]
    public async Task SendUpsert_WhenE2EEActive_BodyHasCiphertextAndEmptyMessages()
    {
        var chat = SampleChat();
        _handler.SetPut("/api/v1/chats/" + chat.Id, HttpStatusCode.Created,
            @"{""id"":""" + chat.Id + @""",""schemaVersion"":1,""windowMode"":""Assistant""}");

        var sut = CreateSut(NewE2EEMapper());
        await InvokeSendUpsertAsync(sut, chat);

        Assert.True(_handler.LastPutBody is not null);
        using var doc = JsonDocument.Parse(_handler.LastPutBody!);

        Assert.True(doc.RootElement.TryGetProperty("encryptedPayload", out var enc));
        Assert.True(enc.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(enc.GetString()));
        Assert.True(doc.RootElement.TryGetProperty("wrappedDek", out var dek));
        Assert.True(dek.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(dek.GetString()));

        // Plaintext fields must not leak.
        var hasTitle = doc.RootElement.TryGetProperty("title", out var title)
            && title.ValueKind != JsonValueKind.Null;
        Assert.False(hasTitle);
        Assert.True(doc.RootElement.TryGetProperty("messages", out var msgs));
        Assert.Equal(0, msgs.GetArrayLength());
    }

    [Fact]
    public async Task StartupPush_BackfillsAllLocalChats_AndSetsFlag()
    {
        var chat = SampleChat();
        _chatService.GetAllIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { chat.Id }.AsReadOnly());
        _chatService.GetAsync(chat.Id, Arg.Any<CancellationToken>()).Returns(chat);
        _handler.SetPut("/api/v1/chats/" + chat.Id, HttpStatusCode.Created,
            @"{""id"":""" + chat.Id + @""",""schemaVersion"":1,""windowMode"":""Assistant""}");

        var sut = CreateSut(NewPlainMapper());
        await InvokeRunStartupPushAsync(sut);

        Assert.Contains(_handler.RequestsByUri.Keys,
            u => u.EndsWith("/api/v1/chats/" + chat.Id));
        await _settings.Received(1).SaveSettingsAsync(
            Arg.Is<AppSettings>(s => s.AssistantChatsBackfilledAt != null));
    }

    [Fact]
    public async Task StartupPush_WhenAlreadyBackfilled_DoesNothing()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings
        {
            ServerUrl = ServerUrl,
            SyncUserId = UserId,
            AssistantChatsBackfilledAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var sut = CreateSut(NewPlainMapper());
        await InvokeRunStartupPushAsync(sut);

        await _chatService.DidNotReceive().GetAllIdsAsync(Arg.Any<CancellationToken>());
        await _settings.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
    }

    // ===== Helpers =====

    private AssistantChatSyncService CreateSut(SyncMapper mapper) =>
        new(_chatService, _capabilities, _auth, _settings, _clientFactory, mapper,
            Substitute.For<ISyncClientService>(),
            NullLogger<AssistantChatSyncService>.Instance);

    private static SyncMapper NewPlainMapper()
    {
        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        return new SyncMapper(dpapi);
    }

    private static SyncMapper NewE2EEMapper()
    {
        var crypto = new CryptoService();
        var deviceKeys = Substitute.For<IDeviceKeyService>();
        deviceKeys.GetDeviceId().Returns("dev-test");

        var dpapi = Substitute.ForPartsOf<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        dpapi.Encrypt(Arg.Any<string>())
            .Returns(c => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(c.Arg<string>())));
        dpapi.Decrypt(Arg.Any<string>())
            .Returns(c => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(c.Arg<string>())));

        var appSettings = new AppSettings { IsE2EEEnabled = true };
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(appSettings);

        var e2ee = new E2EEService(crypto, deviceKeys, dpapi, settings, NullLogger<E2EEService>.Instance);
        e2ee.GenerateAndStoreUmkAsync().GetAwaiter().GetResult();

        return new SyncMapper(dpapi, e2ee);
    }

    private static SyncAssistantChat SampleChat()
    {
        var t = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        return new SyncAssistantChat
        {
            Id = Guid.NewGuid(),
            SchemaVersion = 1,
            Title = "Sample",
            CreatedAt = t,
            UpdatedAt = t,
            LastAccessedAt = t,
            WindowMode = "Assistant",
            Messages = [
                new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "user",
                    Content = "Hello",
                    Timestamp = t,
                }
            ],
        };
    }

    private static Task InvokeRunStartupPullAsync(AssistantChatSyncService sut)
    {
        var m = typeof(AssistantChatSyncService)
            .GetMethod("RunStartupPullAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)m.Invoke(sut, [CancellationToken.None])!;
    }

    private static Task InvokeSendUpsertAsync(AssistantChatSyncService sut, SyncAssistantChat chat)
    {
        var m = typeof(AssistantChatSyncService)
            .GetMethod("SendUpsertAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)m.Invoke(sut, [chat, false, CancellationToken.None])!;
    }

    private static Task InvokeRunStartupPushAsync(AssistantChatSyncService sut)
    {
        var m = typeof(AssistantChatSyncService)
            .GetMethod("RunStartupPushAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)m.Invoke(sut, [CancellationToken.None])!;
    }

    private static Task InvokeSendDeleteAsync(AssistantChatSyncService sut, Guid chatId)
    {
        var m = typeof(AssistantChatSyncService)
            .GetMethod("SendDeleteAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)m.Invoke(sut, [chatId, CancellationToken.None])!;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public ConcurrentDictionary<string, (HttpStatusCode Status, string Body)> GetResponses { get; } = new();
        public ConcurrentDictionary<string, (HttpStatusCode Status, string Body)> PutResponses { get; } = new();
        public ConcurrentDictionary<string, (HttpStatusCode Status, string Body)> DeleteResponses { get; } = new();
        public ConcurrentDictionary<string, Queue<(HttpStatusCode Status, string Body, string? ETag)>> GetSequences { get; } = new();
        public ConcurrentDictionary<string, HttpRequestMessage> RequestsByUri { get; } = new();
        public string? LastPutBody { get; private set; }

        public void SetGet(string pathPrefix, string body) =>
            GetResponses[pathPrefix] = (HttpStatusCode.OK, body);
        public void SetPut(string path, HttpStatusCode status, string body) =>
            PutResponses[path] = (status, body);
        public void SetDelete(string path, HttpStatusCode status, string body) =>
            DeleteResponses[path] = (status, body);

        // Queues successive responses (with optional ETag headers) for the same path — used to
        // test multi-page conditional-GET behavior where page 1 and page 2 must differ.
        public void SetGetSequence(string pathPrefix, params (HttpStatusCode Status, string Body, string? ETag)[] responses) =>
            GetSequences[pathPrefix] = new Queue<(HttpStatusCode, string, string?)>(responses);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            RequestsByUri[uri] = request;

            if (request.Method == HttpMethod.Get)
            {
                foreach (var (path, queue) in GetSequences)
                {
                    if (uri.Contains(path) && queue.Count > 0)
                    {
                        var (status, body, etag) = queue.Dequeue();
                        var queuedResponse = new HttpResponseMessage(status)
                        {
                            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                        };
                        if (etag is not null)
                            queuedResponse.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
                        return queuedResponse;
                    }
                }
            }

            (HttpStatusCode Status, string Body)? match = null;
            if (request.Method == HttpMethod.Get)
            {
                foreach (var (path, resp) in GetResponses)
                {
                    if (uri.Contains(path))
                    {
                        match = resp;
                        break;
                    }
                }
            }
            else if (request.Method == HttpMethod.Put)
            {
                if (request.Content is not null)
                    LastPutBody = await ReadBodyAsync(request.Content, cancellationToken);
                foreach (var (path, resp) in PutResponses)
                {
                    if (uri.EndsWith(path))
                    {
                        match = resp;
                        break;
                    }
                }
            }
            else if (request.Method == HttpMethod.Delete)
            {
                foreach (var (path, resp) in DeleteResponses)
                {
                    if (uri.EndsWith(path))
                    {
                        match = resp;
                        break;
                    }
                }
            }

            if (match is null)
                return new HttpResponseMessage(HttpStatusCode.NotImplemented);

            return new HttpResponseMessage(match.Value.Status)
            {
                Content = new StringContent(match.Value.Body, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        // The per-chat PUT is now gzipped (Content-Encoding: gzip); decompress so body assertions
        // can inspect the JSON. Falls back to a plain read for uncompressed content.
        private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct)
        {
            var bytes = await content.ReadAsByteArrayAsync(ct);
            if (!content.Headers.ContentEncoding.Contains("gzip"))
                return System.Text.Encoding.UTF8.GetString(bytes);

            using var input = new System.IO.MemoryStream(bytes);
            using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var output = new System.IO.MemoryStream();
            await gzip.CopyToAsync(output, ct);
            return System.Text.Encoding.UTF8.GetString(output.ToArray());
        }
    }
}

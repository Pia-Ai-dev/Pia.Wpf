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

/// <summary>
/// Behavioral coverage for AssistantChatSyncService: that startup pulls apply
/// remote deletes, that 404s from the chats endpoint invalidate the capability
/// cache, and that E2EE encryption wraps the wire body so the server never sees
/// plaintext content fields.
/// </summary>
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
        public ConcurrentDictionary<string, HttpRequestMessage> RequestsByUri { get; } = new();
        public string? LastPutBody { get; private set; }

        public void SetGet(string pathPrefix, string body) =>
            GetResponses[pathPrefix] = (HttpStatusCode.OK, body);
        public void SetPut(string path, HttpStatusCode status, string body) =>
            PutResponses[path] = (status, body);
        public void SetDelete(string path, HttpStatusCode status, string body) =>
            DeleteResponses[path] = (status, body);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            RequestsByUri[uri] = request;

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
                    LastPutBody = await request.Content.ReadAsStringAsync(cancellationToken);
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
    }
}

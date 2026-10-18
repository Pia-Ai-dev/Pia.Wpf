using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.KnowledgeManager;
using Pia.Shared.Knowledge;
using Xunit;

namespace Pia.Tests.KnowledgeManager;

public sealed class KnowledgeManagerApiClientTests
{
    private const string ServerUrl = "https://pia.test";

    private readonly QueueHandler _handler = new();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly IHttpClientFactory _factory = Substitute.For<IHttpClientFactory>();

    public KnowledgeManagerApiClientTests()
    {
        _factory.CreateClient().Returns(_ => new HttpClient(_handler));
        _settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = ServerUrl });
        _auth.GetAccessTokenAsync().Returns("token-1");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private KnowledgeManagerApiClient CreateSut() =>
        new(_settings, _auth, _factory, NullLogger<KnowledgeManagerApiClient>.Instance);

    [Fact]
    public async Task ListKnowledgeBases_On200_IsOkWithTheRows()
    {
        var id = Guid.NewGuid();
        _handler.Enqueue(HttpStatusCode.OK,
            $$"""[{"id":"{{id}}","name":"Handbook","documentCount":2,"shared":true,"hasPrompt":true}]""");

        var result = await CreateSut().ListKnowledgeBasesAsync(Ct);

        Assert.Equal(KbManagerCallStatus.Ok, result.Status);
        var kb = Assert.Single(result.Value!);
        Assert.Equal(id, kb.Id);
        Assert.True(kb.Shared);
        Assert.Equal($"{ServerUrl}/api/kb-manager/knowledge-bases", _handler.Requests[0].Uri);
        Assert.Equal("Bearer token-1", _handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task ListKnowledgeBases_OnAnEmpty200_IsStillOk()
    {
        _handler.Enqueue(HttpStatusCode.OK, "[]");

        var result = await CreateSut().ListKnowledgeBasesAsync(Ct);

        Assert.Equal(KbManagerCallStatus.Ok, result.Status);
        Assert.Empty(result.Value!);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, KbManagerCallStatus.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, KbManagerCallStatus.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable, KbManagerCallStatus.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, KbManagerCallStatus.Unavailable)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, KbManagerCallStatus.TooLarge)]
    [InlineData(HttpStatusCode.BadRequest, KbManagerCallStatus.Invalid)]
    public async Task ARefusal_MapsToItsStatus(HttpStatusCode status, KbManagerCallStatus expected)
    {
        _handler.Enqueue(status, """{"error":"some_code","message":"Some message."}""");

        var result = await CreateSut().ListKnowledgeBasesAsync(Ct);

        Assert.Equal(expected, result.Status);
        Assert.Equal("some_code", result.Error?.Code);
    }

    [Fact]
    public async Task A404WithNoBody_IsNotFoundWithoutAnError()
    {
        _handler.Enqueue(HttpStatusCode.NotFound, "");

        var result = await CreateSut().ListKnowledgeBasesAsync(Ct);

        Assert.Equal(KbManagerCallStatus.NotFound, result.Status);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task NoServerConfigured_IsNotConnected_AndCallsNothing()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = null });

        var result = await CreateSut().ListKnowledgeBasesAsync(Ct);

        Assert.Equal(KbManagerCallStatus.NotConnected, result.Status);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task A401_RefreshesTheTokenOnceAndRetries()
    {
        _auth.GetAccessTokenAsync(true, "token-1").Returns("token-2");
        _handler.Enqueue(HttpStatusCode.Unauthorized, "");
        _handler.Enqueue(HttpStatusCode.OK, "[]");

        var result = await CreateSut().ListKnowledgeBasesAsync(Ct);

        Assert.Equal(KbManagerCallStatus.Ok, result.Status);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal("Bearer token-2", _handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task UpdateContent_On200_IsUnchanged_On202_IsOk()
    {
        var kb = Guid.NewGuid();
        var doc = Guid.NewGuid();
        _handler.Enqueue(HttpStatusCode.OK, $$"""{"documentId":"{{doc}}","status":"Ready"}""");
        _handler.Enqueue(HttpStatusCode.Accepted, $$"""{"documentId":"{{doc}}","status":"Pending"}""");
        var sut = CreateSut();
        var request = new KbManagerUpdateContentRequest("new text", null, null);

        var unchanged = await sut.UpdateContentAsync(kb, doc, request, Ct);
        var replaced = await sut.UpdateContentAsync(kb, doc, request, Ct);

        Assert.Equal(KbManagerCallStatus.Unchanged, unchanged.Status);
        Assert.Equal(KbManagerCallStatus.Ok, replaced.Status);
        Assert.Equal("Pending", replaced.Value!.Status);
        Assert.Equal("PUT", _handler.Requests[0].Method);
        Assert.EndsWith($"/api/kb-manager/knowledge-bases/{kb}/documents/{doc}/content", _handler.Requests[0].Uri);
    }

    [Fact]
    public async Task ADuplicateRefusal_CarriesTheOtherDocumentsId()
    {
        var other = Guid.NewGuid();
        _handler.Enqueue(HttpStatusCode.Conflict, $$"""{"error":"duplicate_content","documentId":"{{other}}"}""");

        var result = await CreateSut().UpdateContentAsync(
            Guid.NewGuid(), Guid.NewGuid(), new KbManagerUpdateContentRequest("x", null, null), Ct);

        Assert.Equal(KbManagerCallStatus.Conflict, result.Status);
        Assert.Equal(KbManagerErrorCodes.DuplicateContent, result.Error!.Code);
        Assert.Equal(other, result.Error.DocumentId);
    }

    [Fact]
    public async Task AQuotaRefusal_CarriesResourceLimitAndCurrent()
    {
        _handler.Enqueue(HttpStatusCode.Conflict,
            """{"error":"quota_exceeded","resource":"KnowledgeDocuments","limit":100,"current":101}""");

        var result = await CreateSut().UploadAsync(
            Guid.NewGuid(), new KbManagerUploadRequest("t", null, "text/plain", "x"), Ct);

        Assert.Equal("KnowledgeDocuments", result.Error!.Resource);
        Assert.Equal(100, result.Error.Limit);
        Assert.Equal(101, result.Error.Current);
    }

    [Fact]
    public async Task AQuotaRefusalWithOnlyTheResource_LeavesLimitAndCurrentAbsent()
    {
        _handler.Enqueue(HttpStatusCode.Conflict, """{"error":"quota_exceeded","resource":"MonthlyEmbeddingTokens"}""");

        var result = await CreateSut().UploadAsync(
            Guid.NewGuid(), new KbManagerUploadRequest("t", null, "text/plain", "x"), Ct);

        Assert.Equal(KbManagerCallStatus.Conflict, result.Status);
        Assert.Equal("MonthlyEmbeddingTokens", result.Error!.Resource);
        Assert.Null(result.Error.Limit);
        Assert.Null(result.Error.Current);
    }

    /// <summary>The server's size limit counts UTF-8 bytes; a default encoder would send every umlaut as six.</summary>
    [Fact]
    public async Task Upload_SendsNonAsciiUnescaped_InCamelCase()
    {
        _handler.Enqueue(HttpStatusCode.Accepted, $$"""{"documentId":"{{Guid.NewGuid()}}","status":"Pending"}""");

        await CreateSut().UploadAsync(
            Guid.NewGuid(), new KbManagerUploadRequest("Übersicht", null, "text/markdown", "Größe"), Ct);

        var body = _handler.Requests[0].Body!;
        Assert.Contains("\"title\":\"Übersicht\"", body, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"Größe\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetContent_ReturnsTheTextAndItsMediaType()
    {
        _handler.Enqueue(HttpStatusCode.OK, "# Heading\nBody", "text/markdown");

        var result = await CreateSut().GetContentAsync(Guid.NewGuid(), Guid.NewGuid(), Ct);

        Assert.Equal("# Heading\nBody", result.Value!.Content);
        Assert.Equal("text/markdown", result.Value.ContentType);
    }

    [Fact]
    public async Task ATransportFailure_IsUnavailable()
    {
        _handler.Throw(new HttpRequestException("down"));

        var result = await CreateSut().GetStatsAsync(Guid.NewGuid(), Ct);

        Assert.Equal(KbManagerCallStatus.Unavailable, result.Status);
    }

    internal sealed record SentRequest(string Method, string Uri, string? Authorization, string? Body);

    internal sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body, string MediaType)> _responses = new();
        private Exception? _throw;

        public List<SentRequest> Requests { get; } = [];

        public void Enqueue(HttpStatusCode status, string body, string mediaType = "application/json")
            => _responses.Enqueue((status, body, mediaType));

        public void Throw(Exception exception) => _throw = exception;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SentRequest(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            if (_throw is not null) throw _throw;
            var (status, body, mediaType) = _responses.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        }
    }
}

namespace Pia.Tests.Services;

using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

public class AccountDataServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly StubHandler _handler = new();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();

    public AccountDataServiceTests()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = "https://sync.example.com" });
        _auth.GetAccessTokenAsync().Returns("token-1");
    }

    [Fact]
    public async Task ExportAsync_CopiesTheServersArchiveIntoTheDestination()
    {
        var archive = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x2A };
        _handler.Respond(HttpStatusCode.OK, new ByteArrayContent(archive));
        using var destination = new MemoryStream();

        await CreateSut().ExportAsync(destination, Ct);

        Assert.Equal(archive, destination.ToArray());
        Assert.Equal(HttpMethod.Get, _handler.LastMethod);
        Assert.Equal("https://sync.example.com/auth/account/export", _handler.LastUri);
        Assert.Equal("Bearer token-1", _handler.LastAuthorization);
    }

    [Fact]
    public async Task ExportAsync_WhenTheServerRefuses_Throws()
    {
        _handler.Respond(HttpStatusCode.InternalServerError, new StringContent(""));
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateSut().ExportAsync(destination, Ct));
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task ExportToFileAsync_WritesTheArchiveToThePath()
    {
        var archive = new byte[] { 0x50, 0x4B, 0x05, 0x06 };
        _handler.Respond(HttpStatusCode.OK, new ByteArrayContent(archive));
        var path = Path.Combine(Path.GetTempPath(), $"pia-export-test-{Guid.NewGuid():N}.zip");

        try
        {
            await CreateSut().ExportToFileAsync(path, Ct);

            Assert.Equal(archive, await File.ReadAllBytesAsync(path, Ct));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportToFileAsync_WhenTheServerRefuses_LeavesNoPartialFile()
    {
        _handler.Respond(HttpStatusCode.BadGateway, new StringContent(""));
        var path = Path.Combine(Path.GetTempPath(), $"pia-export-test-{Guid.NewGuid():N}.zip");

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateSut().ExportToFileAsync(path, Ct));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DeleteAsync_SendsTheServersConfirmationWordAndThePassword()
    {
        _handler.Respond(HttpStatusCode.OK, Json("""{"deleted":true}"""));

        var outcome = await CreateSut().DeleteAsync("s3cret", Ct);

        Assert.Equal(AccountDeletionOutcome.Deleted, outcome);
        Assert.Equal(HttpMethod.Post, _handler.LastMethod);
        Assert.Equal("https://sync.example.com/auth/account/delete", _handler.LastUri);
        using var body = JsonDocument.Parse(_handler.LastBody!);
        Assert.Equal("DELETE", body.RootElement.GetProperty("confirm").GetString());
        Assert.Equal("s3cret", body.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task DeleteAsync_WithoutPassword_SendsNull()
    {
        _handler.Respond(HttpStatusCode.OK, Json("""{"deleted":true}"""));

        await CreateSut().DeleteAsync(null, Ct);

        using var body = JsonDocument.Parse(_handler.LastBody!);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("password").ValueKind);
    }

    [Fact]
    public async Task DeleteAsync_WrongPassword_ReportsInvalidPassword()
    {
        _handler.Respond(HttpStatusCode.Unauthorized,
            Json("""{"error":"invalid_password","message":"Incorrect password."}"""));

        Assert.Equal(AccountDeletionOutcome.InvalidPassword, await CreateSut().DeleteAsync("wrong", Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":"boom"}""")]
    [InlineData(HttpStatusCode.NotFound, """{"error":"user_not_found"}""")]
    public async Task DeleteAsync_AnyOtherRefusal_ReportsFailed(HttpStatusCode status, string body)
    {
        _handler.Respond(status, Json(body));

        Assert.Equal(AccountDeletionOutcome.Failed, await CreateSut().DeleteAsync("pw", Ct));
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private AccountDataService CreateSut()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_handler, disposeHandler: false));
        return new AccountDataService(_settings, _auth, factory, NullLogger<AccountDataService>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private HttpContent _content = new StringContent("");

        public HttpMethod? LastMethod { get; private set; }
        public string? LastUri { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastBody { get; private set; }

        public void Respond(HttpStatusCode status, HttpContent content)
        {
            _status = status;
            _content = content;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastUri = request.RequestUri?.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status) { Content = _content };
        }
    }
}

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

public class AccountDataServiceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly StubHandler _handler = new();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly string _exportDir = Path.Combine(Path.GetTempPath(), $"pia-export-test-{Guid.NewGuid():N}");

    public AccountDataServiceTests()
    {
        UseServerUrl("https://sync.example.com");
        _auth.GetAccessTokenAsync().Returns("token-1");
    }

    public void Dispose()
    {
        if (Directory.Exists(_exportDir))
            Directory.Delete(_exportDir, recursive: true);
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
    public async Task ExportAsync_WhenTheBodyStalls_GivesUp()
    {
        _handler.Respond(HttpStatusCode.OK, new StreamContent(new StallingStream()));
        var sut = CreateSut();
        sut.ExportTimeout = TimeSpan.FromMilliseconds(100);
        using var destination = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.ExportAsync(destination, Ct));
    }

    [Theory]
    [InlineData("https://corp.example/pia")]
    [InlineData("https://corp.example/pia/")]
    public async Task Requests_KeepThePathOfTheServerUrl(string serverUrl)
    {
        UseServerUrl(serverUrl);
        _handler.Respond(HttpStatusCode.OK, new ByteArrayContent(Array.Empty<byte>()));
        using var destination = new MemoryStream();

        await CreateSut().ExportAsync(destination, Ct);

        Assert.Equal("https://corp.example/pia/auth/account/export", _handler.LastUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    public async Task Requests_WithoutAServerUrl_SayItIsNotConfigured(string? serverUrl)
    {
        UseServerUrl(serverUrl);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut().DeleteAsync(null, Ct));
    }

    [Fact]
    public async Task ExportToFileAsync_WritesTheArchiveToThePath_AndNothingElse()
    {
        var archive = new byte[] { 0x50, 0x4B, 0x05, 0x06 };
        _handler.Respond(HttpStatusCode.OK, new ByteArrayContent(archive));
        var path = NewExportPath();

        await CreateSut().ExportToFileAsync(path, Ct);

        Assert.Equal(archive, await File.ReadAllBytesAsync(path, Ct));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(_exportDir)));
    }

    [Fact]
    public async Task ExportToFileAsync_ReplacesAnEarlierExport()
    {
        var archive = new byte[] { 0x50, 0x4B, 0x05, 0x06 };
        _handler.Respond(HttpStatusCode.OK, new ByteArrayContent(archive));
        var path = NewExportPath();
        await File.WriteAllBytesAsync(path, new byte[] { 0x01, 0x02 }, Ct);

        await CreateSut().ExportToFileAsync(path, Ct);

        Assert.Equal(archive, await File.ReadAllBytesAsync(path, Ct));
    }

    [Fact]
    public async Task ExportToFileAsync_WhenTheServerRefuses_LeavesNoPartialFile()
    {
        _handler.Respond(HttpStatusCode.BadGateway, new StringContent(""));
        var path = NewExportPath();

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateSut().ExportToFileAsync(path, Ct));

        Assert.Empty(Directory.GetFiles(_exportDir));
    }

    [Fact]
    public async Task ExportToFileAsync_WhenTheServerRefuses_KeepsAnEarlierExport()
    {
        _handler.Respond(HttpStatusCode.BadGateway, new StringContent(""));
        var path = NewExportPath();
        var earlier = new byte[] { 0x50, 0x4B, 0x05, 0x06, 0x07 };
        await File.WriteAllBytesAsync(path, earlier, Ct);

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateSut().ExportToFileAsync(path, Ct));

        Assert.Equal(earlier, await File.ReadAllBytesAsync(path, Ct));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(_exportDir)));
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

    [Fact]
    public async Task DeleteAsync_WhenTheServerHasNoSuchUser_ReportsDeleted()
    {
        _handler.Respond(HttpStatusCode.NotFound, Json("""{"error":"user_not_found"}"""));

        Assert.Equal(AccountDeletionOutcome.Deleted, await CreateSut().DeleteAsync("pw", Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":"boom"}""")]
    public async Task DeleteAsync_AnyOtherRefusal_ReportsFailed(HttpStatusCode status, string body)
    {
        _handler.Respond(status, Json(body));

        Assert.Equal(AccountDeletionOutcome.Failed, await CreateSut().DeleteAsync("pw", Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_WhenTheAnswerIsLost_AsksOnceMore(bool timedOut)
    {
        _handler.FailNext(timedOut ? new TaskCanceledException("timed out") : new HttpRequestException("connection reset"));
        _handler.Respond(HttpStatusCode.OK, Json("""{"deleted":true}"""));

        var outcome = await CreateSut().DeleteAsync("pw", Ct);

        Assert.Equal(AccountDeletionOutcome.Deleted, outcome);
        Assert.Equal(2, _handler.Requests);
    }

    [Fact]
    public async Task DeleteAsync_WhenTheServerStaysUnreachable_Throws()
    {
        _handler.FailNext(new HttpRequestException("offline"));
        _handler.FailNext(new HttpRequestException("offline"));

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateSut().DeleteAsync("pw", Ct));
        Assert.Equal(2, _handler.Requests);
    }

    private void UseServerUrl(string? serverUrl) =>
        _settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = serverUrl });

    private string NewExportPath()
    {
        Directory.CreateDirectory(_exportDir);
        return Path.Combine(_exportDir, "pia-export.zip");
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
        private readonly Queue<Exception> _failures = new();
        private HttpStatusCode _status = HttpStatusCode.OK;
        private HttpContent _content = new StringContent("");

        public int Requests { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastUri { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastBody { get; private set; }

        public void Respond(HttpStatusCode status, HttpContent content)
        {
            _status = status;
            _content = content;
        }

        public void FailNext(Exception failure) => _failures.Enqueue(failure);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastMethod = request.Method;
            LastUri = request.RequestUri?.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            if (_failures.TryDequeue(out var failure))
                throw failure;
            return new HttpResponseMessage(_status) { Content = _content };
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

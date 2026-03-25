using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

public class HttpLoggingHandlerTests
{
    private readonly FakeLogger _logger = new();

    private HttpLoggingHandler CreateHandler(HttpMessageHandler innerHandler)
    {
        var handler = new HttpLoggingHandler(_logger) { InnerHandler = innerHandler };
        return handler;
    }

    private static HttpMessageInvoker CreateInvoker(HttpLoggingHandler handler)
        => new(handler);

    private static HttpRequestMessage CreateRequest(HttpMethod? method = null, string? url = null)
        => new(method ?? HttpMethod.Get, url ?? "https://example.com/api/test");

    [Fact]
    public async Task SendAsync_SuccessfulRequest_LogsAtDebugLevel()
    {
        var handler = CreateHandler(new StubHandler(HttpStatusCode.OK));
        var invoker = CreateInvoker(handler);

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _logger.Entries.Should().ContainSingle();
        _logger.Entries[0].Level.Should().Be(LogLevel.Debug);
    }

    [Fact]
    public async Task SendAsync_SuccessfulRequest_LogsMethodAndUrl()
    {
        var handler = CreateHandler(new StubHandler(HttpStatusCode.OK));
        var invoker = CreateInvoker(handler);

        await invoker.SendAsync(CreateRequest(HttpMethod.Post, "https://cloud.pia-ai.de/api/ai"), CancellationToken.None);

        _logger.Entries[0].Message.Should().Contain("POST");
        _logger.Entries[0].Message.Should().Contain("https://cloud.pia-ai.de/api/ai");
    }

    [Fact]
    public async Task SendAsync_SuccessfulRequest_LogsElapsedTime()
    {
        var handler = CreateHandler(new StubHandler(HttpStatusCode.OK));
        var invoker = CreateInvoker(handler);

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _logger.Entries[0].Message.Should().Contain("ms");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task SendAsync_NonSuccessStatusCode_LogsAtWarningLevel(HttpStatusCode statusCode)
    {
        var handler = CreateHandler(new StubHandler(statusCode));
        var invoker = CreateInvoker(handler);

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _logger.Entries.Should().ContainSingle();
        _logger.Entries[0].Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task SendAsync_Exception_LogsAtErrorLevelAndRethrows()
    {
        var expectedEx = new HttpRequestException("Connection refused");
        var handler = new HttpLoggingHandler(_logger)
        {
            InnerHandler = new ThrowingHandler(expectedEx)
        };
        var invoker = CreateInvoker(handler);

        var act = () => invoker.SendAsync(CreateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("Connection refused");
        _logger.Entries.Should().ContainSingle();
        _logger.Entries[0].Level.Should().Be(LogLevel.Error);
        _logger.Entries[0].Exception.Should().BeSameAs(expectedEx);
    }

    [Fact]
    public async Task SendAsync_LongUrl_TruncatesInLogMessage()
    {
        var longPath = new string('x', 600);
        var handler = CreateHandler(new StubHandler(HttpStatusCode.OK));
        var invoker = CreateInvoker(handler);

        await invoker.SendAsync(CreateRequest(url: $"https://example.com/{longPath}"), CancellationToken.None);

        _logger.Entries[0].Message.Should().Contain("...");
        _logger.Entries[0].Message.Should().NotContain(longPath);
    }

    [Fact]
    public async Task SendAsync_RequestWithAuthHeader_DoesNotLogHeaderValue()
    {
        var handler = CreateHandler(new StubHandler(HttpStatusCode.OK));
        var invoker = CreateInvoker(handler);
        var request = CreateRequest();
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret-token-12345");

        await invoker.SendAsync(request, CancellationToken.None);

        _logger.Entries[0].Message.Should().NotContain("secret-token-12345");
    }

    private class StubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw exception;
    }

    private class FakeLogger : ILogger<HttpLoggingHandler>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}

using System.ClientModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Pia.Services;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class StepInterruptionClassifierTests
{
    [Theory]
    [InlineData("credit", StepInterruption.Credit)]
    [InlineData("credit-wrapped", StepInterruption.Credit)]
    [InlineData("llm-timeout", StepInterruption.Timeout)]
    [InlineData("httpclient-timeout", StepInterruption.Timeout)]
    [InlineData("refused", StepInterruption.Offline)]
    [InlineData("stream-reset", StepInterruption.Offline)]
    [InlineData("stream-ended", StepInterruption.Offline)]
    [InlineData("cloud-stream", StepInterruption.Provider)]
    [InlineData("http-503", StepInterruption.Provider)]
    [InlineData("http-no-status", StepInterruption.Provider)]
    [InlineData("http-429", StepInterruption.Provider)]
    [InlineData("client-no-response", StepInterruption.Provider)]
    [InlineData("client-no-response-400", null)]
    [InlineData("client-no-response-bare", null)]
    [InlineData("http-400", null)]
    [InlineData("http-401", null)]
    [InlineData("truncated", null)]
    [InlineData("plain", null)]
    [InlineData("cancelled", null)]
    public void AnExceptionMapsToItsKind(string shape, StepInterruption? expected)
    {
        Assert.Equal(expected, StepInterruptionClassifier.Classify(Build(shape)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADroppedStreamingResponse_IsOffline(bool reset)
    {
        var ct = TestContext.Current.CancellationToken;
        var port = await ServeTruncatedAsync(reset, ct);
        using var http = new HttpClient();

        var caught = await Record.ExceptionAsync(async () =>
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/", HttpCompletionOption.ResponseHeadersRead, ct);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[1024];
            while (await body.ReadAsync(buffer, ct) > 0)
            {
            }
        });

        Assert.NotNull(caught);
        Assert.Equal(StepInterruption.Offline, StepInterruptionClassifier.Classify(caught));
    }

    [Fact]
    public void AToolFault_IsNeverAnInterruption()
    {
        var ex = Build("refused");

        Assert.False(StepInterruptionClassifier.MarkToolFault(ex));
        Assert.Null(StepInterruptionClassifier.Classify(ex));
        Assert.Null(StepInterruptionClassifier.Classify(new AggregateException(ex)));
    }

    internal static Task<int> ServeTruncatedAsync(bool reset, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            await stream.ReadAtLeastAsync(new byte[4096], 1, throwOnEndOfStream: false, ct);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n"), ct);
            await Task.Delay(200, ct);
            if (reset) client.Client.LingerState = new LingerOption(true, 0);
            client.Client.Close();
            listener.Stop();
        }, ct);
        return Task.FromResult(port);
    }

    internal static Exception Build(string shape) => shape switch
    {
        "credit" => new CreditLimitException("x"),
        "credit-wrapped" => new AggregateException(new CreditLimitException("x")),
        "llm-timeout" => new LlmTimeoutException("P", 30),
        "httpclient-timeout" => new TaskCanceledException("t", new TimeoutException("t",
            new IOException("io", new SocketException((int)SocketError.OperationAborted)))),
        "refused" => new HttpRequestException(HttpRequestError.ConnectionError),
        "stream-reset" => new IOException("io", new SocketException((int)SocketError.ConnectionReset)),
        "stream-ended" => new HttpIOException(HttpRequestError.ResponseEnded),
        "cloud-stream" => new PiaCloudStreamException("Bad Gateway", null),
        "http-503" => new HttpRequestException("m", null, HttpStatusCode.ServiceUnavailable),
        "http-no-status" => new HttpRequestException("m"),
        "http-429" => new HttpRequestException("m", null, HttpStatusCode.TooManyRequests),
        "client-no-response" => new ClientResultException("m", null, new HttpRequestException("m")),
        "client-no-response-400" => new ClientResultException("m", null,
            new HttpRequestException("m", null, HttpStatusCode.BadRequest)),
        "client-no-response-bare" => new ClientResultException("m"),
        "http-400" => new HttpRequestException("m", null, HttpStatusCode.BadRequest),
        "http-401" => new HttpRequestException("m", null, HttpStatusCode.Unauthorized),
        "truncated" => new LlmTruncatedException("P", 10),
        "plain" => new InvalidOperationException("boom"),
        "cancelled" => new OperationCanceledException(),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };
}

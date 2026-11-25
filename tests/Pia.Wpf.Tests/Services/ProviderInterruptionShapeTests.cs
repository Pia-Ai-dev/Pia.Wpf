using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Classifies what the real client families throw, so every provider parks on the same failures.</summary>
public class ProviderInterruptionShapeTests
{
    [Theory]
    [InlineData("pia-cloud", "status-503", StepInterruption.Provider)]
    [InlineData("pia-cloud", "status-429", StepInterruption.Credit)]
    [InlineData("pia-cloud", "status-400", null)]
    [InlineData("pia-cloud", "dropped-stream", StepInterruption.Offline)]
    [InlineData("openai-sdk", "status-503", StepInterruption.Provider)]
    [InlineData("openai-sdk", "status-429", StepInterruption.Provider)]
    [InlineData("openai-sdk", "status-400", null)]
    [InlineData("openai-sdk", "dropped-stream", StepInterruption.Offline)]
    [InlineData("anthropic-sdk", "status-503", StepInterruption.Provider)]
    [InlineData("anthropic-sdk", "status-429", StepInterruption.Provider)]
    [InlineData("anthropic-sdk", "status-400", null)]
    [InlineData("anthropic-sdk", "dropped-stream", StepInterruption.Offline)]
    public async Task ARealProviderFailure_ParksTheSameOnEveryProvider(
        string family, string failure, StepInterruption? expected)
    {
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var port = failure == "dropped-stream"
            ? await StepInterruptionClassifierTests.ServeTruncatedAsync(reset: true, stop.Token)
            : ServeStatus(int.Parse(failure["status-".Length..]), stop.Token);
        var baseUrl = $"http://127.0.0.1:{port}";

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var client = await CreateClientAsync(family, baseUrl, http, ct);

        var caught = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")], cancellationToken: ct))
            {
            }
        });
        stop.Cancel();

        Assert.NotNull(caught);
        Assert.Equal(expected, StepInterruptionClassifier.Classify(caught));
    }

    private static Task<IChatClient> CreateClientAsync(string family, string baseUrl, HttpClient http, CancellationToken ct)
    {
        if (family == "pia-cloud")
        {
            return Task.FromResult<IChatClient>(new PiaCloudChatClient(
                http, baseUrl, (_, _) => Task.FromResult<string?>("token"), NullLogger.Instance));
        }

        IAiProviderHandler handler = family == "openai-sdk"
            ? new OpenAiCompatibleProviderHandler()
            : new AnthropicProviderHandler();
        var provider = new AiProvider
        {
            Name = "Provider",
            ProviderType = handler.ProviderType,
            Endpoint = family == "openai-sdk" ? baseUrl + "/v1" : baseUrl,
            ModelName = "m",
        };
        return handler.CreateChatClientAsync(provider, "key", http, null, null, null, ct);
    }

    private static int ServeStatus(int status, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reply = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} Status\r\nContent-Type: application/json\r\nRetry-After: 0\r\n"
            + "Content-Length: 2\r\nConnection: close\r\n\r\n{}");
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(ct);
                    var stream = client.GetStream();
                    await ReadRequestAsync(stream, ct);
                    await stream.WriteAsync(reply, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                listener.Stop();
            }
        }, ct);
        return port;
    }

    private static async Task ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var received = new List<byte>();
        var buffer = new byte[4096];
        int headerEnd;
        while ((headerEnd = IndexOfHeaderEnd(received)) < 0)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) return;
            received.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        var headers = Encoding.ASCII.GetString(received.ToArray(), 0, headerEnd);
        var length = headers.Split("\r\n")
            .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(l => int.Parse(l["Content-Length:".Length..].Trim()))
            .FirstOrDefault();
        var remaining = length - (received.Count - headerEnd - 4);
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) return;
            remaining -= read;
        }
    }

    private static int IndexOfHeaderEnd(List<byte> data)
    {
        for (var i = 0; i + 3 < data.Count; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                return i;
        }
        return -1;
    }
}

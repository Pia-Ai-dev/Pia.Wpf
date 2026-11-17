using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Localization;
using Pia.Services;
using Pia.Services.Exceptions;
using Xunit;

namespace Pia.Tests.Services;

public class PiaCloudChatClientCreditLimitTests
{
    [Fact]
    public async Task A429_ThrowsCreditLimitException_WithTheLocalizedMessage()
    {
        var ex = await Assert.ThrowsAsync<CreditLimitException>(() => StreamAsync("{}"));

        Assert.Equal(LocalizationSource.Instance["Msg_CreditLimit_Reached"], ex.Message);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public async Task A429WithAResetTime_KeepsTheResetWording()
    {
        var resetsAt = DateTime.UtcNow.AddHours(3).ToString("o", CultureInfo.InvariantCulture);

        var ex = await Assert.ThrowsAsync<CreditLimitException>(
            () => StreamAsync($"{{\"resetsAt\":\"{resetsAt}\"}}"));

        Assert.NotEqual(LocalizationSource.Instance["Msg_CreditLimit_Reached"], ex.Message);
    }

    private static async Task StreamAsync(string body)
    {
        var http = new HttpClient(new TooManyRequestsHandler(body));
        var client = new PiaCloudChatClient(
            http, "https://cloud.pia", (_, _) => Task.FromResult<string?>("token"), NullLogger.Instance);

        await foreach (var _ in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, cancellationToken: TestContext.Current.CancellationToken))
        {
        }
    }

    private sealed class TooManyRequestsHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}

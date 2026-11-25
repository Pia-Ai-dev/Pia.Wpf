using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Localization;
using Pia.Models;
using Pia.Services;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Tests.Services;

public class AiClientServiceCreditLimitTests
{
    [Fact]
    public async Task Optimize_A429_ThrowsCreditLimitException()
    {
        var ex = await Assert.ThrowsAsync<CreditLimitException>(() => CreateSut().OptimizeViaPiaCloudAsync(
            "text", Guid.NewGuid(), "en", isVoiceInput: false,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(LocalizationSource.Instance["Msg_CreditLimit_Reached"], ex.Message);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public async Task GeneratePrompt_A429_ThrowsCreditLimitException()
    {
        var ex = await Assert.ThrowsAsync<CreditLimitException>(() => CreateSut().GeneratePromptViaPiaCloudAsync(
            "style", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(LocalizationSource.Instance["Msg_CreditLimit_Reached"], ex.Message);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    private static AiClientService CreateSut()
    {
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new TooManyRequestsHandler()));

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = "https://cloud.pia" });

        return new AiClientService(
            new DpapiHelper(NullLogger<DpapiHelper>.Instance),
            httpFactory,
            settings,
            new AiProviderHandlerResolver([]),
            Substitute.For<IAuthService>(),
            NullLogger<AiClientService>.Instance,
            Substitute.For<IProviderRequestThrottle>());
    }

    private sealed class TooManyRequestsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
    }
}

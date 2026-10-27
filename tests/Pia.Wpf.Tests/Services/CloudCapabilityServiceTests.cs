namespace Pia.Tests.Services;

using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

public class CloudCapabilityServiceTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, @"{""chats"":false}", true)]
    [InlineData(HttpStatusCode.OK, @"{}", true)]
    [InlineData(HttpStatusCode.OK, @"{""chats"":true}", false)]
    [InlineData(HttpStatusCode.InternalServerError, "", false)]
    [InlineData(HttpStatusCode.OK, "not json", false)]
    public async Task ChatsConfirmedOff_IsTrueOnlyWhenTheServerAnsweredWithoutChats(
        HttpStatusCode status, string body, bool expected)
    {
        var sut = CreateSut(new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) }));

        Assert.Equal(expected, await sut.ChatsConfirmedOffAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChatsConfirmedOff_IsFalseWhenTheServerIsUnreachable()
    {
        var sut = CreateSut(new StubHandler(_ => throw new HttpRequestException("offline")));

        Assert.False(await sut.ChatsSupportedAsync(TestContext.Current.CancellationToken));
        Assert.False(await sut.ChatsConfirmedOffAsync(TestContext.Current.CancellationToken));
    }

    private static CloudCapabilityService CreateSut(HttpMessageHandler handler)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = "https://test.local" });
        var auth = Substitute.For<IAuthService>();
        auth.GetAccessTokenAsync().Returns("test-token");
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient().Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new CloudCapabilityService(settings, auth, factory, NullLogger<CloudCapabilityService>.Instance);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}

using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Pia.Models;
using Pia.Services.MeetingAttendee;
using Pia.Services.Providers;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Architecture;

public class HttpClientDefaultsTests
{
    private const string ChatCompletion =
        """{"id":"c","object":"chat.completion","created":0,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

    public static TheoryData<string> FactoryClientNames() =>
        [Options.DefaultName, TeamsMeetingSession.MeetingRedirectHttpClientName];

    [Theory]
    [MemberData(nameof(FactoryClientNames))]
    public void EveryFactoryClient_CarriesThePiaUserAgent(string clientName)
    {
        using var client = new HttpClient();

        ApplyProductionClientActions(client, clientName);

        Assert.Equal(AppVersionInfo.UserAgent, client.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public async Task ARequestWithItsOwnUserAgent_KeepsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = new CapturingRequestHandler();
        using var client = new HttpClient(capture);
        ApplyProductionClientActions(client, Options.DefaultName);

        using var own = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
        own.Headers.UserAgent.ParseAdd("Other/2.0");
        using (await client.SendAsync(own, ct))
            Assert.Equal("Other/2.0", capture.LastUserAgent);

        using (await client.GetAsync("https://example.test/", ct))
            Assert.Equal(AppVersionInfo.UserAgent, capture.LastUserAgent);
    }

    // These three hand the factory client itself to the SDK transport, so its default header is in reach.
    [Theory]
    [InlineData(AiProviderType.OpenAICompatible)]
    [InlineData(AiProviderType.Ollama)]
    [InlineData(AiProviderType.AzureOpenAI)]
    public async Task AnOpenAiCompatibleRequest_KeepsTheSdkUserAgent(AiProviderType providerType)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = new CapturingRequestHandler(ChatCompletion);
        using var client = new HttpClient(capture);
        ApplyProductionClientActions(client, Options.DefaultName);
        IAiProviderHandler handler = providerType switch
        {
            AiProviderType.OpenAICompatible => new OpenAiCompatibleProviderHandler(),
            AiProviderType.Ollama => new OllamaProviderHandler(),
            _ => new AzureOpenAiProviderHandler(),
        };
        var provider = new AiProvider
        {
            Name = "Provider",
            ProviderType = providerType,
            Endpoint = "https://provider.example.test/v1",
            ModelName = "m",
        };

        using var chat = await handler.CreateChatClientAsync(
            provider, "key", client, mode: null, managedPersonaId: null, personaModelType: null, ct);
        await chat.GetResponseAsync("hi", cancellationToken: ct);

        Assert.False(string.IsNullOrWhiteSpace(capture.LastUserAgent));
        Assert.DoesNotContain("Pia/", capture.LastUserAgent, StringComparison.Ordinal);
    }

    // Runs what the factory runs on each new client, without building its handlers.
    private static void ApplyProductionClientActions(HttpClient client, string clientName)
    {
        using var provider = BootstrapperGraphValidationTests.BuildConfiguredServices().BuildServiceProvider();
        var actions = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(clientName).HttpClientActions;

        Assert.NotEmpty(actions);
        foreach (var action in actions)
            action(client);
    }
}

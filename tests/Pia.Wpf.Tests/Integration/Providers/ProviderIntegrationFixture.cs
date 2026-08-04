using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;

namespace Pia.Wpf.Tests.Integration.Providers;

/// <summary>
/// Reusable factory for an <see cref="AiClientService"/> wired with the same
/// provider handler set used in production, minus the PII tokenizer decorator.
/// Tests want to exercise the real provider HTTP path without retokenization.
/// </summary>
internal sealed class ProviderIntegrationFixture
{
    public AiClientService BuildClient()
    {
        var dpapiHelper = Substitute.ForPartsOf<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        // Integration tests pass a plaintext API key as EncryptedApiKey; the
        // mocked Decrypt returns it unchanged.
        dpapiHelper.Decrypt(Arg.Any<string>()).Returns(call => call.Arg<string>());

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(new AppSettings());

        var authService = Substitute.For<IAuthService>();

        var handlers = new IAiProviderHandler[]
        {
            new OpenAiProviderHandler(),
            new AzureOpenAiProviderHandler(),
            new OllamaProviderHandler(),
            new MistralProviderHandler(),
            new OpenRouterProviderHandler(),
            new OpenAiCompatibleProviderHandler(),
            new VLlmProviderHandler(),
            new PiaCloudProviderHandler(
                authService,
                settingsService,
                NullLogger<PiaCloudProviderHandler>.Instance),
        };

        return new AiClientService(
            dpapiHelper,
            httpClientFactory,
            settingsService,
            new AiProviderHandlerResolver(handlers),
            authService,
            NullLogger<AiClientService>.Instance,
            new ProviderRequestThrottle(settingsService, NullLogger<ProviderRequestThrottle>.Instance));
    }
}

using System.Net.Http;
using Anthropic;
using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services.Providers.Http;

namespace Pia.Services.Providers;

public sealed class AnthropicProviderHandler : IAiProviderHandler
{
    private const string DefaultModel = "claude-opus-5";

    public AiProviderType ProviderType => AiProviderType.Anthropic;

    // Effort rides in output_config, outside the tools array, so a tool-carrying turn keeps its level.
    public bool DropsReasoningEffortWithTools => false;

    public Task<IChatClient> CreateChatClientAsync(
        AiProvider provider,
        string? apiKey,
        HttpClient httpClient,
        string? mode,
        // Ignored: this handler talks to a third-party provider, which has no server-side persona scope.
        Guid? managedPersonaId,
        string? personaModelType,
        CancellationToken cancellationToken)
    {
        var http = new HttpClient(
            new AnthropicRequestHandler(
                provider.EnableWebSearch,
                provider.EnablePromptCache,
                ReasoningEffortMapping.ToAnthropic(provider.ReasoningEffort),
                disableThinking: provider.ReasoningEffort == Pia.Models.ReasoningEffort.None)
            {
                InnerHandler = new HttpClientHandler(),
            },
            disposeHandler: true)
        {
            // AiClientService's per-call timeoutCts owns the bound; the 100s HttpClient default would fire first.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var client = new AnthropicClient
        {
            ApiKey = string.IsNullOrEmpty(apiKey) ? "unused" : apiKey,
            BaseUrl = provider.Endpoint,
            HttpClient = http,
        }.AsIChatClient(provider.ModelName ?? DefaultModel);

        return Task.FromResult(client);
    }

    // Everything provider-specific is applied by AnthropicRequestHandler instead; see its header for why.
    public ChatOptions CreateChatOptions(AiProvider provider, bool hasTools) => new();
}

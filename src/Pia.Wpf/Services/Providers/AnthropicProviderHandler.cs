using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Providers.Http;

namespace Pia.Services.Providers;

public sealed class AnthropicProviderHandler : IAiProviderHandler
{
    private const string DefaultModel = "claude-opus-5";

    // Every Claude model accepts at least this; the provider's own Max output tokens raises it.
    private const int DefaultMaxOutputTokens = 8192;

    // One per endpoint and key, not per row: a step may switch models and still replay what the last one stored,
    // but encrypted search content from another account would not be accepted.
    private readonly ConcurrentDictionary<string, AnthropicNativeStore> _nativeStores = new(StringComparer.Ordinal);
    private readonly ILogger<AnthropicProviderHandler>? _logger;

    public AnthropicProviderHandler(ILogger<AnthropicProviderHandler>? logger = null)
    {
        _logger = logger;
    }

    internal AnthropicNativeStore NativeStoreFor(string? endpoint, string? apiKey) =>
        _nativeStores.GetOrAdd(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{endpoint}\n{apiKey}"))),
            _ => new AnthropicNativeStore());

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
                provider.UsesWebSearch,
                provider.EnablePromptCache,
                ReasoningEffortMapping.ToAnthropic(provider.ReasoningEffort),
                disableThinking: provider.ReasoningEffort == Pia.Models.ReasoningEffort.None,
                NativeStoreFor(provider.Endpoint, apiKey),
                _logger)
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

    // Everything else provider-specific is applied by AnthropicRequestHandler; see its header for why.
    // MaxOutputTokens cannot be: left unset the SDK adapter sends its own 1024, which truncates every answer.
    public ChatOptions CreateChatOptions(AiProvider provider, bool hasTools) =>
        new() { MaxOutputTokens = provider.MaxOutputTokens ?? DefaultMaxOutputTokens };
}

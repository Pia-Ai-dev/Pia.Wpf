using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Pia.Models;

namespace Pia.Services.Providers;

/// <summary>
/// Generic OpenAI-compatible servers (LM Studio, llama.cpp, LiteLLM,
/// jan, etc.). We can't make assumptions about which reasoning fields they
/// honour, so we never set any. Users wanting reasoning controls should pick
/// a dedicated provider type (Ollama, vLLM, OpenAI, OpenRouter, Mistral).
/// </summary>
public sealed class OpenAiCompatibleProviderHandler : IAiProviderHandler
{
    public AiProviderType ProviderType => AiProviderType.OpenAICompatible;

    // Never sends any reasoning field, with or without tools — so there is nothing a second turn could
    // recover.
    public bool DropsReasoningEffortWithTools => false;

    public Task<IChatClient> CreateChatClientAsync(
        AiProvider provider,
        string? apiKey,
        HttpClient httpClient,
        string? mode,
        // Ignored: this handler talks to a third-party provider, which has no server-side persona scope.
        Guid? managedPersonaId,
        CancellationToken cancellationToken)
    {
        var client = new ChatClient(
            model: provider.ModelName ?? "gpt-4o-mini",
            credential: new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "unused" : apiKey),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(provider.Endpoint),
                Transport = new HttpClientPipelineTransport(httpClient),
                // The per-call timeoutCts owns the bound; the SDK's 100s network default would fire first.
                NetworkTimeout = Timeout.InfiniteTimeSpan,
            }).AsIChatClient();

        return Task.FromResult(client);
    }

    public ChatOptions CreateChatOptions(AiProvider provider, bool hasTools)
        => new();
}

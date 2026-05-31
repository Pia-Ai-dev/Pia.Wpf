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

    public Task<IChatClient> CreateChatClientAsync(
        AiProvider provider,
        string? apiKey,
        HttpClient httpClient,
        string? mode,
        CancellationToken cancellationToken)
    {
        var client = new ChatClient(
            model: provider.ModelName ?? "gpt-4o-mini",
            credential: new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "unused" : apiKey),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(provider.Endpoint),
                Transport = new HttpClientPipelineTransport(httpClient),
            }).AsIChatClient();

        return Task.FromResult(client);
    }

    public ChatOptions CreateChatOptions(AiProvider provider, bool hasTools)
        => new();
}

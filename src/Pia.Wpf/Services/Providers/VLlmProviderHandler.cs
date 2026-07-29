using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Pia.Models;
using Pia.Services.Providers.Http;

namespace Pia.Services.Providers;

public sealed class VLlmProviderHandler : IAiProviderHandler
{
    public AiProviderType ProviderType => AiProviderType.VLlm;

    // VLlmThinkingHandler sets chat_template_kwargs.enable_thinking unconditionally (boolean only, no
    // effort granularity), so tools never turn thinking off here.
    public bool DropsReasoningEffortWithTools => false;

    public Task<IChatClient> CreateChatClientAsync(
        AiProvider provider,
        string? apiKey,
        HttpClient httpClient,
        string? mode,
        CancellationToken cancellationToken)
    {
        // vLLM does not understand `reasoning_effort`. Thinking is toggled via
        // `chat_template_kwargs.enable_thinking` at the top level of the
        // request body. A DelegatingHandler rewrites outgoing JSON to inject
        // (or strip) that field.
        var rewrite = new VLlmThinkingHandler(provider.ReasoningEffort ?? Pia.Models.ReasoningEffort.None)
        {
            InnerHandler = new HttpClientHandler(),
        };
        var http = new HttpClient(rewrite, disposeHandler: true);

        var client = new ChatClient(
            model: provider.ModelName ?? "Qwen/Qwen3-8B",
            credential: new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "unused" : apiKey),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(provider.Endpoint),
                Transport = new HttpClientPipelineTransport(http),
            }).AsIChatClient();

        return Task.FromResult(client);
    }

    public ChatOptions CreateChatOptions(AiProvider provider, bool hasTools)
    {
        // Reasoning is controlled via the DelegatingHandler; nothing belongs
        // on ChatOptions for vLLM.
        return new ChatOptions();
    }
}

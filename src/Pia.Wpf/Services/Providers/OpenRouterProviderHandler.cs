using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Pia.Models;
using Pia.Services.Providers.Http;

namespace Pia.Services.Providers;

public sealed class OpenRouterProviderHandler : IAiProviderHandler
{
    public AiProviderType ProviderType => AiProviderType.OpenRouter;

    public Task<IChatClient> CreateChatClientAsync(
        AiProvider provider,
        string? apiKey,
        HttpClient httpClient,
        string? mode,
        CancellationToken cancellationToken)
    {
        // Build a dedicated HttpClient stack with the rewrite handler so the
        // flat `reasoning_effort` field (which the OpenAI SDK emits) is
        // replaced with OpenRouter's nested `reasoning: { effort: ... }` shape.
        var rewrite = new OpenRouterReasoningHandler(provider.ReasoningEffort ?? Pia.Models.ReasoningEffort.None)
        {
            InnerHandler = new HttpClientHandler(),
        };
        var http = new HttpClient(rewrite, disposeHandler: true);
        http.DefaultRequestHeaders.Add("X-Title", "Pia");
        http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/Pia-Ai-dev/Pia.Wpf");

        var client = new ChatClient(
            model: provider.ModelName ?? "openai/gpt-4o-mini",
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
        // OpenRouter rejects the combination of flat `reasoning_effort` and
        // nested `reasoning: { effort }`. We rely on the DelegatingHandler to
        // emit the nested form, so we do NOT set ReasoningEffortLevel here —
        // letting the SDK include the flat field would cause 400s on reasoning
        // models. Tools route stays effort-free per existing convention.
        return new ChatOptions();
    }
}

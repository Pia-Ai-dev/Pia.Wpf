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

    // OpenRouterReasoningHandler rewrites the body to reasoning:{effort} unconditionally, tool-independent,
    // so a tool-using turn already carries the configured effort — nothing for a second turn to recover.
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
        // Build a dedicated HttpClient stack with the rewrite handler so the
        // flat `reasoning_effort` field (which the OpenAI SDK emits) is
        // replaced with OpenRouter's nested `reasoning: { effort: ... }` shape.
        var rewrite = new OpenRouterReasoningHandler(
            provider.ReasoningEffort ?? Pia.Models.ReasoningEffort.None,
            provider.EnableWebSearch)
        {
            InnerHandler = new HttpClientHandler(),
        };
        var http = new HttpClient(rewrite, disposeHandler: true);
        // AiClientService's per-call timeoutCts owns the bound; the 100s HttpClient default would fire first.
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.Add("X-Title", "Pia");
        http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/Pia-Ai-dev/Pia.Wpf");

        var client = new ChatClient(
            model: provider.ModelName ?? "openai/gpt-4o-mini",
            credential: new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "unused" : apiKey),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(provider.Endpoint),
                Transport = new HttpClientPipelineTransport(http),
                // The per-call timeoutCts owns the bound; the SDK's 100s network default would fire first.
                NetworkTimeout = Timeout.InfiniteTimeSpan,
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

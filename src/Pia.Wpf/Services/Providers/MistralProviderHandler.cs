using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Pia.Models;
using Pia.Services.Providers.Http;

namespace Pia.Services.Providers;

public sealed class MistralProviderHandler : IAiProviderHandler
{
    /// <summary>
    /// Mistral models that accept the OpenAI-style `reasoning_effort` field on
    /// `/v1/chat/completions`. Every other model (Magistral, Large, Codestral,
    /// embeddings) returns HTTP 422 "Extra inputs are not permitted" when the
    /// field is present.
    /// </summary>
    private static readonly HashSet<string> ReasoningCapableModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "mistral-small-latest",
        "mistral-medium-3.5",
    };

    public AiProviderType ProviderType => AiProviderType.Mistral;

    public Task<IChatClient> CreateChatClientAsync(
        AiProvider provider,
        string? apiKey,
        HttpClient httpClient,
        string? mode,
        CancellationToken cancellationToken)
    {
        var responseFilter = new MistralThinkingResponseHandler
        {
            InnerHandler = new HttpClientHandler(),
        };

        DelegatingHandler outerHandler = responseFilter;
        if (provider.EnableWebSearch && !string.IsNullOrWhiteSpace(provider.MistralAgentId))
            outerHandler = new MistralAgentsHandler(provider.MistralAgentId) { InnerHandler = responseFilter };

        var http = new HttpClient(outerHandler, disposeHandler: true);

        var client = new ChatClient(
            model: provider.ModelName ?? "mistral-small-latest",
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
        var emit = ShouldEmitReasoning(provider, hasTools);
        if (!emit.emit)
        {
            return new ChatOptions();
        }

        return new ChatOptions
        {
            RawRepresentationFactory = _ =>
            {
#pragma warning disable OPENAI001
                return new ChatCompletionOptions
                {
                    ReasoningEffortLevel = emit.level,
                };
#pragma warning restore OPENAI001
            },
        };
    }

#pragma warning disable OPENAI001
    internal static (bool emit, ChatReasoningEffortLevel level) ShouldEmitReasoning(AiProvider provider, bool hasTools)
    {
        if (hasTools) return (false, default);
        if (provider.ReasoningEffort is null) return (false, default);
        if (provider.ReasoningEffort == Pia.Models.ReasoningEffort.None) return (false, default);

        var model = provider.ModelName ?? string.Empty;
        if (!ReasoningCapableModels.Contains(model)) return (false, default);

        // Mistral accepts only `none` or `high`. We've already filtered `None`
        // above; everything else clamps to High.
        return (true, ChatReasoningEffortLevel.High);
    }
#pragma warning restore OPENAI001
}

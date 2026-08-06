using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Pia.Models;

namespace Pia.Services.Providers;

public sealed class OllamaProviderHandler : IAiProviderHandler
{
    public AiProviderType ProviderType => AiProviderType.Ollama;

    // Same ToOpenAi(effort, hasTools) tool gate as Azure: with tools attached the effort is never sent.
    public bool DropsReasoningEffortWithTools => true;

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
        var client = new ChatClient(
            model: provider.ModelName ?? "llama3.2",
            credential: new ApiKeyCredential("unused"),
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
    {
        var effort = ReasoningEffortMapping.ToOpenAi(provider.ReasoningEffort, hasTools);
        return new ChatOptions
        {
            RawRepresentationFactory = _ =>
            {
#pragma warning disable OPENAI001
                return new ChatCompletionOptions
                {
                    ReasoningEffortLevel = effort,
                };
#pragma warning restore OPENAI001
            },
        };
    }
}

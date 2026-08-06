using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Pia.Models;

namespace Pia.Services.Providers;

public sealed class AzureOpenAiProviderHandler : IAiProviderHandler
{
    public AiProviderType ProviderType => AiProviderType.AzureOpenAI;

    // ToOpenAi(effort, hasTools) omits the reasoning-effort parameter entirely when tools are present.
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
        var deployment = provider.AzureDeploymentName ?? provider.ModelName ?? "gpt-4o-mini";

        var client = new AzureOpenAIClient(
                new Uri(provider.Endpoint),
                new ApiKeyCredential(apiKey ?? string.Empty),
                new AzureOpenAIClientOptions
                {
                    Transport = new HttpClientPipelineTransport(httpClient),
                    // The per-call timeoutCts owns the bound; the SDK's 100s network default would fire first.
                    NetworkTimeout = Timeout.InfiniteTimeSpan,
                })
            .GetChatClient(deployment)
            .AsIChatClient();

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
                var options = new ChatCompletionOptions();
                if (effort is not null)
                    options.ReasoningEffortLevel = effort;
                return options;
#pragma warning restore OPENAI001
            },
        };
    }
}

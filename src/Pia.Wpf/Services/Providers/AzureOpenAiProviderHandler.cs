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

    public Task<IChatClient> CreateChatClientAsync(
        AiProvider provider,
        string? apiKey,
        HttpClient httpClient,
        string? mode,
        CancellationToken cancellationToken)
    {
        var deployment = provider.AzureDeploymentName ?? provider.ModelName ?? "gpt-4o-mini";

        var client = new AzureOpenAIClient(
                new Uri(provider.Endpoint),
                new ApiKeyCredential(apiKey ?? string.Empty),
                new AzureOpenAIClientOptions
                {
                    Transport = new HttpClientPipelineTransport(httpClient),
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
                return new ChatCompletionOptions
                {
                    ReasoningEffortLevel = effort,
                };
#pragma warning restore OPENAI001
            },
        };
    }
}

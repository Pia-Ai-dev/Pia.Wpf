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

    public Task<IChatClient> CreateChatClientAsync(
        AiProvider provider,
        string? apiKey,
        HttpClient httpClient,
        string? mode,
        CancellationToken cancellationToken)
    {
        var client = new ChatClient(
            model: provider.ModelName ?? "llama3.2",
            credential: new ApiKeyCredential("unused"),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(provider.Endpoint),
                Transport = new HttpClientPipelineTransport(httpClient),
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

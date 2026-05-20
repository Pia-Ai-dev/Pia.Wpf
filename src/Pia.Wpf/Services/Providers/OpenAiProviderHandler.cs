using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using Pia.Models;

namespace Pia.Services.Providers;

public sealed class OpenAiProviderHandler : IAiProviderHandler
{
    public AiProviderType ProviderType => AiProviderType.OpenAI;

    public Task<IChatClient> CreateChatClientAsync(
        AiProvider provider,
        string? apiKey,
        HttpClient httpClient,
        string? mode,
        CancellationToken cancellationToken)
    {
        var model = provider.ModelName ?? "gpt-4o-mini";

#pragma warning disable OPENAI001
        var client = new ResponsesClient(
            credential: new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "unused" : apiKey),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(provider.Endpoint),
                Transport = new HttpClientPipelineTransport(httpClient),
            }).AsIChatClient(model);
#pragma warning restore OPENAI001

        return Task.FromResult(client);
    }

    public ChatOptions CreateChatOptions(AiProvider provider, bool hasTools)
    {
        var effort = ReasoningEffortMapping.ToOpenAiResponses(provider.ReasoningEffort, hasTools);
        return new ChatOptions
        {
            RawRepresentationFactory = _ =>
            {
#pragma warning disable OPENAI001
                var options = new CreateResponseOptions();
                if (effort is not null)
                    options.ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = effort };
                return options;
#pragma warning restore OPENAI001
            },
        };
    }
}

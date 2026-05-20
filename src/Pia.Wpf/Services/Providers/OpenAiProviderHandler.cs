using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
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

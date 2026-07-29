using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Responses;
using Pia.Models;
using Pia.Services.Providers.Http;

namespace Pia.Services.Providers;

public sealed class OpenAiProviderHandler : IAiProviderHandler
{
    private readonly ILogger<OpenAiProviderHandler>? _logger;

    // Logger is optional so existing direct constructions in tests keep working; DI injects it at runtime.
    public OpenAiProviderHandler(ILogger<OpenAiProviderHandler>? logger = null) => _logger = logger;

    public AiProviderType ProviderType => AiProviderType.OpenAI;

    // Responses API: ToOpenAiResponses has no tool gate, so the configured effort already survives tools.
    public bool DropsReasoningEffortWithTools => false;

    public Task<IChatClient> CreateChatClientAsync(
        AiProvider provider,
        string? apiKey,
        HttpClient httpClient,
        string? mode,
        CancellationToken cancellationToken)
    {
        var model = provider.ModelName ?? "gpt-4o-mini";

        // Outermost handler retries without reasoning.summary if the org/model 400s on it,
        // so requesting a reasoning summary can never regress a working OpenAI provider.
        HttpMessageHandler tail = new HttpClientHandler();
        if (provider.EnableWebSearch)
            tail = new OpenAiWebSearchHandler { InnerHandler = tail };
        var http = new HttpClient(
            new OpenAiReasoningSummaryFallbackHandler(_logger) { InnerHandler = tail },
            disposeHandler: true);

#pragma warning disable OPENAI001
        var client = new ResponsesClient(
            credential: new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "unused" : apiKey),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(provider.Endpoint),
                Transport = new HttpClientPipelineTransport(http),
            }).AsIChatClient(model);
#pragma warning restore OPENAI001

        return Task.FromResult(client);
    }

    public ChatOptions CreateChatOptions(AiProvider provider, bool hasTools)
    {
        var effort = ReasoningEffortMapping.ToOpenAiResponses(provider.ReasoningEffort);
        return new ChatOptions
        {
            RawRepresentationFactory = _ =>
            {
#pragma warning disable OPENAI001
                var options = new CreateResponseOptions();
                if (effort is not null)
                    options.ReasoningOptions = new ResponseReasoningOptions
                    {
                        ReasoningEffortLevel = effort,
                        // Without an explicit summary request the Responses API returns no
                        // reasoning text at all. "Auto" lets the model emit whatever summary
                        // it supports, which Microsoft.Extensions.AI maps to TextReasoningContent.
                        ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Auto,
                    };
                return options;
#pragma warning restore OPENAI001
            },
        };
    }
}

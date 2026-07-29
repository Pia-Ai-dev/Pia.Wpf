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
    /// field is present. Includes the rolling `-latest` aliases, which resolve
    /// server-side to a reasoning-capable snapshot (e.g. mistral-medium-latest
    /// → mistral-medium-3.5) but must be matched here on the literal name.
    /// </summary>
    private static readonly HashSet<string> ReasoningCapableModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "mistral-small-latest",
        "mistral-medium-latest",
        "mistral-medium-3.5",
        // Both Magistral sizes are Mistral's reasoning family and think by DEFAULT, so both need
        // reasoning_effort sent explicitly — including `none` to suppress it. magistral-small-latest was
        // missing here while magistral-medium-latest was present, so on small the field was omitted and
        // reasoning stayed on regardless of the user's setting: the exact gap this set exists to close.
        "magistral-small-latest",
        "magistral-medium-latest",
    };

    public AiProviderType ProviderType => AiProviderType.Mistral;

    // ShouldEmitReasoning returns (false, default) for any non-None effort once hasTools is true, so
    // turning reasoning ON is dropped on a tool-using turn.
    // The flag is transport-level and deliberately cannot know whether ModelName is in
    // ReasoningCapableModels: for a non-reasoning Mistral model the planner therefore spends one extra
    // free-form turn at default effort. Accepted — reason-then-emit is itself the mechanism (the analysis
    // seeds the constrained turn); the boosted effort is an amplifier, not the whole benefit.
    public bool DropsReasoningEffortWithTools => true;

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
            outerHandler = new MistralConversationsHandler(provider.MistralAgentId) { InnerHandler = responseFilter };

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
        if (provider.ReasoningEffort is null) return (false, default);

        var model = provider.ModelName ?? string.Empty;
        if (!ReasoningCapableModels.Contains(model)) return (false, default);

        // Reasoning-capable Mistral models think by DEFAULT when `reasoning_effort`
        // is absent, so turning reasoning OFF means actively sending `none` — and
        // it must happen even on tool-using turns (the normal assistant case).
        // Omitting the field would silently leave reasoning on; that gap is the
        // bug this branch closes.
        if (provider.ReasoningEffort == Pia.Models.ReasoningEffort.None)
            return (true, ChatReasoningEffortLevel.None);

        // Turning reasoning ON stays gated on tool-free turns (reasoning during
        // tool calls is suppressed by design). Mistral accepts only `none` or
        // `high`, so every non-None value clamps to High.
        if (hasTools) return (false, default);
        return (true, ChatReasoningEffortLevel.High);
    }
#pragma warning restore OPENAI001
}

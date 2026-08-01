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

    // TRANSPORT FACT (what this flag states): ShouldEmitReasoning returns (false, default) for any non-None
    // effort once hasTools is true, and CreateChatOptions then returns a bare ChatOptions — so
    // `reasoning_effort` is OMITTED from every tool-using request. That is what `true` means here.
    //
    // What it does NOT mean for Mistral, on either half of the model list: that a tool-free turn recovers a
    // HIGHER effort. Neither half does, for different reasons.
    //   • Model NOT in ReasoningCapableModels: the field is never sent on either turn (the model-list check
    //     runs before the hasTools check), so the tool-free turn is at default effort as well. This is D7.
    //   • Model IN ReasoningCapableModels: an absent field leaves reasoning ON (see the comment in
    //     ShouldEmitReasoning), and Mistral's ladder is `none` | `high` only — so the tool-using turn is
    //     already thinking at the one ON rung that exists, and the tool-free turn's explicit `high` is the
    //     same rung. No boost to recover here either.
    // Accepted deliberately, for both halves: reason-then-emit is itself the mechanism (a free-form
    // decomposition the constrained turn consumes); the boosted effort is an amplifier, not the whole
    // benefit. So on Mistral the opt-in buys the split and not the boost — one extra round on a globally
    // opted-in setting. Do NOT "fix" this by flipping the flag to false: the flag is a transport constant
    // read off an uninitialised instance by the conformance test, and false would contradict the request
    // this handler demonstrably builds. Narrowing it needs a model-aware member, which D7 rejected.
    public bool DropsReasoningEffortWithTools => true;

    public Task<IChatClient> CreateChatClientAsync(
        AiProvider provider,
        string? apiKey,
        HttpClient httpClient,
        string? mode,
        // Ignored: this handler talks to a third-party provider, which has no server-side persona scope.
        Guid? managedPersonaId,
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

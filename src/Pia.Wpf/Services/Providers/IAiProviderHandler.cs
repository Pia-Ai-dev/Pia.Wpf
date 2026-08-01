using System.Net.Http;
using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Providers;

public interface IAiProviderHandler
{
    AiProviderType ProviderType { get; }

    /// <summary>
    /// TRANSPORT-ONLY: true when this handler's request shape omits the configured reasoning effort as soon
    /// as tools are attached — i.e. a tool-using turn is sent WITHOUT
    /// <see cref="AiProvider.ReasoningEffort"/> and therefore reasons at the provider's default, whatever
    /// that is. It says nothing about how good or bad that default is.
    /// <para>
    /// <c>AgentPlanner</c> reads it as "this plan turn is worth splitting into a free-form reasoning turn
    /// (tool-free, so the effort IS sent) followed by the constrained <c>emit_plan</c> turn". That is an
    /// APPROXIMATION, and knowingly so: on a provider whose default-on level already equals its maximum, the
    /// split still buys the free-form decomposition but recovers no extra effort (see the comment on
    /// <c>MistralProviderHandler</c>, which is exactly that case). Do not narrow the flag to "…and the boost
    /// is worth it" — no handler can answer that without knowing the model, which would turn a transport
    /// constant into a per-provider query.
    /// </para>
    /// <para>
    /// The knowledge lives next to the handler that HAS it: whether effort survives tools is decided by the
    /// exact request this handler builds in <see cref="CreateChatOptions"/> (or by the DelegatingHandler it
    /// installs in <see cref="CreateChatClientAsync"/>), so a future handler cannot silently inherit a wrong
    /// answer from a ProviderType switch living somewhere else.
    /// </para>
    /// <para>
    /// MUST be implemented as an expression-bodied constant (<c>=&gt; true;</c> / <c>=&gt; false;</c>) and never
    /// as an initialised auto-property: it is a transport constant, and the conformance test reads it off an
    /// instance created without running any constructor.
    /// </para>
    /// </summary>
    bool DropsReasoningEffortWithTools { get; }

    /// <param name="managedPersonaId">
    /// Only PiaCloudProviderHandler consumes this — third-party providers have no server-side persona scope.
    /// </param>
    Task<IChatClient> CreateChatClientAsync(
        AiProvider provider,
        string? apiKey,
        HttpClient httpClient,
        string? mode,
        Guid? managedPersonaId,
        CancellationToken cancellationToken);

    ChatOptions CreateChatOptions(AiProvider provider, bool hasTools);
}

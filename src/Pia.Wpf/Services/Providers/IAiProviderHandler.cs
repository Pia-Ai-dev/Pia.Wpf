using System.Net.Http;
using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Providers;

public interface IAiProviderHandler
{
    AiProviderType ProviderType { get; }

    /// <summary>
    /// True when this handler's request shape drops the configured reasoning effort as soon as tools are
    /// attached — i.e. a tool-using turn always reasons at the provider's DEFAULT effort no matter what
    /// <see cref="AiProvider.ReasoningEffort"/> says. <c>AgentPlanner</c> reads this to decide whether a
    /// plan turn is worth splitting into a free-form reasoning turn (tool-free, so the effort survives)
    /// followed by the constrained <c>emit_plan</c> turn.
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

    Task<IChatClient> CreateChatClientAsync(
        AiProvider provider,
        string? apiKey,
        HttpClient httpClient,
        string? mode,
        CancellationToken cancellationToken);

    ChatOptions CreateChatOptions(AiProvider provider, bool hasTools);
}

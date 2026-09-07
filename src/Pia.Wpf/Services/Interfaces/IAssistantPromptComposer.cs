using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// The fully-resolved setup for a single assistant turn: the system prompt to
/// send, the tool set (null when tools are gated off), and the flags the caller
/// needs afterwards (whether tools were enabled, whether web-search citations
/// should be post-processed).
/// </summary>
public sealed record AssistantTurnSetup(
    string SystemPrompt,
    IList<AITool>? Tools,
    bool SupportsTools,
    bool WebSearchActive,

    /// <summary>
    /// The persona driving this turn, sent as <c>X-Pia-Persona</c> so the server can scope persona-bound
    /// KBs/connectors. Null only when no persona was resolved. Trailing and defaulted so every existing
    /// construction site (all of which pass exactly the four members above) stays source-compatible.
    /// </summary>
    Guid? PersonaId = null,

    /// <summary>
    /// The persona's model-routing hint, sent as <c>metadata.pia_persona_type</c> on Pia Cloud chat
    /// requests. Null ⇒ no persona-type routing. Same trailing-default rule as <see cref="PersonaId"/>.
    /// </summary>
    string? ModelType = null,

    /// <summary>
    /// Whether a schedule (not a person) drove this turn. Carried so a per-step setup can mirror the run's
    /// shape without the resolver being told twice.
    /// </summary>
    bool Unattended = false);

/// <summary>
/// Builds the persona-driven system prompt and resolves the tool set for an
/// assistant turn (contract §5/§8). Extracted from <c>AssistantViewModel</c> so
/// the prompt composition is a single, independently testable responsibility.
/// </summary>
public interface IAssistantPromptComposer
{
    /// <summary>
    /// Composes the system prompt and tool list for one turn. Pass an empty
    /// <paramref name="atCommands"/> list for turns without @-commands (e.g. voice mode).
    /// </summary>
    /// <param name="suggestAgentModeEligible">
    /// When true (interactive Chat turn on a tool-capable provider), the <c>suggest_agent_mode</c> tool is
    /// injected so the model can offer to switch the user to Agent mode (R7). Only honoured inside the
    /// tools path with no @-commands; false everywhere by default (headless + voice-mode + Planned turns).
    /// </param>
    /// <param name="environmentRoot">
    /// Absolute folder the file tools resolve paths against, rendered as an <c>## Environment</c> block on
    /// the tools path. Null (the default) renders nothing and leaves the prompt unchanged.
    /// </param>
    /// <param name="unattended">
    /// True for a turn a schedule drove: renders the <c>## Unattended Run</c> block and withholds the
    /// routine-management tools, so a routine's own run cannot be talked into configuring itself.
    /// </param>
    AssistantTurnSetup PrepareTurn(
        Persona persona,
        AiProvider provider,
        IReadOnlyList<AtCommand> atCommands,
        bool tokenizationEnabled,
        bool suggestAgentModeEligible = false,
        string? environmentRoot = null,
        bool unattended = false);
}

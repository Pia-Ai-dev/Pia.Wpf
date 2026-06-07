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
    bool WebSearchActive);

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
    AssistantTurnSetup PrepareTurn(
        Persona persona,
        AiProvider provider,
        IReadOnlyList<AtCommand> atCommands,
        bool tokenizationEnabled);
}

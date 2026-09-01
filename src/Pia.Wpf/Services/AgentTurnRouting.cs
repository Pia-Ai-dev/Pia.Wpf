using Pia.Models;

namespace Pia.Services;

/// <summary>
/// How the agent spine's own turns — plan, replan, the optional reasoning turn, verify — reach Pia Cloud.
/// They carry no user persona, so without this they arrive with no mode at all and resolve to the server's
/// global default model, out of reach of every group routing layer.
/// </summary>
internal static class AgentTurnRouting
{
    /// <summary>Assistant is the only mode whose group persona-type mapping the server honours.</summary>
    public const string Mode = nameof(WindowMode.Assistant);

    /// <summary>Sent as <c>metadata.pia_persona_type</c>: emitting one structured tool call does not need the flagship model.</summary>
    public const string ModelType = "fast";
}

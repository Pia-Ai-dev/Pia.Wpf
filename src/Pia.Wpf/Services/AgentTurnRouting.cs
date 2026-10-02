using Pia.Models;
using Pia.Shared;

namespace Pia.Services;

/// <summary>Routes the persona-less spine turns through the group's persona-type mapping on Pia Cloud.</summary>
internal static class AgentTurnRouting
{
    /// <summary>Assistant is the only mode whose group persona-type mapping the server honours.</summary>
    public const string Mode = nameof(WindowMode.Assistant);

    /// <summary>Sent as <c>metadata.pia_persona_type</c>: emitting one structured tool call does not need the flagship model.</summary>
    public const string ModelType = "fast";

    /// <summary>Planning turns get their own type so a group can give them a stronger model than the other spine turns.</summary>
    public const string PlanModelType = PersonaTypes.Plan;
}

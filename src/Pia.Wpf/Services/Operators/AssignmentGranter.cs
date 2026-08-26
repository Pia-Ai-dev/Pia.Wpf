using Pia.Models;

namespace Pia.Services.Operators;

/// <summary>
/// Who authorised a send, for the consent audit line. A human is the only granter that names no id; an
/// unattended run names the routine that fired it, or itself when nothing scheduled it.
/// </summary>
public static class AssignmentGranter
{
    public const string User = "user";

    /// <summary>Keyed on the trigger KIND, not on the presence of a reference: an event-triggered run carries
    /// one too, and calling that a routine would put a wrong id in the audit file.</summary>
    public static string ForUnattendedRun(AgentRunTrigger trigger, Guid? triggerRef, Guid runId) =>
        trigger == AgentRunTrigger.Schedule && triggerRef is { } job
            ? $"routine:{job}"
            : $"background:{runId}";
}

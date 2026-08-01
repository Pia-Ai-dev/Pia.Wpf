using System.Text.Json;
using Pia.Models;

namespace Pia.Services;

/// <summary>
/// Reader for the PAUSE envelope a parked run carries in <c>AgentRuns.ExtraJson</c>:
/// <c>{"paused":true,"reason":"&lt;token&gt;"}</c>, written by <c>IAgentRunService.PauseAsync</c> and by the
/// startup reconcile's re-park of a parent that was awaiting children.
/// <para>
/// It exists as its own type because it has TWO consumers in different layers — the run-progress panel's
/// activity line and <see cref="AgentRunNotificationSurface"/>'s Flow body — and neither is allowed to reach into
/// the other. Duplicating the parse in both is exactly how the two would come to disagree about a reason token.
/// </para>
/// <para>
/// The reason vocabulary is a fixed set of APP-OWNED tokens (<c>"step-cap"</c>, <c>"wall-clock"</c>,
/// <see cref="AgentRunOrchestrator.ChildrenParkedReason"/>,
/// <see cref="AgentRunService.ChildrenInterruptedReason"/>, <see cref="AgentRunService.UserPausedReason"/>) —
/// never user content, so a consumer may key copy on
/// it and may log it. Same swallowing discipline as the truncation reader beside it: a malformed, absent or
/// foreign envelope reads as <c>null</c>, i.e. "no stated reason", never a guess.
/// <para>
/// Batch 08 adds <see cref="AgentRunService.UserPausedReason"/>, which is also the only token written to a run
/// at <see cref="Models.AgentRunState.Paused"/> rather than <see cref="Models.AgentRunState.WaitingForInput"/>
/// — the envelope shape is identical, so this reader needs no state knowledge.
/// </para>
/// </para>
/// </summary>
internal static class RunPauseEnvelope
{
    /// <summary>The pause reason, or <c>null</c> when the run carries no readable pause envelope.</summary>
    internal static string? ReadReason(AgentRun run)
    {
        if (string.IsNullOrEmpty(run.ExtraJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(run.ExtraJson);
            if (!doc.RootElement.TryGetProperty("paused", out var paused) || paused.ValueKind != JsonValueKind.True)
                return null;
            return doc.RootElement.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}

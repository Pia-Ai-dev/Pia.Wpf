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

    /// <summary>
    /// hermes #16. The TOOL a run parked on for a human decision, or <c>null</c> when this park carries no
    /// such member — which is every park but the approval one.
    /// <para>
    /// A SIBLING of <see cref="ReadReason"/> rather than a widened return, because that method has three
    /// production callers and a suite pinning it, and because the two facts are independent: a reader that
    /// only wants to know why a run parked must not be made to carry a tool name it will not use.
    /// </para>
    /// <para>
    /// It does NOT check the reason token. The <c>tool</c> member is only ever written alongside
    /// <see cref="AgentRunOrchestrator.ToolApprovalReason"/>, so keying it on the reason as well would be one
    /// more place for the two to disagree; a caller that needs the pairing tests the reason itself. Same
    /// swallowing discipline as its sibling: malformed, absent or foreign reads as <c>null</c>. A blank name
    /// also reads as <c>null</c> — a Continue card asking a human to approve <c>""</c> is worse than one
    /// asking them to approve an unnamed tool.
    /// </para>
    /// </summary>
    internal static string? ReadApprovalTool(AgentRun run)
    {
        if (string.IsNullOrEmpty(run.ExtraJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(run.ExtraJson);
            if (!doc.RootElement.TryGetProperty("paused", out var paused) || paused.ValueKind != JsonValueKind.True)
                return null;
            if (!doc.RootElement.TryGetProperty("tool", out var tool) || tool.ValueKind != JsonValueKind.String)
                return null;
            var name = tool.GetString();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// What the parked calls asked to act on — the paths behind the tool name, so Continue is not blind
    /// consent. Absent on every envelope written before the member existed and on every call that carried no
    /// string arguments, which both read as <c>null</c>: the affordances fall back to naming the tool alone.
    /// USER CONTENT — render it, never log it.
    /// </summary>
    internal static string? ReadApprovalArgs(AgentRun run)
    {
        if (string.IsNullOrEmpty(run.ExtraJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(run.ExtraJson);
            if (!doc.RootElement.TryGetProperty("paused", out var paused) || paused.ValueKind != JsonValueKind.True)
                return null;
            if (!doc.RootElement.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.String)
                return null;
            var text = args.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}

using Pia.Models;

namespace Pia.Services;

/// <summary>
/// The fully-resolved question put to <see cref="ToolAutonomy.Resolve"/>. Every membership test arrives as a
/// <c>bool</c> computed by the set's existing OWNER (04 D7): the three sets involved use three different
/// comparers today (<c>AutoApproveAllowlist</c> is <c>Ordinal</c>, the persisted grant keys are
/// case-sensitive, <c>grantedWrites</c> is <c>OrdinalIgnoreCase</c>) and this design changes none of them.
/// Moving those lookups inside the resolver would force one comparer on all three and silently change which
/// tools are eligible.
/// </summary>
/// <param name="Surface">Which gate is asking.</param>
/// <param name="ToolName">The tool name, as the model called it.</param>
/// <param name="ToolClass">From <see cref="ToolClassifier.Classify"/>, route-first.</param>
/// <param name="IsAllowlisted"><c>ToolPermissionService.IsAutoApproveEligible(name)</c>.</param>
/// <param name="HasStandingGrant"><c>IToolPermissionService.IsGranted(pluginId, name)</c>. False where there
/// is no persisted-grant concept (the unattended gate).</param>
/// <param name="IsNamedGrant"><c>grantedWrites.Contains(name)</c>. False where there is no grant list.</param>
/// <param name="Policy">The run's autonomy policy, or null for "today's behaviour".</param>
public readonly record struct ToolGateInput(
    ToolGateSurface Surface,
    string ToolName,
    ToolClass ToolClass,
    bool IsAllowlisted,
    bool HasStandingGrant,
    bool IsNamedGrant,
    RunAutonomyPolicy? Policy);

/// <summary>What the gate must do, and the audit reason Batch 03 persists.</summary>
public readonly record struct ToolGateVerdict(ToolGateOutcome Outcome, ToolGateDecision Decision);

/// <summary>
/// The ONE autonomy decision in the codebase (04 D5). Pure: no DI, no state, no I/O — deliberately NOT an
/// injectable service, because an interface a future policy could substitute is an interface that can
/// substitute the floor away.
/// <para>
/// Both the interactive gate (<c>ChatSession.HandleToolCall</c>) and the unattended gate
/// (<c>BackgroundAssistantTurnRunner.HandleToolCallAsync</c>) reach an auto-approval through exactly one
/// <see cref="Resolve"/> call, and voice mode joins them. That is what makes the destructive-external FLOOR
/// structural rather than merely current: it used to be two independent expressions over the same name
/// heuristic, with no shared chokepoint, so a policy branch added anywhere else would have bypassed both.
/// </para>
/// </summary>
public static class ToolAutonomy
{
    /// <summary>
    /// May this tool be offered — and honoured — as a one-click STANDING grant? The executable form of the
    /// historic interactive <c>eligible</c> expression
    /// (<c>IsAutoApproveEligible(t) || (IsMcpTool(t) &amp;&amp; !IsDeleteLike(t))</c>), with the route-derived
    /// <see cref="ToolClass.External"/> standing in for <c>IsMcpTool</c>. The card and the gate now compute it
    /// with this same function, so they cannot drift apart again.
    /// </summary>
    public static bool IsStandingGrantOfferable(ToolClass toolClass, string toolName, bool isAllowlisted)
        => isAllowlisted
           || (toolClass == ToolClass.External && !ToolPermissionService.IsDeleteLike(toolName));

    /// <summary>
    /// Resolve one gated tool call. The FLOOR is evaluated FIRST and unconditionally, so no policy value and
    /// no grant can reach an auto-approval past it; ordering it first (rather than ANDing it into each
    /// branch) means a branch added below inherits it by construction.
    /// <para>
    /// Name comparisons inside this method are case-INSENSITIVE (<c>IsDeleteLike</c> already is); the set
    /// membership tests are the caller's, per <see cref="ToolGateInput"/>.
    /// </para>
    /// </summary>
    public static ToolGateVerdict Resolve(in ToolGateInput input)
    {
        var isDeleteLike = ToolPermissionService.IsDeleteLike(input.ToolName);

        // FLOOR (M3) — a destructive EXTERNAL (MCP) tool. Interactively this suppresses auto-approval only:
        // a human is looking at the card and may still click "Allow once", which is today's semantics and is
        // deliberately not tightened here. Unattended (and in voice, which has no card) it refuses outright.
        if (input.ToolClass == ToolClass.External && isDeleteLike)
        {
            return input.Surface == ToolGateSurface.Interactive
                ? new ToolGateVerdict(ToolGateOutcome.Prompt, ToolGateDecision.Unknown)
                : new ToolGateVerdict(ToolGateOutcome.Refuse, ToolGateDecision.DeniedDestructiveFloor);
        }

        // POLICY — additive over classes, and NEVER over a delete-like name (04 D6). This is strictly
        // stronger than the floor above, which is external-only: ToolClass.Files holds both write_file and
        // delete_file, so without this a "let the agent write files" preset would hand an unattended run
        // card-free delete_file. A NAMED grant for a built-in delete still runs — that is the user's own
        // auditable decision and it is not this policy's doing.
        if (input.Policy is { } policy && policy.Covers(input.ToolClass) && !isDeleteLike)
            return new ToolGateVerdict(ToolGateOutcome.AutoRun, ToolGateDecision.AutoApprovedPolicy);

        // EXISTING AUTHORITY — unchanged semantics, per surface.

        // A persisted "always allow" the user clicked. Interactive and voice only (the unattended gate has
        // no persisted-grant concept and passes false).
        if (input.HasStandingGrant
            && IsStandingGrantOfferable(input.ToolClass, input.ToolName, input.IsAllowlisted))
        {
            return new ToolGateVerdict(ToolGateOutcome.AutoRun, ToolGateDecision.AutoApprovedStandingGrant);
        }

        // The curated additive allowlist (create_object / create_todo / create_reminder / append_to_list)
        // authorizes on VOICE only. Interactive requires the standing grant as well — an allowlisted tool
        // still shows a card the first time — and unattended has no allowlist at all, so widening it there
        // would silently grant four tools to every scheduled job.
        if (input.Surface == ToolGateSurface.Voice && input.IsAllowlisted)
            return new ToolGateVerdict(ToolGateOutcome.AutoRun, ToolGateDecision.AutoApprovedAllowlist);

        // A name in the run's grant list (headless launch envelope / scheduled job).
        if (input.IsNamedGrant)
            return new ToolGateVerdict(ToolGateOutcome.AutoRun, ToolGateDecision.GrantedByName);

        return input.Surface == ToolGateSurface.Interactive
            ? new ToolGateVerdict(ToolGateOutcome.Prompt, ToolGateDecision.Unknown)
            : new ToolGateVerdict(ToolGateOutcome.Refuse, ToolGateDecision.DeniedNotGranted);
    }
}

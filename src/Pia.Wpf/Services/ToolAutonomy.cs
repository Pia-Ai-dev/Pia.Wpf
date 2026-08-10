using Pia.Models;

namespace Pia.Services;

/// <summary>
/// The fully-resolved question put to <see cref="ToolAutonomy.Resolve"/>. Every membership test arrives as a
/// <c>bool</c> computed by the set's own owner, because the sets involved use three different comparers and
/// moving the lookups in here would force one comparer on all of them.
/// </summary>
/// <param name="Surface">Which gate is asking.</param>
/// <param name="ToolName">The tool name, as the model called it.</param>
/// <param name="ToolClass">From <see cref="ToolClassifier.Classify"/>, route-first.</param>
/// <param name="ServerDeclaredDestructive">The MCP server's own <c>ToolAnnotations.DestructiveHint</c>. It is
/// ORed into <see cref="ToolPermissionService.IsDeleteLike"/>, so it can only ever widen it.</param>
/// <param name="IsAllowlisted"><c>ToolPermissionService.IsAutoApproveEligible(name)</c>; read by the voice arm
/// alone.</param>
/// <param name="HasSessionGrant"><c>ISessionToolGrantStore.IsGranted(pluginId, name)</c> — the process-scoped
/// middle tier, and the one grant set that is never written anywhere.</param>
/// <param name="HasStandingGrant"><c>IToolPermissionService.IsGranted(pluginId, name)</c>. Read on every
/// surface: the grant is a durable statement about the tool, not about who is watching.</param>
/// <param name="IsNamedGrant"><c>grantedWrites.Contains(name)</c>. False where there is no grant list.</param>
/// <param name="HasNamedDenial"><c>deniedWrites.Contains(name)</c> — the run-scoped denial a tool-approval
/// park's Deny button wrote into the envelope. False where there is no denial list.</param>
/// <param name="Policy">The run's autonomy policy, or null for "today's behaviour".</param>
/// <param name="CanPark">May THIS run stop and ask a human instead of refusing? Only a ROOT unattended run
/// passes <c>true</c> — it is the only surface with somewhere durable to put the question.</param>
public readonly record struct ToolGateInput(
    ToolGateSurface Surface,
    string ToolName,
    ToolClass ToolClass,
    bool ServerDeclaredDestructive,
    bool IsAllowlisted,
    bool HasSessionGrant,
    bool HasStandingGrant,
    bool IsNamedGrant,
    bool HasNamedDenial,
    RunAutonomyPolicy? Policy,
    // Nothing here is defaulted: a new gate must answer every question out loud at compile time rather than
    // inherit a silent false.
    bool CanPark);

/// <summary>What the gate must do, and the audit reason persisted beside it.</summary>
public readonly record struct ToolGateVerdict(ToolGateOutcome Outcome, ToolGateDecision Decision);

/// <summary>
/// The ONE autonomy decision in the codebase. Pure: no DI, no state, no I/O — deliberately not an injectable
/// service, so no future policy can substitute the ordering below.
/// </summary>
/// <remarks>
/// The interactive gate (<c>ChatSession.HandleToolCall</c>), the unattended gate
/// (<c>BackgroundAssistantTurnRunner.HandleToolCallAsync</c>) and voice mode all reach an auto-approval
/// through exactly one <see cref="Resolve"/> call, so a branch added on any of them cannot bypass the others.
/// </remarks>
public static class ToolAutonomy
{
    /// <summary>
    /// Resolve one gated tool call. Order is the rule: the first arm that answers wins, so an arm added below
    /// inherits every narrowing above it.
    /// </summary>
    /// <remarks>Name comparisons here are case-INSENSITIVE (<c>IsDeleteLike</c> already is); the set
    /// membership tests are the caller's, per <see cref="ToolGateInput"/>.</remarks>
    public static ToolGateVerdict Resolve(in ToolGateInput input)
    {
        // Widened once, so the server's own declaration reaches the policy arm, the session tier and the park
        // together rather than three places to keep in step.
        var isDeleteLike = ToolPermissionService.IsDeleteLike(input.ToolName, input.ServerDeclaredDestructive);

        // PER-RUN HUMAN DENIAL — the person answered "no" for THIS run on a tool-approval park, and that
        // beats every auto-approval below: a settings toggle or a grant list must not re-approve what a human
        // just declined. Refuse, not Park: asking again would livelock the run on a settled decision.
        if (input.HasNamedDenial)
            return new ToolGateVerdict(ToolGateOutcome.Refuse, ToolGateDecision.DeniedForRun);

        // POLICY — the autonomy SWITCH, not a grant: additive over classes, and never over a delete-like name.
        // ToolClass.Files holds both write_file and delete_file, so without the exclusion a "let the agent
        // write files" preset would hand an unattended run card-free delete_file. A NAMED grant for a delete
        // still runs below — that is the user's own auditable decision and not this policy's doing.
        if (input.Policy is { } policy && policy.Covers(input.ToolClass) && !isDeleteLike)
            return new ToolGateVerdict(ToolGateOutcome.AutoRun, ToolGateDecision.AutoApprovedPolicy);

        // THE SESSION TIER, ABOVE the standing grant, the named grant, the interactive Prompt and the Park.
        // Above the first two so a call authorized by several tiers is audited as the one that is actually
        // revocable-by-restart; above the last two or it would authorize nothing at all — the card would come
        // back, or an unattended run would park on a capability a human already answered.
        //
        // Offered for EVERY tool, like the standing tier: this one is strictly weaker (it dies with the
        // process), so withholding it while "Always" is on offer only pushed a user toward the durable grant.
        //
        // VOICE IS EXCLUDED HERE, not at its call site, so the reason lives with the rule: a session grant is
        // collected on a CARD, and voice has neither a card nor a visible transcript to show what it
        // authorized. Voice passes the honest lookup anyway so the input stays a fact.
        //
        // AND UNATTENDED, NOT FOR ToolClass.External, for the same reason the PARK below refuses to ask about
        // one: an MCP tool's name and effect are server-defined, and every later call's arguments are unseen.
        if (input.HasSessionGrant
            && input.Surface != ToolGateSurface.Voice
            && (input.Surface != ToolGateSurface.Unattended || input.ToolClass != ToolClass.External))
        {
            return new ToolGateVerdict(ToolGateOutcome.AutoRun, ToolGateDecision.AutoApprovedSessionGrant);
        }

        // A persisted "always allow" the user clicked, on ANY tool: the Tool access page offers this tier for
        // every tool, so the gate honours it for every tool. On every surface too, headless included — the
        // user granted the TOOL, and a scheduled job that refused it would make the page's promise false.
        if (input.HasStandingGrant)
            return new ToolGateVerdict(ToolGateOutcome.AutoRun, ToolGateDecision.AutoApprovedStandingGrant);

        // The curated additive allowlist (create_todo / create_reminder) authorizes on VOICE only. Interactive
        // requires a standing grant as well — an allowlisted tool still shows a card the first time — and
        // unattended has no allowlist at all.
        //
        // BUILT-INS ONLY. `IsAutoApproveEligible` is a NAME-only set with no PluginId restriction, and
        // PluginService's tool-name routes are LAST-WINS with no collision detection, so an MCP server exposing
        // a tool literally named `create_todo` owns that route. Voice has no card and leaves no transcript
        // entry, so the allowlist alone must not authorize a third-party tool.
        if (input.Surface == ToolGateSurface.Voice
            && input.IsAllowlisted
            && input.ToolClass != ToolClass.External)
        {
            return new ToolGateVerdict(ToolGateOutcome.AutoRun, ToolGateDecision.AutoApprovedAllowlist);
        }

        // A name in the run's grant list (headless launch envelope / scheduled job).
        if (input.IsNamedGrant)
            return new ToolGateVerdict(ToolGateOutcome.AutoRun, ToolGateDecision.GrantedByName);

        if (input.Surface == ToolGateSurface.Interactive)
            return new ToolGateVerdict(ToolGateOutcome.Prompt, ToolGateDecision.Unknown);

        // THE UNATTENDED APPROVAL PARK. Nothing above authorized the call, so instead of a hard denial the run
        // stops and ASKS. It sits dead last because parking is the weakest possible answer: it changes nothing
        // about WHICH calls may run unattended, only what happens to the ones that may not.
        //
        // It refuses to ask about a delete-like or EXTERNAL tool, because the affordance it reuses is one
        // "Continue" button that shows no arguments — weaker evidence of consent than the grant list a named
        // grant carries, not stronger. Those stay a hard denial and the model is told to ask the user directly.
        // Surface is pinned too, even though only this gate passes CanPark: a park would leave a spoken turn
        // hanging on a Flow item the speaker cannot see.
        if (input.Surface == ToolGateSurface.Unattended
            && input.CanPark
            && !isDeleteLike
            && input.ToolClass != ToolClass.External)
        {
            return new ToolGateVerdict(ToolGateOutcome.Park, ToolGateDecision.ParkedForApproval);
        }

        return new ToolGateVerdict(ToolGateOutcome.Refuse, ToolGateDecision.DeniedNotGranted);
    }
}

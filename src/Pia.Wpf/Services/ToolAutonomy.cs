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
/// <param name="ServerDeclaredDestructive">
/// T2-7b. <c>PluginToolCall.ServerDeclaredDestructive</c> — the MCP server's own
/// <c>ToolAnnotations.DestructiveHint</c>, which widens <see cref="ToolPermissionService.IsDeleteLike"/> for
/// this one call and can never narrow it.
/// <para>
/// DELIBERATELY NOT DEFAULTED, for the same reason <see cref="CanPark"/> and <see cref="HasSessionGrant"/> are
/// not: it breaks all three <see cref="ToolAutonomy.Resolve"/> call sites at compile time, so a fourth gate
/// cannot be added without answering "does this surface know what the server declared?" out loud. A surface
/// with no pending action to read it from must pass <c>false</c> and say so, rather than inherit a default that
/// silently discards the one hint an MCP server can send us.
/// </para>
/// </param>
/// <param name="IsAllowlisted"><c>ToolPermissionService.IsAutoApproveEligible(name)</c>.</param>
/// <param name="HasSessionGrant">
/// hermes #15. <c>ISessionToolGrantStore.IsGranted(pluginId, name)</c> — the PROCESS-scoped middle tier,
/// reached interactively through <c>IToolPermissionService.IsGrantedForSession</c> and unattended through
/// <c>ToolApprovalStore.HasSessionGrant</c>. Caller knowledge like the other membership bools; the fourth
/// grant set in the tree and the only one that is never written anywhere.
/// </param>
/// <param name="HasStandingGrant"><c>IToolPermissionService.IsGranted(pluginId, name)</c>. False where there
/// is no persisted-grant concept (the unattended gate).</param>
/// <param name="IsNamedGrant"><c>grantedWrites.Contains(name)</c>. False where there is no grant list.</param>
/// <param name="Policy">The run's autonomy policy, or null for "today's behaviour".</param>
/// <param name="CanPark">
/// hermes #16. May THIS run stop and ask a human instead of refusing? Caller knowledge, exactly like the
/// three membership bools above: only the unattended gate ever passes <c>true</c>, and only for a run that
/// has somewhere to put the question (a ROOT run, whose park raises a durable Flow Continue card and a panel
/// Continue button). A child run passes <c>false</c> — it is a delegate that does the work its parent asked
/// for, and hermes #8 pins it to default-deny — and so do the interactive and voice gates, which have their
/// own answer for "no authority" already.
/// <para>
/// It is a permission to ASK, never a permission to run: <see cref="ToolAutonomy.Resolve"/> evaluates the
/// destructive-external FLOOR before it, and refuses to park a delete-like tool of ANY class. See the Park
/// arm for why.
/// </para>
/// </param>
public readonly record struct ToolGateInput(
    ToolGateSurface Surface,
    string ToolName,
    ToolClass ToolClass,
    // T2-7b. Positioned with the other two facts about the TOOL rather than among the membership bools, and
    // not defaulted — see the param docs above.
    bool ServerDeclaredDestructive,
    bool IsAllowlisted,
    // DELIBERATELY NOT DEFAULTED either, and for the same reason CanPark is not: hermes #15 adds a way for a
    // call to be authorized, so a gate that does not answer "does this surface honour a session grant?" out
    // loud must not compile.
    bool HasSessionGrant,
    bool HasStandingGrant,
    bool IsNamedGrant,
    RunAutonomyPolicy? Policy,
    // DELIBERATELY NOT DEFAULTED. A positional member with no default breaks all three Resolve call sites at
    // compile time, which is the property this record was given (04 D7) and the reason a fourth gate cannot be
    // added without answering "may this surface park?" out loud.
    bool CanPark);

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
    /// <param name="serverDeclaredDestructive">T2-7b, threaded here and not only into <see cref="Resolve"/>:
    /// the floor already refuses to AUTO-RUN a server-declared-destructive external tool, but without this the
    /// card would still offer "Always allow" for it and persist a grant the floor then ignores forever — a
    /// button that does nothing, which is exactly the card-vs-gate drift 04 D4/D5 collapsed into one
    /// function.</param>
    public static bool IsStandingGrantOfferable(
        ToolClass toolClass, string toolName, bool isAllowlisted, bool serverDeclaredDestructive = false)
        => isAllowlisted
           || (toolClass == ToolClass.External
               && !ToolPermissionService.IsDeleteLike(toolName, serverDeclaredDestructive));

    /// <summary>
    /// hermes #15. May this tool be offered — and honoured — as a SESSION grant? Used by the card to decide
    /// what to offer and by both gates to decide what to mint and what to honour, so the offer and the
    /// authority cannot drift apart (the 04 D4/D5 property).
    /// <para>
    /// NAME ONLY, and deliberately NOT <see cref="IsStandingGrantOfferable"/>. The standing rule collapses to
    /// <c>isAllowlisted</c> for every non-<see cref="ToolClass.External"/> tool, and the unattended gate has no
    /// allowlist at all (it passes <c>IsAllowlisted: false</c>) — so reusing it would make the middle tier
    /// unreachable on the one surface that needs it most, and would leave the tool a user actually approves
    /// forty times a session (<c>write_file</c>) with no tier between "once" and "never".
    /// </para>
    /// <para>
    /// The three exclusions are the whole rule, and all three are about what a MULTI-CALL grant can consent to.
    /// One card shows the arguments of ONE call; a session grant authorizes every later call of that tool with
    /// arguments the user will never see. So:
    /// <list type="bullet">
    /// <item><see cref="ToolPermissionService.IsDeleteLike"/> — no destructive tool of any class. Wider than
    /// the FLOOR (external-only) on purpose, and the same line the unattended park draws.</item>
    /// <item><see cref="ToolPermissionService.IsWorkDiscarding"/> — <c>git_switch</c> / <c>git_restore</c> /
    /// <c>git_stash</c> shed uncommitted work while carrying no "delete" in the name. This exclusion used to
    /// live in <c>ActionCardBuilder</c> as the card's own stricter rule; the session tier shares it rather
    /// than re-deriving it, so the gate's mint check is exactly as narrow as the card's offer.</item>
    /// <item><see cref="ToolPermissionService.IsAuthorityAuthoring"/> — the review pass on #15 found the case
    /// the "arguments nobody will see" reasoning above points straight at and the first two exclusions miss:
    /// a tool whose ARGUMENTS ARE THEMSELVES A GRANT LIST. <c>create_scheduled_research</c> is neither
    /// delete-like nor work-discarding, so one click on "Allow this session" on a benign-looking "create a
    /// scheduled job" card authorized every LATER job-authoring call in the process, card-free — and that
    /// tool's <c>grantedTools</c> argument may name <c>delete_file</c> (its create-time filter strips only
    /// PRESUMED-EXTERNAL destructive names, by design), which the unattended gate then auto-runs as a named
    /// grant. The persisted tier was never on offer here
    /// (<c>IsStandingGrantOfferable(Scheduling, "create_scheduled_research", false)</c> is false), so without
    /// this the middle tier was strictly WIDER than the permanent one for the one tool that mints authority.
    /// </item>
    /// </list>
    /// It admits a non-destructive EXTERNAL (MCP) tool, which the standing tier already offers — the middle
    /// tier must not be the only one missing for the tools a user is prompted for most. (What it may do
    /// UNATTENDED is narrower still; see the session arm in <see cref="Resolve"/>.)
    /// </para>
    /// </summary>
    /// <param name="serverDeclaredDestructive">T2-7b. The first exclusion below is "no destructive tool of any
    /// class", and a server that declares its own tool destructive is naming exactly that — so the middle tier
    /// must not be the one place the declaration is ignored, or "Allow for this session" would be the widest
    /// authority on offer for the tool the server itself flagged.</param>
    public static bool IsSessionGrantOfferable(string toolName, bool serverDeclaredDestructive = false)
        => !ToolPermissionService.IsDeleteLike(toolName, serverDeclaredDestructive)
           && !ToolPermissionService.IsWorkDiscarding(toolName)
           && !ToolPermissionService.IsAuthorityAuthoring(toolName);

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
        // T2-7b: ONE line, and deliberately this one. Every branch below reads `isDeleteLike` — the floor, the
        // policy arm, the session tier (through IsSessionGrantOfferable) and the park — so widening it here is
        // what makes the server's declaration reach all of them at once, instead of four places to keep in step.
        var isDeleteLike = ToolPermissionService.IsDeleteLike(input.ToolName, input.ServerDeclaredDestructive);

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

        // hermes #15 THE SESSION TIER. Positioned exactly where the requirement puts it: BELOW the floor and
        // the policy (a one-click grant must not lift either), and ABOVE the persisted standing grant, the
        // named grant, the interactive Prompt and the Park arm. Each of those four is load-bearing:
        //
        //  * above the STANDING grant, so a call authorized by both is audited as the tier that is actually
        //    revocable-by-restart rather than as one the user would look for in Settings and not find. The
        //    overlap is nearly inert in practice — a tool with a standing grant never shows a card, so no
        //    session grant is ever collected for it — but the ordering is what makes the audit unambiguous.
        //  * above the NAMED grant, so a resumed run that was widened by its own Continue AND holds a session
        //    grant records AutoApprovedSessionGrant. Without that ordering, "the session grant reached the
        //    resumed run" would be indistinguishable from the resume's per-run widening.
        //  * above the interactive Prompt, or the tier would authorize nothing at all: the card would come
        //    back for a tool the user just granted for the session.
        //  * above the PARK, or an unattended run holding a session grant would stop and ask for a capability
        //    a human already answered — which is the same "asked forty times" failure one layer out.
        //
        // VOICE IS EXCLUDED HERE, not at its call site, so the reason lives with the rule: a session grant is
        // collected on a CARD, and this tier is wider than the standing tier voice already honours (it covers
        // write_file). Honouring it on a surface with no card and no visible transcript would silently widen
        // what a spoken turn may do, on evidence the speaker never sees. Voice keeps exactly the authority it
        // had, and passes the honest lookup anyway so the input stays a fact and the policy stays here.
        //
        // AND UNATTENDED, NOT FOR ToolClass.External. The review pass on #15 found this arm auto-running the
        // exact class the PARK 60 lines below refuses to raise a question about: `send_email` is not
        // delete-like, so the floor and IsSessionGrantOfferable both pass it, and
        // Resolve(Unattended, "send_email", External, sessionGrant: true) returned AutoRun where the same input
        // without the grant returned Refuse — it did not even reach the park. The park's written reason applies
        // with MORE force here, not less: an MCP tool's name and effect are server-defined, the card that
        // collected the grant showed ONE call's arguments, and every later call's arguments are invisible. So
        // unattended the tier is honoured only for the classes the park would have been willing to ask about,
        // and an external write stays the hard denial it was. Interactively it is still admitted — a human is
        // looking at the card, and the standing tier already offers non-destructive external tools.
        if (input.HasSessionGrant
            && input.Surface != ToolGateSurface.Voice
            && (input.Surface != ToolGateSurface.Unattended || input.ToolClass != ToolClass.External)
            && IsSessionGrantOfferable(input.ToolName, input.ServerDeclaredDestructive))
        {
            return new ToolGateVerdict(ToolGateOutcome.AutoRun, ToolGateDecision.AutoApprovedSessionGrant);
        }

        // A persisted "always allow" the user clicked. Interactive and voice only (the unattended gate has
        // no persisted-grant concept and passes false).
        if (input.HasStandingGrant
            && IsStandingGrantOfferable(
                input.ToolClass, input.ToolName, input.IsAllowlisted, input.ServerDeclaredDestructive))
        {
            return new ToolGateVerdict(ToolGateOutcome.AutoRun, ToolGateDecision.AutoApprovedStandingGrant);
        }

        // The curated additive allowlist (create_object / create_todo / create_reminder / append_to_list)
        // authorizes on VOICE only. Interactive requires the standing grant as well — an allowlisted tool
        // still shows a card the first time — and unattended has no allowlist at all, so widening it there
        // would silently grant four tools to every scheduled job.
        //
        // BUILT-INS ONLY. `IsAutoApproveEligible` is a NAME-only set with no PluginId restriction, and
        // PluginService's tool-name routes are LAST-WINS with no collision detection (§13.4), so an MCP server
        // exposing a tool literally named `create_todo` owns that route. Interactively that shadowing is
        // contained because this surface additionally needs a standing grant the user clicked on a card; voice
        // has no card and leaves no transcript entry, so the allowlist alone must not authorize a third-party
        // tool. All four allowlisted names are ours, so excluding External narrows nothing intended.
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

        // THE UNATTENDED APPROVAL PARK (hermes #16). Nothing above this line authorized the call, and until
        // now that meant a hard denial even for a capability a human would happily have approved — a headless
        // run that needed one ungranted write told the model "Do not retry" and produced a run that had
        // stopped without saying so. It now stops and ASKS, on the Batch 06 park the budget cap already uses.
        //
        // It sits HERE — dead last, below every authority branch and far below the FLOOR — because parking is
        // the weakest possible answer: it changes nothing about which calls may run unattended, only about
        // what happens to the ones that may not. Two conditions guard it, and each is load-bearing:
        //
        //  * !isDeleteLike, so no destructive tool of ANY class ever parks. The FLOOR above already refuses a
        //    destructive EXTERNAL one, but this is strictly wider and covers the built-ins the floor lets a
        //    named grant run (delete_file, forget). The asymmetry is deliberate: an interactive card shows the
        //    ARGUMENTS of the call it is asking about, and the Continue affordance a park reuses does not — it
        //    is one button whose whole vocabulary is "carry on". Approving `delete_file` blind, for a run that
        //    will then re-execute the step and choose its own path, is not informed consent. An irreversible
        //    action stays a hard denial and the model is told to ask the user directly.
        //  * ToolClass != External, for the SAME reason one step further out, and it is not covered by
        //    !isDeleteLike: `send_email` and `create_issue` are not delete-like, so without this clause an
        //    ungranted MCP write raised a Continue button naming a SERVER-DEFINED tool whose arguments the card
        //    never shows and whose effects are outside the run's workspace containment entirely. The floor above
        //    already holds that "an MCP tool's name and effect are server-defined, so a grant list authored days
        //    earlier is not informed consent" — a single unlabelled button is weaker evidence of consent than
        //    that grant list, not stronger. So an external write stays the hard denial it was before #16, and
        //    the park keeps its scope: the run's OWN capabilities, which the user can reason about.
        //  * Surface == Unattended, even though only that gate passes CanPark today. Voice has no card either,
        //    and a park would leave a spoken turn hanging on a Flow item the speaker cannot see.
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

using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// 04 D5/D6: the ONE autonomy resolver both gates call. The destructive floor is evaluated FIRST and
/// unconditionally, so these tests are the executable form of "no value of the policy document can reach an
/// auto-approval past the floor" — asserted over the whole policy value space, not a sample.
/// </summary>
public class ToolAutonomyTests
{
    private static readonly ToolGateSurface[] AllSurfaces = Enum.GetValues<ToolGateSurface>();

    private static readonly ToolClass[] AllClasses = Enum.GetValues<ToolClass>();

    /// <summary>Every <c>DestructiveStems</c> member as a tool name, plus the literal <c>forget</c>.</summary>
    private static readonly string[] DeleteLikeNames =
    [
        "delete_thing", "remove_thing", "purge_thing", "drop_thing", "wipe_thing",
        "erase_thing", "destroy_thing", "truncate_thing", "forget",
    ];

    private static readonly RunAutonomyPolicy EveryClassPolicy = new(AllClasses);

    private static ToolGateInput Input(
        ToolGateSurface surface,
        string toolName,
        ToolClass toolClass,
        RunAutonomyPolicy? policy = null,
        bool allowlisted = false,
        bool standingGrant = false,
        bool namedGrant = false,
        bool canPark = false,
        // hermes #15. Defaulted like the rest, so the AXIS is what the exhaustive facts below must add
        // explicitly — a defaulted parameter silently keeps a matrix green while it stops covering the new
        // dimension, which is the failure mode this branch has been bitten by four times.
        bool sessionGrant = false)
        => new(surface, toolName, toolClass, allowlisted, sessionGrant, standingGrant, namedGrant, policy, canPark);

    /// <summary>
    /// T-FLOOR-1. A single Fact with nested loops rather than a ~3.5k-case Theory: the assertion is the same
    /// exhaustive cross product, it adds no xUnit analyzer surface, and it does not inflate the suite total.
    /// </summary>
    [Fact]
    public void DestructiveExternalTool_IsNeverAutoRun_AcrossTheEntirePolicySpace()
    {
        var violations = new List<string>();

        foreach (var surface in AllSurfaces)
        foreach (var toolClass in AllClasses)
        foreach (var name in DeleteLikeNames)
        foreach (var policy in new RunAutonomyPolicy?[] { null, new RunAutonomyPolicy([toolClass]), EveryClassPolicy })
        foreach (var granted in new[] { false, true })
        foreach (var allowlisted in new[] { false, true })
        foreach (var named in new[] { false, true })
        // hermes #16 added the FOURTH axis, and it is not decoration: CanPark is a new way in, so the
        // "no value of the inputs reaches an auto-approval past the floor" claim is only still exhaustive
        // if the park's own permission is part of the value space.
        foreach (var canPark in new[] { false, true })
        // hermes #15 added the FIFTH axis for the same reason: the session tier is a new way for a call to be
        // authorized, so "no value of the inputs reaches an auto-approval past the floor" is only still an
        // exhaustive claim if the session grant is part of the value space.
        foreach (var sessionGrant in new[] { false, true })
        {
            var verdict = ToolAutonomy.Resolve(Input(
                surface, name, toolClass, policy,
                allowlisted: allowlisted, standingGrant: granted, namedGrant: named, canPark: canPark,
                sessionGrant: sessionGrant));

            // The M3 FLOOR: a delete-like EXTERNAL tool never auto-runs, whatever the policy says and
            // however it was granted.
            var floorBroken = toolClass == ToolClass.External && verdict.Outcome == ToolGateOutcome.AutoRun;

            // D6, which is strictly stronger and applies to EVERY class: a POLICY may never be the reason a
            // delete-like tool ran. (A NAMED grant for a built-in delete still may — that is the user's own
            // auditable decision, pinned by PolicyNeverCoversADeleteLikeTool_EvenABuiltInOne.)
            var policyBroken = verdict.Decision == ToolGateDecision.AutoApprovedPolicy;

            // hermes #16: and a delete-like tool of ANY class never PARKS. Wider than the floor on purpose —
            // the floor is external-only and lets a named grant run delete_file, whereas the park would put
            // an irreversible action behind a one-button Continue that shows no arguments.
            var parkBroken = verdict.Outcome == ToolGateOutcome.Park;

            // hermes #15, and the same shape as policyBroken: a SESSION grant may never be the reason a
            // delete-like tool ran, on any surface and in any class. One card shows one call's arguments; a
            // multi-call grant would be authorizing deletions the user never saw.
            var sessionBroken = verdict.Decision == ToolGateDecision.AutoApprovedSessionGrant;

            if (floorBroken || policyBroken || parkBroken || sessionBroken)
            {
                violations.Add(
                    $"{surface}/{toolClass}/{name}/policy={(policy is null ? "none" : string.Join('+', policy.AutoApproveClasses))}"
                    + $"/granted={granted}/allowlisted={allowlisted}/named={named}/canPark={canPark}"
                    + $"/session={sessionGrant}"
                    + $" => {verdict.Outcome} {verdict.Decision}");
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// T-PARK-A (hermes #16), <b>GUARD</b>. The park is UNATTENDED-ONLY, asserted over the whole input space
    /// with <c>canPark</c> forced true: the interactive surface has a card (a park would be strictly worse
    /// than the card it replaces) and voice has a speaker who would never see a Flow item.
    /// <para>
    /// The non-vacuity control is inside the same loop: the unattended surface DOES park these exact inputs,
    /// so a resolver that had simply stopped parking altogether reds here rather than passing.
    /// </para>
    /// <para>Neutralize: drop <c>Surface == ToolGateSurface.Unattended</c> from the Park arm → red.</para>
    /// </summary>
    [Fact]
    public void OnlyTheUnattendedSurfaceEverParks_EvenWhenTheCallerPermitsIt()
    {
        var violations = new List<string>();
        var unattendedParks = 0;

        // Promptable names only — a delete-like name never parks anywhere, which T-FLOOR-1 above now covers.
        foreach (var surface in AllSurfaces)
        foreach (var toolClass in AllClasses)
        foreach (var name in new[] { "write_file", "update_todo", "some_mcp_action" })
        foreach (var policy in new RunAutonomyPolicy?[] { null, EveryClassPolicy })
        foreach (var allowlisted in new[] { false, true })
        {
            var verdict = ToolAutonomy.Resolve(Input(
                surface, name, toolClass, policy, allowlisted: allowlisted, canPark: true));

            if (verdict.Outcome != ToolGateOutcome.Park)
                continue;

            if (surface == ToolGateSurface.Unattended)
                unattendedParks++;
            else
                violations.Add($"{surface}/{toolClass}/{name}/allowlisted={allowlisted} => parked");
        }

        Assert.Empty(violations);
        Assert.True(unattendedParks > 0, "no input parked at all — the guard above would then be vacuous");
    }

    /// <summary>
    /// T-PARK-B (hermes #16). <c>canPark</c> is a permission to ASK, never to run: the same input that parks
    /// with it refuses without it, and NEITHER executes. The park changes what happens to a call the gate was
    /// always going to withhold — it does not widen the set of calls that run unattended.
    /// </summary>
    [Fact]
    public void CanPark_ChangesRefuseIntoAsk_AndNeverIntoRun()
    {
        var refused = ToolAutonomy.Resolve(Input(ToolGateSurface.Unattended, "write_file", ToolClass.Files));
        Assert.Equal(ToolGateOutcome.Refuse, refused.Outcome);
        Assert.Equal(ToolGateDecision.DeniedNotGranted, refused.Decision);

        var parked = ToolAutonomy.Resolve(Input(ToolGateSurface.Unattended, "write_file", ToolClass.Files, canPark: true));
        Assert.Equal(ToolGateOutcome.Park, parked.Outcome);
        Assert.Equal(ToolGateDecision.ParkedForApproval, parked.Decision);

        // The point of the pair: neither is AutoRun, so no value of canPark authorizes anything.
        Assert.NotEqual(ToolGateOutcome.AutoRun, refused.Outcome);
        Assert.NotEqual(ToolGateOutcome.AutoRun, parked.Outcome);
    }

    // ------------------------------------------------- hermes #15, THE SESSION TIER, at the resolver

    /// <summary>
    /// T-SESS-A. What the session tier may NEVER cover, asserted over the whole input space rather than a
    /// sample: a delete-like name (any class) and a work-discarding one (git_switch/git_restore/git_stash).
    /// A session grant authorizes calls whose ARGUMENTS the user will never see, so the rule is wider than
    /// the destructive-external floor by construction.
    /// <para>
    /// The non-vacuity control is in the same test: a promptable name with the same grant DOES auto-run on the
    /// two surfaces that honour the tier, so a resolver that had simply stopped honouring session grants reds
    /// here instead of passing.
    /// </para>
    /// <para><b>Neutralize:</b> drop <c>IsSessionGrantOfferable</c> from the session arm → red.</para>
    /// </summary>
    [Fact]
    public void SessionGrant_NeverCoversADeleteLikeOrWorkDiscardingTool()
    {
        var violations = new List<string>();

        foreach (var surface in AllSurfaces)
        foreach (var toolClass in AllClasses)
        foreach (var name in DeleteLikeNames.Concat(new[] { "git_switch", "git_restore", "git_stash" }))
        foreach (var canPark in new[] { false, true })
        {
            var verdict = ToolAutonomy.Resolve(Input(
                surface, name, toolClass, policy: null, sessionGrant: true, canPark: canPark));

            if (verdict.Decision == ToolGateDecision.AutoApprovedSessionGrant)
                violations.Add($"{surface}/{toolClass}/{name}/canPark={canPark} => {verdict.Outcome}");
        }

        Assert.Empty(violations);

        // The control: the SAME grant on a promptable name does authorize, so the loop above is not passing
        // because nothing is honoured any more.
        foreach (var surface in new[] { ToolGateSurface.Interactive, ToolGateSurface.Unattended })
        {
            var honoured = ToolAutonomy.Resolve(Input(surface, "write_file", ToolClass.Files, sessionGrant: true));
            Assert.Equal(ToolGateOutcome.AutoRun, honoured.Outcome);
            Assert.Equal(ToolGateDecision.AutoApprovedSessionGrant, honoured.Decision);
        }
    }

    /// <summary>
    /// T-SESS-B. VOICE never honours a session grant. It is collected on a card the speaker cannot see, and
    /// the tier is wider than the standing grant voice already honours (it covers <c>write_file</c>), so
    /// honouring it here would silently widen what a spoken turn may do.
    /// <para><b>Neutralize:</b> drop <c>Surface != Voice</c> from the session arm → red.</para>
    /// </summary>
    [Fact]
    public void VoiceSurface_NeverHonoursASessionGrant()
    {
        foreach (var toolClass in AllClasses)
        foreach (var name in new[] { "write_file", "update_todo", "some_mcp_action", "create_todo" })
        {
            var verdict = ToolAutonomy.Resolve(Input(
                ToolGateSurface.Voice, name, toolClass, sessionGrant: true));

            Assert.NotEqual(ToolGateDecision.AutoApprovedSessionGrant, verdict.Decision);
        }

        // Control: the standing grant voice DOES honour is unaffected by this batch.
        var standing = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Voice, "some_mcp_action", ToolClass.External, standingGrant: true));
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, standing.Decision);
    }

    /// <summary>
    /// T-SESS-C. WHERE the tier sits, as four separate facts about the same input — this is the ordering the
    /// requirement pins ("between the once-decision and the persisted list").
    /// <list type="bullet">
    /// <item>ABOVE the park: an unattended run holding the grant RUNS instead of stopping to ask again.</item>
    /// <item>ABOVE the named grant: a run widened by its own Continue AND holding a session grant is audited
    /// as the session tier — the discriminator the park-interaction tests depend on.</item>
    /// <item>ABOVE the interactive prompt: no card for a tool granted this session.</item>
    /// <item>BELOW the floor: it does not lift the destructive-external refusal.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void SessionGrant_OutranksTheParkAndTheNamedGrant_ButNotTheFloor()
    {
        var overPark = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "write_file", ToolClass.Files, canPark: true, sessionGrant: true));
        Assert.Equal(ToolGateOutcome.AutoRun, overPark.Outcome);
        Assert.Equal(ToolGateDecision.AutoApprovedSessionGrant, overPark.Decision);

        var overNamed = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "write_file", ToolClass.Files, namedGrant: true, sessionGrant: true));
        Assert.Equal(ToolGateDecision.AutoApprovedSessionGrant, overNamed.Decision);

        var overPrompt = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Interactive, "write_file", ToolClass.Files, sessionGrant: true));
        Assert.Equal(ToolGateOutcome.AutoRun, overPrompt.Outcome);

        // …and BELOW the floor: a destructive external tool is still refused unattended and still only
        // CARDED interactively, session grant or not.
        var floor = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "delete_issue", ToolClass.External, sessionGrant: true, canPark: true));
        Assert.Equal(ToolGateOutcome.Refuse, floor.Outcome);
        Assert.Equal(ToolGateDecision.DeniedDestructiveFloor, floor.Decision);

        // The standing tier keeps its own decision when it is the only authority (no silent re-labelling).
        var standing = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Interactive, "create_todo", ToolClass.Todo, allowlisted: true, standingGrant: true));
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, standing.Decision);
    }

    /// <summary>
    /// T-SESS-D. The offerability rule itself: what the card may offer and the gate may mint are ONE function,
    /// and it is name-only (unlike the standing rule, which collapses to the allowlist off the External class
    /// and would therefore make the tier unreachable unattended).
    /// </summary>
    [Fact]
    public void SessionGrantOfferable_AdmitsThePromptableTools_AndExcludesTheIrreversibleOnes()
    {
        Assert.True(ToolAutonomy.IsSessionGrantOfferable("write_file"));
        Assert.True(ToolAutonomy.IsSessionGrantOfferable("update_todo"));
        Assert.True(ToolAutonomy.IsSessionGrantOfferable("create_todo"));
        Assert.True(ToolAutonomy.IsSessionGrantOfferable("git_commit"));
        Assert.True(ToolAutonomy.IsSessionGrantOfferable("send_email"));

        Assert.False(ToolAutonomy.IsSessionGrantOfferable("delete_file"));
        Assert.False(ToolAutonomy.IsSessionGrantOfferable("forget"));
        Assert.False(ToolAutonomy.IsSessionGrantOfferable("purge_index"));
        Assert.False(ToolAutonomy.IsSessionGrantOfferable("git_switch"));
        Assert.False(ToolAutonomy.IsSessionGrantOfferable("git_restore"));
        Assert.False(ToolAutonomy.IsSessionGrantOfferable("git_stash"));
        // Case-insensitive, like IsDeleteLike — a differently-cased route must not slip the exclusion.
        Assert.False(ToolAutonomy.IsSessionGrantOfferable("GIT_STASH"));

        // The tier is deliberately WIDER than the standing one for a built-in: that is the gap #15 closes.
        Assert.False(ToolAutonomy.IsStandingGrantOfferable(ToolClass.Files, "write_file", isAllowlisted: false));
        Assert.True(ToolAutonomy.IsSessionGrantOfferable("write_file"));
    }

    /// <summary>
    /// REVIEW FIX (#15). A tool whose ARGUMENTS ARE A GRANT LIST is not session-grantable. The first two
    /// exclusions could not see it: <c>create_scheduled_research</c> is neither delete-like nor
    /// work-discarding, so one click on "Allow this session" minted a grant that authorized every later
    /// job-authoring call in the process with no card — and that argument may name <c>delete_file</c>, which
    /// the unattended gate runs as a named grant because the FLOOR is external-only.
    /// <para>
    /// The last two assertions are the PREMISE, not decoration: they are what makes this an escalation rather
    /// than a style rule. Delete them and the fact still passes while no longer saying why it matters.
    /// </para>
    /// <para>Neutralize: drop <c>&amp;&amp; !ToolPermissionService.IsAuthorityAuthoring(toolName)</c> from
    /// <c>IsSessionGrantOfferable</c> → red.</para>
    /// </summary>
    [Fact]
    public void AToolWhoseArgumentsAreAGrantList_IsNeverSessionGrantable()
    {
        Assert.False(ToolAutonomy.IsSessionGrantOfferable("create_scheduled_research"));
        Assert.False(ToolAutonomy.IsSessionGrantOfferable("update_scheduled_research"));
        // Case-insensitive, like the other two exclusions.
        Assert.False(ToolAutonomy.IsSessionGrantOfferable("CREATE_SCHEDULED_RESEARCH"));
        // Its sibling IS delete-like and was already excluded; and reading the schedule stays grantable, so
        // the exclusion is about AUTHORING authority, not about the scheduling plugin.
        Assert.False(ToolAutonomy.IsSessionGrantOfferable("delete_scheduled_research"));
        Assert.True(ToolAutonomy.IsSessionGrantOfferable("query_scheduled_research"));

        // …and the gate honours nothing it would not offer: a forged card cannot make the tier authorize it.
        var forged = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Interactive, "create_scheduled_research", ToolClass.Scheduling, sessionGrant: true));
        Assert.Equal(ToolGateOutcome.Prompt, forged.Outcome);

        // THE PREMISE. The persisted tier was never on offer for this tool, so before the fix the SESSION tier
        // was strictly wider than the permanent one …
        Assert.False(ToolAutonomy.IsStandingGrantOfferable(ToolClass.Scheduling, "create_scheduled_research", false));
        // … and what it authorized authoring is real: delete_file survives that tool's create-time grant filter
        // (IsPresumedExternalDeleteLike spares our own destructive names on purpose), reaches the run as a
        // NAMED grant, and the floor above is External-only, so it auto-runs unattended.
        Assert.False(ToolPermissionService.IsPresumedExternalDeleteLike("delete_file"));
        Assert.Equal(
            ToolGateDecision.GrantedByName,
            ToolAutonomy.Resolve(Input(
                ToolGateSurface.Unattended, "delete_file", ToolClass.Files, namedGrant: true, canPark: true)).Decision);
    }

    /// <summary>
    /// REVIEW FIX (#15). UNATTENDED, the session tier is honoured only for the classes the PARK would have been
    /// willing to raise a question about. Before the fix the arm had no External exclusion and no surface
    /// condition, so <c>Resolve(Unattended, "send_email", External, sessionGrant: true)</c> returned AutoRun
    /// where the identical input without the grant returned Refuse — it did not even reach the park, whose own
    /// doc refuses External on the grounds that a server-defined name behind one unlabelled button is WEAKER
    /// evidence of consent than the grant list it already rejects.
    /// <para>
    /// The interactive leg is asserted in the same fact deliberately: the fix must narrow the UNATTENDED
    /// surface only. A human looking at a card may still grant a non-destructive MCP tool for the session, and
    /// the standing tier already offers exactly that — narrowing both would undo #15's stated purpose.
    /// </para>
    /// <para>Neutralize: drop
    /// <c>&amp;&amp; (input.Surface != ToolGateSurface.Unattended || input.ToolClass != ToolClass.External)</c>
    /// from the session arm → the Refuse assertions red.</para>
    /// </summary>
    [Fact]
    public void UnattendedSessionGrant_DoesNotReachAnExternalWrite_ButInteractiveStillDoes()
    {
        // The exact input the review found auto-running: not delete-like, so nothing above the arm stops it.
        Assert.False(ToolPermissionService.IsDeleteLike("send_email"));
        Assert.True(ToolAutonomy.IsSessionGrantOfferable("send_email"));

        var unattended = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "send_email", ToolClass.External, canPark: true, sessionGrant: true));
        Assert.Equal(ToolGateOutcome.Refuse, unattended.Outcome);
        Assert.Equal(ToolGateDecision.DeniedNotGranted, unattended.Decision);
        // …the SAME answer the ungranted call gets. The grant buys nothing here, which is the whole fix.
        var ungranted = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "send_email", ToolClass.External, canPark: true));
        Assert.Equal(ungranted, unattended);

        // Nor may it park, so the fix does not smuggle the question back in through the arm below.
        Assert.NotEqual(ToolGateOutcome.Park, unattended.Outcome);

        // INTERACTIVELY it is still honoured — the narrowing is unattended-only.
        var interactive = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Interactive, "send_email", ToolClass.External, sessionGrant: true));
        Assert.Equal(ToolGateOutcome.AutoRun, interactive.Outcome);
        Assert.Equal(ToolGateDecision.AutoApprovedSessionGrant, interactive.Decision);

        // …and an unattended NON-external tool is untouched: T-SESS's headline still holds.
        var files = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "write_file", ToolClass.Files, canPark: true, sessionGrant: true));
        Assert.Equal(ToolGateDecision.AutoApprovedSessionGrant, files.Decision);
    }

    /// <summary>T-FLOOR-2: this batch does not tighten the path where a human is looking at the card.</summary>
    [Fact]
    public void InteractiveSurface_NeverRefuses()
    {
        var violations = new List<string>();

        foreach (var toolClass in AllClasses)
        foreach (var name in DeleteLikeNames.Concat(new[] { "write_file", "create_todo", "update_todo" }))
        foreach (var policy in new RunAutonomyPolicy?[] { null, EveryClassPolicy })
        foreach (var granted in new[] { false, true })
        foreach (var allowlisted in new[] { false, true })
        {
            var verdict = ToolAutonomy.Resolve(Input(
                ToolGateSurface.Interactive, name, toolClass, policy,
                allowlisted: allowlisted, standingGrant: granted, namedGrant: false));

            if (verdict.Outcome == ToolGateOutcome.Refuse)
                violations.Add($"{toolClass}/{name}/granted={granted}/allowlisted={allowlisted}");
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// T-FLOOR-3, D6's exact boundary: a POLICY class grant never covers a delete-like tool even when the
    /// class is covered and the tool is a BUILT-IN — but an explicit NAMED grant still runs it, because that
    /// is the user's own auditable decision and the pre-batch behaviour.
    /// </summary>
    [Fact]
    public void PolicyNeverCoversADeleteLikeTool_EvenABuiltInOne()
    {
        var filesPolicy = new RunAutonomyPolicy([ToolClass.Files]);

        var byPolicy = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "delete_file", ToolClass.Files, filesPolicy));
        Assert.NotEqual(ToolGateOutcome.AutoRun, byPolicy.Outcome);
        Assert.Equal(ToolGateDecision.DeniedNotGranted, byPolicy.Decision);

        var byName = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "delete_file", ToolClass.Files, filesPolicy, namedGrant: true));
        Assert.Equal(ToolGateOutcome.AutoRun, byName.Outcome);
        Assert.Equal(ToolGateDecision.GrantedByName, byName.Decision);

        // …and the non-delete sibling in the same class DOES run on the policy alone.
        var sibling = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "write_file", ToolClass.Files, filesPolicy));
        Assert.Equal(ToolGateOutcome.AutoRun, sibling.Outcome);
        Assert.Equal(ToolGateDecision.AutoApprovedPolicy, sibling.Decision);
    }

    /// <summary>T-FLOOR-4: an unrecognised class name in a persisted document must never become authority.</summary>
    [Fact]
    public void UnknownClass_IsNeverCovered()
    {
        var policy = new RunAutonomyPolicy([ToolClass.Unknown]);
        Assert.False(policy.Covers(ToolClass.Unknown));

        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "write_file", ToolClass.Unknown, policy));
        Assert.NotEqual(ToolGateOutcome.AutoRun, verdict.Outcome);
    }

    [Fact]
    public void Interactive_OfferableAndGranted_AutoRuns_WithStandingGrantDecision()
    {
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Interactive, "create_todo", ToolClass.Todo,
            allowlisted: true, standingGrant: true));

        Assert.Equal(ToolGateOutcome.AutoRun, verdict.Outcome);
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, verdict.Decision);
    }

    [Fact]
    public void Interactive_AllowlistedButUngranted_StillPrompts()
    {
        // Today's semantics: the allowlist alone does not auto-run interactively — it only makes the tool
        // grantable, so the first call still shows a card with an "Always allow" button.
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Interactive, "create_todo", ToolClass.Todo, allowlisted: true));

        Assert.Equal(ToolGateOutcome.Prompt, verdict.Outcome);
    }

    [Fact]
    public void Interactive_PolicyCoveredClass_AutoRuns_WithoutAnyGrant()
    {
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Interactive, "create_todo", ToolClass.Todo, new RunAutonomyPolicy([ToolClass.Todo])));

        Assert.Equal(ToolGateOutcome.AutoRun, verdict.Outcome);
        Assert.Equal(ToolGateDecision.AutoApprovedPolicy, verdict.Decision);
    }

    [Fact]
    public void Unattended_Ungranted_Refuses_WithNotGrantedDecision()
    {
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "write_file", ToolClass.Files));

        Assert.Equal(ToolGateOutcome.Refuse, verdict.Outcome);
        Assert.Equal(ToolGateDecision.DeniedNotGranted, verdict.Decision);
    }

    [Fact]
    public void Unattended_NamedGrant_AutoRuns_WithGrantedByNameDecision()
    {
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "write_file", ToolClass.Files, namedGrant: true));

        Assert.Equal(ToolGateOutcome.AutoRun, verdict.Outcome);
        Assert.Equal(ToolGateDecision.GrantedByName, verdict.Decision);
    }

    [Fact]
    public void Unattended_DestructiveExternal_RefusesWithTheFloorDecision_EvenWhenNamed()
    {
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "delete_issue", ToolClass.External, EveryClassPolicy, namedGrant: true));

        Assert.Equal(ToolGateOutcome.Refuse, verdict.Outcome);
        Assert.Equal(ToolGateDecision.DeniedDestructiveFloor, verdict.Decision);
    }

    [Fact]
    public void Unattended_TheAllowlistIsNotHonoured()
    {
        // §0.3: IToolPermissionService is injected nowhere in the headless files, so the unattended gate
        // passes IsAllowlisted: false. Pinned here so a future "tidy-up" that starts passing it true is a
        // deliberate behaviour change with a red test, not a silent widening of four tools on every job.
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "create_todo", ToolClass.Todo, allowlisted: true));

        Assert.Equal(ToolGateOutcome.Refuse, verdict.Outcome);
    }

    [Fact]
    public void Voice_AllowlistedTool_AutoRuns_WithTheAllowlistDecision()
    {
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Voice, "create_todo", ToolClass.Todo, allowlisted: true));

        Assert.Equal(ToolGateOutcome.AutoRun, verdict.Outcome);
        Assert.Equal(ToolGateDecision.AutoApprovedAllowlist, verdict.Decision);
    }

    /// <summary>
    /// The voice allowlist is BUILT-INS only. <c>IsAutoApproveEligible</c> is a name-only set and
    /// <c>PluginService</c>'s tool-name routes are last-wins with no collision detection (§13.4), so an MCP
    /// server can own the route for a name in the allowlist. Voice has no card and no transcript entry, so
    /// name-only authority there would hand a third-party server the user's spoken content with no consent.
    /// </summary>
    [Theory]
    [InlineData("create_todo")]
    [InlineData("create_object")]
    [InlineData("create_reminder")]
    [InlineData("append_to_list")]
    public void Voice_AnExternalToolShadowingAnAllowlistedName_DoesNotAutoRun(string toolName)
    {
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Voice, toolName, ToolClass.External, allowlisted: true));

        Assert.Equal(ToolGateOutcome.Refuse, verdict.Outcome);
        Assert.Equal(ToolGateDecision.DeniedNotGranted, verdict.Decision);

        // Same name, built-in route ⇒ still authorized. The discriminator is the CLASS, not the name.
        var builtIn = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Voice, toolName, ToolClass.Todo, allowlisted: true));
        Assert.Equal(ToolGateOutcome.AutoRun, builtIn.Outcome);
    }

    [Fact]
    public void Voice_UngrantedWrite_Refuses()
    {
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Voice, "write_file", ToolClass.Files));

        Assert.Equal(ToolGateOutcome.Refuse, verdict.Outcome);
        Assert.Equal(ToolGateDecision.DeniedNotGranted, verdict.Decision);
    }

    [Fact]
    public void Voice_StandingGrantIsHonoured()
    {
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Voice, "notion_create_page", ToolClass.External, standingGrant: true));

        Assert.Equal(ToolGateOutcome.AutoRun, verdict.Outcome);
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, verdict.Decision);
    }

    [Theory]
    [InlineData("DELETE_thing")]
    [InlineData("Notion_DeletePage")]
    [InlineData("FORGET")]
    public void Resolve_IsCaseInsensitiveOnTheDeleteLikeName(string toolName)
    {
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, toolName, ToolClass.External, EveryClassPolicy, namedGrant: true));

        Assert.Equal(ToolGateOutcome.Refuse, verdict.Outcome);
        Assert.Equal(ToolGateDecision.DeniedDestructiveFloor, verdict.Decision);
    }

    /// <summary>
    /// T-OFF-1: the executable form of "the refactor changed no semantics". The historic interactive
    /// expression was <c>IsAutoApproveEligible(t) || (IsMcpTool(t) &amp;&amp; !IsDeleteLike(t))</c>.
    /// </summary>
    [Fact]
    public void IsStandingGrantOfferable_MatchesTheHistoricEligibleExpression()
    {
        // Allowlisted → offerable in every class.
        foreach (var toolClass in AllClasses)
            Assert.True(ToolAutonomy.IsStandingGrantOfferable(toolClass, "create_todo", isAllowlisted: true));

        // External and non-destructive → offerable (an external tool is a named capability the user may
        // choose to always allow), even though it is not allowlisted.
        Assert.True(ToolAutonomy.IsStandingGrantOfferable(ToolClass.External, "notion_create_page", false));

        // External and destructive → never offerable.
        Assert.False(ToolAutonomy.IsStandingGrantOfferable(ToolClass.External, "notion_delete_page", false));

        // Every other class, not allowlisted → the allowlist's answer, i.e. false. write_file and the git
        // tools are the load-bearing cases (GitActionCardTests pins that git is not auto-approve-eligible).
        foreach (var toolClass in AllClasses.Where(c => c != ToolClass.External))
        {
            Assert.False(ToolAutonomy.IsStandingGrantOfferable(toolClass, "write_file", false));
            Assert.False(ToolAutonomy.IsStandingGrantOfferable(toolClass, "git_switch", false));
            Assert.False(ToolAutonomy.IsStandingGrantOfferable(toolClass, "delete_file", false));
        }
    }
}

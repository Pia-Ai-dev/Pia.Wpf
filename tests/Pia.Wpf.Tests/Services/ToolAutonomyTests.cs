using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

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
        bool sessionGrant = false,
        bool serverDeclaredDestructive = false,
        bool namedDenial = false)
        => new(surface, toolName, toolClass, serverDeclaredDestructive, allowlisted, sessionGrant, standingGrant,
               namedGrant, namedDenial, policy, canPark);

    /// <summary>Nested loops in one Fact, not a ~3.5k-case Theory, to keep the cross product out of the suite total.</summary>
    [Fact]
    public void DeleteLikeTool_OnlyEverAutoRunsOnAnExplicitGrant_AcrossTheEntirePolicySpace()
    {
        var violations = new List<string>();
        var destructiveExternalRuns = 0;

        foreach (var surface in AllSurfaces)
        foreach (var toolClass in AllClasses)
        foreach (var name in DeleteLikeNames)
        foreach (var policy in new RunAutonomyPolicy?[] { null, new RunAutonomyPolicy([toolClass]), EveryClassPolicy })
        foreach (var granted in new[] { false, true })
        foreach (var allowlisted in new[] { false, true })
        foreach (var named in new[] { false, true })
        foreach (var canPark in new[] { false, true })
        foreach (var sessionGrant in new[] { false, true })
        foreach (var namedDenial in new[] { false, true })
        {
            var verdict = ToolAutonomy.Resolve(Input(
                surface, name, toolClass, policy,
                allowlisted: allowlisted, standingGrant: granted, namedGrant: named, canPark: canPark,
                sessionGrant: sessionGrant, namedDenial: namedDenial));

            // A policy may never be the reason a delete-like tool ran; a grant the user typed still may.
            var policyBroken = verdict.Decision == ToolGateDecision.AutoApprovedPolicy;

            // A park puts an irreversible action behind a Continue button that shows no arguments.
            var parkBroken = verdict.Outcome == ToolGateOutcome.Park;

            // A session grant is NOT a violation: it is a grant the user typed on a card, and the tier now
            // admits every tool. What must never authorize a delete is a policy, a park, or a denied name.

            // The denial tier is a REFUSE-only arm: a declined tool never auto-runs, whatever else is granted.
            var denialBroken = namedDenial && verdict.Outcome == ToolGateOutcome.AutoRun;

            if (policyBroken || parkBroken || denialBroken)
            {
                violations.Add(
                    $"{surface}/{toolClass}/{name}/policy={(policy is null ? "none" : string.Join('+', policy.AutoApproveClasses))}"
                    + $"/granted={granted}/allowlisted={allowlisted}/named={named}/canPark={canPark}"
                    + $"/session={sessionGrant}/denied={namedDenial}"
                    + $" => {verdict.Outcome} {verdict.Decision}");
            }

            if (toolClass == ToolClass.External && verdict.Outcome == ToolGateOutcome.AutoRun)
                destructiveExternalRuns++;
        }

        Assert.Empty(violations);
        // Non-vacuity, and the owner's decision restated: a destructive MCP tool DOES auto-run once granted.
        Assert.True(destructiveExternalRuns > 0);
    }

    [Fact]
    public void NamedDenial_Refuses_AheadOfPolicyGrantsAndPark()
    {
        var denied = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "git_commit", ToolClass.Git, EveryClassPolicy,
            namedGrant: true, canPark: true, namedDenial: true));

        Assert.Equal(ToolGateOutcome.Refuse, denied.Outcome);
        Assert.Equal(ToolGateDecision.DeniedForRun, denied.Decision);

        var granted = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "git_commit", ToolClass.Git, EveryClassPolicy,
            namedGrant: true, canPark: true));

        Assert.Equal(ToolGateOutcome.AutoRun, granted.Outcome);
    }

    [Fact]
    public void OnlyTheUnattendedSurfaceEverParks_EvenWhenTheCallerPermitsIt()
    {
        var violations = new List<string>();
        var unattendedParks = 0;

        // Promptable names only — a delete-like name never parks anywhere.
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

    // ------------------------------------------------- THE SESSION TIER, at the resolver

    /// <summary>The tier covers these names too now. The only two exceptions are the arm's OWN surface pins —
    /// voice (no card, no transcript) and an unattended EXTERNAL call (server-defined name, unseen arguments) —
    /// so this is an equivalence, not a one-way check: a pin that stopped holding fails it as loudly.</summary>
    [Fact]
    public void SessionGrant_CoversADeleteLikeOrWorkDiscardingTool_ExceptWhereTheArmPinsTheSurface()
    {
        var violations = new List<string>();

        foreach (var surface in AllSurfaces)
        foreach (var toolClass in AllClasses)
        foreach (var name in DeleteLikeNames.Concat(new[] { "git_switch", "git_restore", "git_stash" }))
        foreach (var canPark in new[] { false, true })
        {
            var verdict = ToolAutonomy.Resolve(Input(
                surface, name, toolClass, policy: null, sessionGrant: true, canPark: canPark));

            var pinned = surface == ToolGateSurface.Voice
                         || (surface == ToolGateSurface.Unattended && toolClass == ToolClass.External);

            if ((verdict.Decision == ToolGateDecision.AutoApprovedSessionGrant) == pinned)
                violations.Add($"{surface}/{toolClass}/{name}/canPark={canPark} => {verdict.Outcome} {verdict.Decision}");
        }

        Assert.Empty(violations);

        // Non-vacuity control: the same grant on a promptable name does authorize.
        foreach (var surface in new[] { ToolGateSurface.Interactive, ToolGateSurface.Unattended })
        {
            var honoured = ToolAutonomy.Resolve(Input(surface, "write_file", ToolClass.Files, sessionGrant: true));
            Assert.Equal(ToolGateOutcome.AutoRun, honoured.Outcome);
            Assert.Equal(ToolGateDecision.AutoApprovedSessionGrant, honoured.Decision);
        }
    }

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

        // Control: voice still honours the standing grant.
        var standing = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Voice, "some_mcp_action", ToolClass.External, standingGrant: true));
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, standing.Decision);
    }

    [Fact]
    public void SessionGrant_OutranksTheParkAndTheNamedGrant()
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

        // …but an UNATTENDED external tool is still refused, on the arm's own Unattended-and-External clause.
        var destructive = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "delete_issue", ToolClass.External, sessionGrant: true, canPark: true));
        Assert.Equal(ToolGateOutcome.Refuse, destructive.Outcome);
        Assert.Equal(ToolGateDecision.DeniedNotGranted, destructive.Decision);

        // The standing tier keeps its own decision when it is the only authority (no silent re-labelling).
        var standing = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Interactive, "create_todo", ToolClass.Todo, allowlisted: true, standingGrant: true));
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, standing.Decision);
    }

    /// <summary>
    /// The session tier admits every tool now — the delete-like, work-discarding and authority-authoring names
    /// it used to exclude included. It is strictly WEAKER than the standing tier, which was already on offer for
    /// all of them, so withholding it only pushed a user toward the durable grant; the Tool access row carries a
    /// caution on a ticked tool instead.
    /// </summary>
    [Theory]
    [InlineData("write_file", ToolClass.Files)]
    [InlineData("delete_file", ToolClass.Files)]
    [InlineData("forget", ToolClass.Memory)]
    [InlineData("purge_index", ToolClass.Memory)]
    [InlineData("git_switch", ToolClass.Git)]
    [InlineData("git_restore", ToolClass.Git)]
    // Case-insensitive, like every other name test here.
    [InlineData("GIT_STASH", ToolClass.Git)]
    [InlineData("create_scheduled_research", ToolClass.Scheduling)]
    [InlineData("update_scheduled_research", ToolClass.Scheduling)]
    public void SessionGrant_IsHonouredForEveryTool_Interactively(string toolName, ToolClass toolClass)
    {
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Interactive, toolName, toolClass, sessionGrant: true));

        Assert.Equal(ToolGateOutcome.AutoRun, verdict.Outcome);
        Assert.Equal(ToolGateDecision.AutoApprovedSessionGrant, verdict.Decision);
    }

    /// <summary>The consequence of opening the tier up, stated rather than left to be discovered: it reaches a
    /// ROOT unattended step, so a session grant runs a delete-like tool there with nobody watching.</summary>
    [Fact]
    public void SessionGrant_ReachesADeleteLikeTool_OnARootUnattendedRun()
    {
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "delete_file", ToolClass.Files, sessionGrant: true, canPark: true));
        Assert.Equal(ToolGateDecision.AutoApprovedSessionGrant, verdict.Decision);

        // A server's own destructive declaration does not veto it either.
        var declared = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Interactive, "notion_archive_page", ToolClass.External,
            sessionGrant: true, serverDeclaredDestructive: true));
        Assert.Equal(ToolGateDecision.AutoApprovedSessionGrant, declared.Decision);
    }

    [Fact]
    public void ANamedGrantOnOurOwnDeleteTool_StillAutoRunsUnattended()
    {
        // IsPresumedExternalDeleteLike spares our own destructive names on purpose, so delete_file survives a
        // create-time grant filter and the named-grant arm then lets it auto-run unattended.
        Assert.False(ToolPermissionService.IsPresumedExternalDeleteLike("delete_file"));
        Assert.Equal(
            ToolGateDecision.GrantedByName,
            ToolAutonomy.Resolve(Input(
                ToolGateSurface.Unattended, "delete_file", ToolClass.Files, namedGrant: true, canPark: true)).Decision);
    }

    [Fact]
    public void UnattendedSessionGrant_DoesNotReachAnExternalWrite_ButInteractiveStillDoes()
    {
        // Not delete-like, so nothing above the session arm stops it.
        Assert.False(ToolPermissionService.IsDeleteLike("send_email"));

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

        // …and an unattended NON-external tool is untouched.
        var files = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "write_file", ToolClass.Files, canPark: true, sessionGrant: true));
        Assert.Equal(ToolGateDecision.AutoApprovedSessionGrant, files.Decision);
    }

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

    /// <summary>An explicit NAMED grant still runs a built-in delete — that is the user's own auditable decision.</summary>
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

    /// <summary>An unrecognised class name in a persisted document must never become authority.</summary>
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
        // The allowlist alone does not auto-run interactively; it only makes the tool grantable.
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

    /// <summary>The sharpest consequence of removing the floor, pinned so it can never be an accident.</summary>
    [Fact]
    public void Unattended_DestructiveExternal_AutoRunsWhenItsGrantListNamesIt()
    {
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "delete_issue", ToolClass.External, EveryClassPolicy, namedGrant: true));

        Assert.Equal(ToolGateOutcome.AutoRun, verdict.Outcome);
        Assert.Equal(ToolGateDecision.GrantedByName, verdict.Decision);

        // Unnamed, it is still a hard denial: the grant list is what authorizes it, not the policy or the park.
        var unnamed = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "delete_issue", ToolClass.External, EveryClassPolicy, canPark: true));
        Assert.Equal(ToolGateOutcome.Refuse, unnamed.Outcome);
        Assert.Equal(ToolGateDecision.DeniedNotGranted, unnamed.Decision);
    }

    /// <summary>An "Always" grant reaches every surface — chat, voice and headless alike.</summary>
    [Fact]
    public void StandingGrant_AutoRunsADestructiveTool_OnEverySurfaceThatReadsOne()
    {
        foreach (var surface in new[]
                 { ToolGateSurface.Interactive, ToolGateSurface.Voice, ToolGateSurface.Unattended })
        foreach (var (name, toolClass) in new[]
                 {
                     ("delete_file", ToolClass.Files),
                     ("forget", ToolClass.Memory),
                     ("git_stash", ToolClass.Git),
                     ("delete_issue", ToolClass.External),
                 })
        {
            var verdict = ToolAutonomy.Resolve(Input(surface, name, toolClass, standingGrant: true));

            Assert.Equal(ToolGateOutcome.AutoRun, verdict.Outcome);
            Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, verdict.Decision);
        }

        // A server that declares its own tool destructive cannot veto the grant either.
        var declared = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Interactive, "notion_archive_page", ToolClass.External,
            standingGrant: true, serverDeclaredDestructive: true));
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, declared.Decision);
    }

    [Fact]
    public void Unattended_TheAllowlistIsNotHonoured()
    {
        // The unattended gate passes IsAllowlisted: false by choice; the arm below is pinned to Voice anyway.
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

    /// <summary>Plugin tool-name routes are last-wins with no collision detection, so an MCP server can own the route for an allowlisted name.</summary>
    [Theory]
    [InlineData("create_todo")]
    [InlineData("create_reminder")]
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

    /// <summary>Delete-likeness still gates the policy arm and the park, and it is still case-insensitive.</summary>
    [Theory]
    [InlineData("DELETE_thing")]
    [InlineData("Notion_DeletePage")]
    [InlineData("FORGET")]
    public void Resolve_IsCaseInsensitiveOnTheDeleteLikeName(string toolName)
    {
        var verdict = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, toolName, ToolClass.Files, EveryClassPolicy, canPark: true));

        Assert.Equal(ToolGateOutcome.Refuse, verdict.Outcome);
        Assert.Equal(ToolGateDecision.DeniedNotGranted, verdict.Decision);

        // The lower-cased sibling in the same class rides the policy, so the loop above is not vacuous.
        var benign = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "write_file", ToolClass.Files, EveryClassPolicy, canPark: true));
        Assert.Equal(ToolGateDecision.AutoApprovedPolicy, benign.Decision);
    }

    /// <summary>Replaces the old offerability rule: EVERY tool of EVERY class is standing-grantable now.</summary>
    [Fact]
    public void StandingGrant_IsHonouredForEveryClass_WithNoOfferabilityTest()
    {
        foreach (var toolClass in AllClasses)
        foreach (var name in DeleteLikeNames.Concat(
                     new[] { "write_file", "git_switch", "create_todo", "create_scheduled_research" }))
        foreach (var allowlisted in new[] { false, true })
        {
            var verdict = ToolAutonomy.Resolve(Input(
                ToolGateSurface.Interactive, name, toolClass, allowlisted: allowlisted, standingGrant: true));

            Assert.Equal(ToolGateOutcome.AutoRun, verdict.Outcome);
            Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, verdict.Decision);
        }

        // The one thing still above it: a per-run denial a human typed.
        var denied = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "write_file", ToolClass.Files, standingGrant: true, namedDenial: true));
        Assert.Equal(ToolGateDecision.DeniedForRun, denied.Decision);
    }
}

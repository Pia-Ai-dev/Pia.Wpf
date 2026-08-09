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
        foreach (var canPark in new[] { false, true })
        foreach (var sessionGrant in new[] { false, true })
        foreach (var namedDenial in new[] { false, true })
        {
            var verdict = ToolAutonomy.Resolve(Input(
                surface, name, toolClass, policy,
                allowlisted: allowlisted, standingGrant: granted, namedGrant: named, canPark: canPark,
                sessionGrant: sessionGrant, namedDenial: namedDenial));

            var floorBroken = toolClass == ToolClass.External && verdict.Outcome == ToolGateOutcome.AutoRun;

            // A policy may never be the reason a delete-like tool ran; a named grant for a built-in still may.
            var policyBroken = verdict.Decision == ToolGateDecision.AutoApprovedPolicy;

            // Wider than the floor on purpose: a park puts an irreversible action behind a Continue button
            // that shows no arguments.
            var parkBroken = verdict.Outcome == ToolGateOutcome.Park;

            // A session grant covers later calls whose arguments the user never sees.
            var sessionBroken = verdict.Decision == ToolGateDecision.AutoApprovedSessionGrant;

            // The denial tier is a REFUSE-only arm: a declined tool never auto-runs, whatever else is granted.
            var denialBroken = namedDenial && verdict.Outcome == ToolGateOutcome.AutoRun;

            if (floorBroken || policyBroken || parkBroken || sessionBroken || denialBroken)
            {
                violations.Add(
                    $"{surface}/{toolClass}/{name}/policy={(policy is null ? "none" : string.Join('+', policy.AutoApproveClasses))}"
                    + $"/granted={granted}/allowlisted={allowlisted}/named={named}/canPark={canPark}"
                    + $"/session={sessionGrant}/denied={namedDenial}"
                    + $" => {verdict.Outcome} {verdict.Decision}");
            }
        }

        Assert.Empty(violations);
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

        var floor = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Unattended, "delete_issue", ToolClass.External, sessionGrant: true, canPark: true));
        Assert.Equal(ToolGateOutcome.Refuse, floor.Outcome);
        Assert.Equal(ToolGateDecision.DeniedDestructiveFloor, floor.Decision);

        // The standing tier keeps its own decision when it is the only authority (no silent re-labelling).
        var standing = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Interactive, "create_todo", ToolClass.Todo, allowlisted: true, standingGrant: true));
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, standing.Decision);
    }

    /// <summary>The offerability rule is name-only, unlike the standing rule which collapses to the allowlist off External.</summary>
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

        // The tier is deliberately WIDER than the standing one for a built-in.
        Assert.False(ToolAutonomy.IsStandingGrantOfferable(ToolClass.Files, "write_file", isAllowlisted: false));
        Assert.True(ToolAutonomy.IsSessionGrantOfferable("write_file"));
    }

    [Fact]
    public void AToolWhoseArgumentsAreAGrantList_IsNeverSessionGrantable()
    {
        Assert.False(ToolAutonomy.IsSessionGrantOfferable("create_scheduled_research"));
        Assert.False(ToolAutonomy.IsSessionGrantOfferable("update_scheduled_research"));
        // Case-insensitive, like the other two exclusions.
        Assert.False(ToolAutonomy.IsSessionGrantOfferable("CREATE_SCHEDULED_RESEARCH"));
        // The exclusion is about AUTHORING authority, not about the scheduling plugin.
        Assert.False(ToolAutonomy.IsSessionGrantOfferable("delete_scheduled_research"));
        Assert.True(ToolAutonomy.IsSessionGrantOfferable("query_scheduled_research"));

        // …and the gate honours nothing it would not offer: a forged card cannot make the tier authorize it.
        var forged = ToolAutonomy.Resolve(Input(
            ToolGateSurface.Interactive, "create_scheduled_research", ToolClass.Scheduling, sessionGrant: true));
        Assert.Equal(ToolGateOutcome.Prompt, forged.Outcome);

        Assert.False(ToolAutonomy.IsStandingGrantOfferable(ToolClass.Scheduling, "create_scheduled_research", false));
        // IsPresumedExternalDeleteLike spares our own destructive names on purpose, so delete_file survives a
        // create-time grant filter and the External-only floor then lets it auto-run unattended.
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
        // IToolPermissionService is injected nowhere headless, so the unattended gate passes IsAllowlisted: false.
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

    /// <summary>The historic expression was <c>IsAutoApproveEligible(t) || (IsMcpTool(t) &amp;&amp; !IsDeleteLike(t))</c>.</summary>
    [Fact]
    public void IsStandingGrantOfferable_MatchesTheHistoricEligibleExpression()
    {
        // Allowlisted → offerable in every class.
        foreach (var toolClass in AllClasses)
            Assert.True(ToolAutonomy.IsStandingGrantOfferable(toolClass, "create_todo", isAllowlisted: true));

        // External and non-destructive → offerable even though it is not allowlisted.
        Assert.True(ToolAutonomy.IsStandingGrantOfferable(ToolClass.External, "notion_create_page", false));

        // External and destructive → never offerable.
        Assert.False(ToolAutonomy.IsStandingGrantOfferable(ToolClass.External, "notion_delete_page", false));

        // Every other class, not allowlisted → the allowlist's answer, i.e. false.
        foreach (var toolClass in AllClasses.Where(c => c != ToolClass.External))
        {
            Assert.False(ToolAutonomy.IsStandingGrantOfferable(toolClass, "write_file", false));
            Assert.False(ToolAutonomy.IsStandingGrantOfferable(toolClass, "git_switch", false));
            Assert.False(ToolAutonomy.IsStandingGrantOfferable(toolClass, "delete_file", false));
        }
    }
}

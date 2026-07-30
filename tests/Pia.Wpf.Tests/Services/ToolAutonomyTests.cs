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
        bool namedGrant = false)
        => new(surface, toolName, toolClass, allowlisted, standingGrant, namedGrant, policy);

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
        {
            var verdict = ToolAutonomy.Resolve(Input(
                surface, name, toolClass, policy,
                allowlisted: allowlisted, standingGrant: granted, namedGrant: named));

            // The M3 FLOOR: a delete-like EXTERNAL tool never auto-runs, whatever the policy says and
            // however it was granted.
            var floorBroken = toolClass == ToolClass.External && verdict.Outcome == ToolGateOutcome.AutoRun;

            // D6, which is strictly stronger and applies to EVERY class: a POLICY may never be the reason a
            // delete-like tool ran. (A NAMED grant for a built-in delete still may — that is the user's own
            // auditable decision, pinned by PolicyNeverCoversADeleteLikeTool_EvenABuiltInOne.)
            var policyBroken = verdict.Decision == ToolGateDecision.AutoApprovedPolicy;

            if (floorBroken || policyBroken)
            {
                violations.Add(
                    $"{surface}/{toolClass}/{name}/policy={(policy is null ? "none" : string.Join('+', policy.AutoApproveClasses))}"
                    + $"/granted={granted}/allowlisted={allowlisted}/named={named} => {verdict.Outcome} {verdict.Decision}");
            }
        }

        Assert.Empty(violations);
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

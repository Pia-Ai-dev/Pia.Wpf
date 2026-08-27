using ModelContextProtocol.Protocol;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// A server's <c>ToolAnnotations</c> are honoured only in the more-restricted direction, and an unspecified
/// <c>destructiveHint</c> deliberately means "no hint" rather than the spec's "assume true".
/// </summary>
public class McpToolAnnotationHintTests
{
    private const string BenignExternalTool = "send_email";

    private static ToolGateInput ExternalCall(
        ToolGateSurface surface,
        bool serverDeclaredDestructive,
        bool namedGrant = false,
        bool standingGrant = false,
        bool sessionGrant = false,
        RunAutonomyPolicy? policy = null,
        bool canPark = false,
        bool topLevelUserRun = false)
        => new(surface, BenignExternalTool, ToolClass.External, serverDeclaredDestructive,
               IsAllowlisted: false, HasSessionGrant: sessionGrant, HasStandingGrant: standingGrant,
               IsNamedGrant: namedGrant, HasNamedDenial: false, Policy: policy, CanPark: canPark,
               IsTopLevelUserRun: topLevelUserRun);

    // ---- the extraction: which annotation shapes count ------------------------------------------------

    [Fact]
    public void NoAnnotationsAtAll_IsNoHint()
    {
        Assert.False(McpPluginToolHandler.IsServerDeclaredDestructive(null));
    }

    [Fact]
    public void AnExplicitDestructiveHint_Counts()
    {
        Assert.True(McpPluginToolHandler.IsServerDeclaredDestructive(
            new ToolAnnotations { DestructiveHint = true }));
    }

    [Fact]
    public void AnUnspecifiedDestructiveHint_IsNoHint_EvenWhenTheToolSaysItWrites()
    {
        Assert.False(McpPluginToolHandler.IsServerDeclaredDestructive(
            new ToolAnnotations { ReadOnlyHint = false }));
        Assert.False(McpPluginToolHandler.IsServerDeclaredDestructive(new ToolAnnotations()));
    }

    [Fact]
    public void AnExplicitlyNonDestructiveHint_ChangesNothing()
    {
        // "destructiveHint: false" is a safety claim, and a safety claim is exactly what may not be honoured.
        Assert.False(McpPluginToolHandler.IsServerDeclaredDestructive(
            new ToolAnnotations { DestructiveHint = false }));
    }

    /// <summary>A contradictory pair resolves to the stricter reading — a buggy server and a hostile one are handled alike.</summary>
    [Fact]
    public void ReadOnlyHint_CannotCancelADestructiveHint()
    {
        Assert.True(McpPluginToolHandler.IsServerDeclaredDestructive(
            new ToolAnnotations { ReadOnlyHint = true, DestructiveHint = true }));
    }

    // ---- the gate: what the declaration actually changes ----------------------------------------------

    /// <summary>A grant list the user authored still runs the tool; the declaration is not a veto over it.</summary>
    [Fact]
    public void Unattended_ADeclaredDestructiveExternalTool_StillRunsOnANamedGrant()
    {
        var after = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Unattended, serverDeclaredDestructive: true, namedGrant: true));

        Assert.Equal(ToolGateOutcome.AutoRun, after.Outcome);
        Assert.Equal(ToolGateDecision.GrantedByName, after.Decision);
    }

    /// <summary>Where the declaration does bite: the class-wide policy switch, which is not a per-tool grant.</summary>
    [Fact]
    public void Unattended_APolicyCoveringTheClass_DoesNotLiftTheDeclaration()
    {
        var policy = new RunAutonomyPolicy([ToolClass.External]);

        var before = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Unattended, serverDeclaredDestructive: false, policy: policy));
        Assert.Equal(ToolGateOutcome.AutoRun, before.Outcome);
        Assert.Equal(ToolGateDecision.AutoApprovedPolicy, before.Decision);

        var after = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Unattended, serverDeclaredDestructive: true, policy: policy));
        Assert.Equal(ToolGateOutcome.Refuse, after.Outcome);
        Assert.Equal(ToolGateDecision.DeniedNotGranted, after.Decision);
    }

    /// <summary>…but NOT the park: it refuses an external tool on class alone, so the hint changes nothing
    /// there and its bite is the policy arm above plus the session tier.</summary>
    [Fact]
    public void Unattended_AnExternalTool_IsNeverParked_HintOrNot()
    {
        foreach (var declared in new[] { false, true })
        {
            var after = ToolAutonomy.Resolve(
                ExternalCall(ToolGateSurface.Unattended, serverDeclaredDestructive: declared, canPark: true));

            Assert.Equal(ToolGateOutcome.Refuse, after.Outcome);
            Assert.Equal(ToolGateDecision.DeniedNotGranted, after.Decision);
        }
    }

    /// <summary>A standing grant is a per-tool decision the user took, so the declaration cannot withdraw it.</summary>
    [Fact]
    public void Interactive_ADeclaredDestructiveExternalTool_StillAutoRunsOnAStandingGrant()
    {
        var after = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Interactive, serverDeclaredDestructive: true, standingGrant: true));

        Assert.Equal(ToolGateOutcome.AutoRun, after.Outcome);
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, after.Decision);
    }

    /// <summary>The declaration no longer withholds the session tier either — a card the user answered is a card
    /// the user answered, and it is the WEAKER of the two grants that card offers.</summary>
    [Fact]
    public void Interactive_ASessionGrant_IsHonouredEvenOnADeclaredDestructiveTool()
    {
        foreach (var declared in new[] { false, true })
        {
            var verdict = ToolAutonomy.Resolve(
                ExternalCall(ToolGateSurface.Interactive, declared, sessionGrant: true));

            Assert.Equal(ToolGateOutcome.AutoRun, verdict.Outcome);
            Assert.Equal(ToolGateDecision.AutoApprovedSessionGrant, verdict.Decision);
        }

        // Non-vacuity: the declaration still narrows where nothing was granted — the card comes back.
        var ungranted = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Interactive, serverDeclaredDestructive: true));
        Assert.Equal(ToolGateOutcome.Prompt, ungranted.Outcome);
    }

    [Fact]
    public void Voice_ADeclaredDestructiveExternalTool_StillAutoRunsOnAStandingGrant()
    {
        var after = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Voice, serverDeclaredDestructive: true, standingGrant: true));

        Assert.Equal(ToolGateOutcome.AutoRun, after.Outcome);
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, after.Decision);

        // Ungranted, voice still refuses it: the declaration only ever narrows, and nothing here authorized it.
        var ungranted = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Voice, serverDeclaredDestructive: true));
        Assert.Equal(ToolGateOutcome.Refuse, ungranted.Outcome);
    }

    /// <summary>A loosening claim reaches the gate as "no hint", so the NAME alone still narrows.</summary>
    [Fact]
    public void TheHintCannotClearADeleteLikeName()
    {
        var verdict = ToolAutonomy.Resolve(new ToolGateInput(
            ToolGateSurface.Unattended, "delete_everything", ToolClass.External,
            ServerDeclaredDestructive: false,
            IsAllowlisted: false, HasSessionGrant: true, HasStandingGrant: false,
            IsNamedGrant: false, HasNamedDenial: false,
            Policy: new RunAutonomyPolicy([ToolClass.External]), CanPark: true, IsTopLevelUserRun: true));

        // Neither the policy, nor the session tier, nor the park will take it.
        Assert.Equal(ToolGateOutcome.Refuse, verdict.Outcome);
        Assert.Equal(ToolGateDecision.DeniedNotGranted, verdict.Decision);
    }

    // ---- the offer rules: the card must not offer what the gate will refuse --------------------------

    /// <summary>The declaration changes how the card LOOKS, not which tiers it offers: neither has an
    /// offerability test left, and the gate honours both either way.</summary>
    [Fact]
    public void NeitherTier_IsWithheldForADeclaredDestructiveTool()
    {
        foreach (var declared in new[] { false, true })
        {
            Assert.Equal(
                ToolGateDecision.AutoApprovedStandingGrant,
                ToolAutonomy.Resolve(ExternalCall(
                    ToolGateSurface.Interactive, declared, standingGrant: true)).Decision);
            Assert.Equal(
                ToolGateDecision.AutoApprovedSessionGrant,
                ToolAutonomy.Resolve(ExternalCall(
                    ToolGateSurface.Interactive, declared, sessionGrant: true)).Decision);
        }
    }

    [Fact]
    public void TheCard_Warns_ButKeepsBothGrantOffers()
    {
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{ci.ArgAt<string>(0)}({string.Join(",", ci.ArgAt<object[]>(1))})");
        var builder = new ActionCardBuilder(localization, Substitute.For<ITokenMapService>());

        PluginToolCall Call(bool declared) => new(
            BenignExternalTool, Guid.NewGuid(), "some-mcp-server", $"some-mcp-server: {BenignExternalTool}",
            Details: null, Execute: () => Task.FromResult<object?>(null),
            ServerDeclaredDestructive: declared);

        var plain = builder.Build(Call(declared: false), detokenize: false, toolClass: ToolClass.External);
        Assert.False(plain.IsDestructive);
        Assert.Null(plain.WarningText);
        Assert.Equal(4, plain.Decisions.Count);

        var declaredCard = builder.Build(Call(declared: true), detokenize: false, toolClass: ToolClass.External);
        Assert.True(declaredCard.IsDestructive);
        Assert.Equal("Msg_Assistant_PermanentDeleteExternal", declaredCard.WarningText);
        // Same four buttons: the declaration is a warning, not a narrowing.
        Assert.Equal(4, declaredCard.Decisions.Count);
    }
}

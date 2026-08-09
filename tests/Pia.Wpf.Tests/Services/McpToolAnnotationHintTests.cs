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
        bool canPark = false)
        => new(surface, BenignExternalTool, ToolClass.External, serverDeclaredDestructive,
               IsAllowlisted: false, HasSessionGrant: sessionGrant, HasStandingGrant: standingGrant,
               IsNamedGrant: namedGrant, HasNamedDenial: false, Policy: policy, CanPark: canPark);

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

    [Fact]
    public void Unattended_ADeclaredDestructiveExternalTool_IsRefusedEvenWithANamedGrant()
    {
        var before = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Unattended, serverDeclaredDestructive: false, namedGrant: true));
        Assert.Equal(ToolGateOutcome.AutoRun, before.Outcome); // non-vacuity: without the hint it runs

        var after = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Unattended, serverDeclaredDestructive: true, namedGrant: true));

        Assert.Equal(ToolGateOutcome.Refuse, after.Outcome);
        Assert.Equal(ToolGateDecision.DeniedDestructiveFloor, after.Decision);
    }

    /// <summary>The floor is evaluated before the policy arm.</summary>
    [Fact]
    public void Unattended_APolicyCoveringTheClass_DoesNotLiftTheDeclaration()
    {
        var policy = new RunAutonomyPolicy([ToolClass.External]);

        var before = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Unattended, serverDeclaredDestructive: false, policy: policy));
        Assert.Equal(ToolGateOutcome.AutoRun, before.Outcome);

        var after = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Unattended, serverDeclaredDestructive: true, policy: policy));
        Assert.Equal(ToolGateOutcome.Refuse, after.Outcome);
        Assert.Equal(ToolGateDecision.DeniedDestructiveFloor, after.Decision);
    }

    /// <summary>Interactively the floor suppresses auto-approval but still prompts, so the human may allow it once.</summary>
    [Fact]
    public void Interactive_ADeclaredDestructiveExternalTool_PromptsInsteadOfAutoRunning()
    {
        var before = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Interactive, serverDeclaredDestructive: false, standingGrant: true));
        Assert.Equal(ToolGateOutcome.AutoRun, before.Outcome);

        var after = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Interactive, serverDeclaredDestructive: true, standingGrant: true));
        Assert.Equal(ToolGateOutcome.Prompt, after.Outcome);
    }

    /// <summary>The session tier sits above the standing grant and below the floor, so the declaration must reach it too.</summary>
    [Fact]
    public void Interactive_ASessionGrant_DoesNotLiftTheDeclaration()
    {
        var before = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Interactive, serverDeclaredDestructive: false, sessionGrant: true));
        Assert.Equal(ToolGateOutcome.AutoRun, before.Outcome);
        Assert.Equal(ToolGateDecision.AutoApprovedSessionGrant, before.Decision);

        var after = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Interactive, serverDeclaredDestructive: true, sessionGrant: true));
        Assert.Equal(ToolGateOutcome.Prompt, after.Outcome);
    }

    [Fact]
    public void Voice_ADeclaredDestructiveExternalTool_IsRefused()
    {
        var after = ToolAutonomy.Resolve(
            ExternalCall(ToolGateSurface.Voice, serverDeclaredDestructive: true, standingGrant: true));

        Assert.Equal(ToolGateOutcome.Refuse, after.Outcome);
        Assert.Equal(ToolGateDecision.DeniedDestructiveFloor, after.Decision);
    }

    [Fact]
    public void TheHintCannotClearADeleteLikeName()
    {
        // A loosening claim reaches the gate as "no hint", which is what false stands for here.
        var verdict = ToolAutonomy.Resolve(new ToolGateInput(
            ToolGateSurface.Unattended, "delete_everything", ToolClass.External,
            ServerDeclaredDestructive: false,
            IsAllowlisted: false, HasSessionGrant: false, HasStandingGrant: false,
            IsNamedGrant: true, HasNamedDenial: false, Policy: null, CanPark: false));

        Assert.Equal(ToolGateOutcome.Refuse, verdict.Outcome);
        Assert.Equal(ToolGateDecision.DeniedDestructiveFloor, verdict.Decision);
    }

    // ---- the offer rules: the card must not offer what the gate will refuse --------------------------

    [Fact]
    public void NeitherGrantTier_IsOfferedForADeclaredDestructiveTool()
    {
        Assert.True(ToolAutonomy.IsStandingGrantOfferable(
            ToolClass.External, BenignExternalTool, isAllowlisted: false));
        Assert.True(ToolAutonomy.IsSessionGrantOfferable(BenignExternalTool));

        Assert.False(ToolAutonomy.IsStandingGrantOfferable(
            ToolClass.External, BenignExternalTool, isAllowlisted: false, serverDeclaredDestructive: true));
        Assert.False(ToolAutonomy.IsSessionGrantOfferable(BenignExternalTool, serverDeclaredDestructive: true));
    }

    [Fact]
    public void TheCard_WarnsAndWithdrawsTheStandingGrantOffer()
    {
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{ci.ArgAt<string>(0)}({string.Join(",", ci.ArgAt<object[]>(1))})");
        var permissions = Substitute.For<IToolPermissionService>();
        permissions.IsAutoApproveEligible(Arg.Any<string>()).Returns(false);
        var builder = new ActionCardBuilder(localization, Substitute.For<ITokenMapService>(), permissions);

        PluginToolCall Call(bool declared) => new(
            BenignExternalTool, Guid.NewGuid(), "some-mcp-server", $"some-mcp-server: {BenignExternalTool}",
            Details: null, Execute: () => Task.FromResult<object?>(null),
            ServerDeclaredDestructive: declared);

        var plain = builder.Build(Call(declared: false), detokenize: false, toolClass: ToolClass.External);
        Assert.False(plain.IsDestructive);
        Assert.Null(plain.WarningText);
        Assert.True(plain.IsAutoApprovable);
        Assert.True(plain.IsSessionGrantable);

        var declaredCard = builder.Build(Call(declared: true), detokenize: false, toolClass: ToolClass.External);
        Assert.True(declaredCard.IsDestructive);
        Assert.Equal("Msg_Assistant_PermanentDeleteExternal", declaredCard.WarningText);
        Assert.False(declaredCard.IsAutoApprovable);
        Assert.False(declaredCard.IsSessionGrantable);
    }
}

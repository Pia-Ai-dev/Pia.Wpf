using ModelContextProtocol.Protocol;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// T2-7b — consuming an MCP server's <c>ToolAnnotations</c> in the MORE-RESTRICTED DIRECTION ONLY.
/// <para>
/// Two properties, and the second is the one worth a file: a server CAN make its own tool stricter, and a
/// server CANNOT make one safer. The second is not a hypothetical — <c>ReadOnlyHint</c> exists precisely as a
/// "this tool is harmless" claim, and honouring it from a stdio subprocess running with full user privileges
/// outside <c>SafeFolderPath</c> (<c>17-trust-model.md</c> §2) would be a self-service exemption from the gate.
/// </para>
/// <para>
/// The other half is the DEFAULT. MCP's spec says an unspecified <c>destructiveHint</c> should be assumed
/// <c>true</c>; taking that literally here would mark every tool of every annotation-less server destructive,
/// i.e. refuse all unattended MCP and drop interactive auto-approval everywhere. Absence therefore means
/// "no hint", and this file pins it so nobody "fixes" it into spec-compliance without seeing the cost.
/// </para>
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
               IsNamedGrant: namedGrant, Policy: policy, CanPark: canPark);

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

    /// <summary>
    /// The spec's "null ⇒ assume true" default is deliberately NOT applied — including in the shape that
    /// triggers it most often, a server that says only "I modify my environment".
    /// </summary>
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
        // "destructiveHint: false" is a safety claim, and a safety claim is exactly what may not be honoured:
        // it must not be able to clear a delete-like NAME either (see TheHintCannotClearADeleteLikeName).
        Assert.False(McpPluginToolHandler.IsServerDeclaredDestructive(
            new ToolAnnotations { DestructiveHint = false }));
    }

    /// <summary>
    /// A contradictory pair resolves to the STRICTER reading. <c>readOnlyHint: true</c> beside
    /// <c>destructiveHint: true</c> is either a buggy server or a hostile one; both are handled the same way.
    /// </summary>
    [Fact]
    public void ReadOnlyHint_CannotCancelADestructiveHint()
    {
        Assert.True(McpPluginToolHandler.IsServerDeclaredDestructive(
            new ToolAnnotations { ReadOnlyHint = true, DestructiveHint = true }));
    }

    // ---- the gate: what the declaration actually changes ----------------------------------------------

    /// <summary>
    /// The floor. Unattended, a NAMED grant for a benign-looking external tool auto-runs it today — that is the
    /// scheduled-job grant path — and the server's declaration is what turns it into the same hard denial a
    /// delete-NAMED external tool already gets.
    /// </summary>
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

    /// <summary>
    /// The floor is evaluated before the POLICY arm, so a run whose autonomy policy covers External cannot
    /// reach an auto-approval past the declaration either.
    /// </summary>
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

    /// <summary>
    /// Interactively the floor SUPPRESSES auto-approval and still prompts — today's semantics for a
    /// delete-named external tool, deliberately not tightened. The human sees the card (now carrying the
    /// destructive warning) and may still allow it once.
    /// </summary>
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

    /// <summary>
    /// hermes #15's session tier is above the standing grant and below the floor; the declaration must reach it
    /// too, or "Allow for this session" would be the widest authority on offer for the one tool the server
    /// itself flagged.
    /// </summary>
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

    /// <summary>
    /// THE asymmetry, stated as a fact rather than a comment: the hint may only be believed one way. A server
    /// declaring its <c>delete_everything</c> tool non-destructive gets nothing.
    /// </summary>
    [Fact]
    public void TheHintCannotClearADeleteLikeName()
    {
        // false is what IsServerDeclaredDestructive returns for `destructiveHint: false` — i.e. the loosening
        // claim reaches the gate as "no hint", and the NAME rule still refuses the call.
        var verdict = ToolAutonomy.Resolve(new ToolGateInput(
            ToolGateSurface.Unattended, "delete_everything", ToolClass.External,
            ServerDeclaredDestructive: false,
            IsAllowlisted: false, HasSessionGrant: false, HasStandingGrant: false,
            IsNamedGrant: true, Policy: null, CanPark: false));

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

    /// <summary>
    /// The CARD, which is where a human meets the declaration: it must render the destructive warning and stop
    /// offering the one-click standing grant — the same answer the gate now gives.
    /// </summary>
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

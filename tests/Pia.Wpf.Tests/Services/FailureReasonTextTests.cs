using NSubstitute;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// One reader for the open failure vocabulary: the app-owned tokens are localized, anything else is a model
/// summary or an exception message and must reach the surface unchanged. Shared by the run card, the routine
/// run list and the background runner, so a token renamed in one place cannot silently show raw in another.
/// </summary>
public class FailureReasonTextTests
{
    private static ILocalizationService KeyEchoLocalizer()
    {
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        return loc;
    }

    /// <summary>MemberData, not InlineData: the rows follow the constants, so a reworded token still maps.</summary>
    public static TheoryData<string, string> Tokens => new()
    {
        { AgentStepTools.EmptyResponseFailure, "Run_Failed_EmptyResponse" },
        { AgentStepTools.UndetailedFailure, "Run_Failed_Undetailed" },
        { HeadlessRunLauncher.WorkspaceSetupFailure, "Run_Failed_WorkspaceSetup" },
        { HeadlessRunLauncher.ShutdownInterruptedFailure, "Run_Failed_Interrupted" },
        { AgentRunOrchestrator.SupersededFailureReason, "Run_Failed_Superseded" },
    };

    [Theory]
    [MemberData(nameof(Tokens))]
    public void AnAppOwnedToken_IsLocalized(string token, string expectedKey)
    {
        Assert.Equal(expectedKey, FailureReasonText.Describe(token, KeyEchoLocalizer()));
    }

    [Fact]
    public void FreeText_PassesThroughVerbatim()
    {
        const string serverMessage = "Request to upstream AI provider timed out.";

        Assert.Equal(serverMessage, FailureReasonText.Describe(serverMessage, KeyEchoLocalizer()));
    }

    [Fact]
    public void NothingIn_NothingOut()
    {
        Assert.Null(FailureReasonText.Describe(null, KeyEchoLocalizer()));
        Assert.Null(FailureReasonText.Describe("", KeyEchoLocalizer()));
    }
}

using System.IO;
using System.Net.Http;
using Pia.Models;
using Pia.Services;
using Pia.Services.Exceptions;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Pure string/exception in, descriptor out. The load-bearing assertion is the NEGATIVE one: an arbitrary
/// message must map to nothing, because a descriptor that guessed would be exactly the substring matching
/// <c>IsPreModelFailure</c>'s doc comment forbids.
/// </summary>
public class FailureMapperTests
{
    [Fact]
    public void AnArbitraryMessage_MapsToNothing_SoItStillReachesTheCardVerbatim()
    {
        Assert.Null(FailureMapper.ForReason("The upstream gateway returned 502 for model x"));
        Assert.Null(FailureMapper.ForReason("NoProvider is what happened"));   // substring, not the constant
        Assert.Null(FailureMapper.ForReason(null));
        Assert.Null(FailureMapper.ForReason(""));
    }

    /// <summary>
    /// MemberData, not InlineData: an attribute argument is a compile-time constant, so an InlineData row
    /// would capture the constant's TEXT and keep passing against the old literal if someone reworded it.
    /// Read at runtime, these rows follow the constants the way the mapper does.
    /// </summary>
    public static TheoryData<string, FailureLayer> NamedConstants => new()
    {
        { AgentStepTools.EmptyResponseFailure, FailureLayer.Provider },
        { AgentStepTools.UndetailedFailure, FailureLayer.Tool },
        { HeadlessRunLauncher.WorkspaceSetupFailure, FailureLayer.Workspace },
        { HeadlessRunLauncher.ShutdownInterruptedFailure, FailureLayer.Cancelled },
        { AgentRunOrchestrator.SupersededFailureReason, FailureLayer.Cancelled },
        { ScheduledJobService.NoProviderFailureReason, FailureLayer.Provider },
    };

    [Theory]
    [MemberData(nameof(NamedConstants))]
    public void EveryNamedConstant_IsRecognised(string reason, FailureLayer expected)
    {
        var failure = FailureMapper.ForReason(reason);

        Assert.NotNull(failure);
        Assert.Equal(expected, failure!.Layer);
    }

    /// <summary>The narrowness is the point: everything else settles terminally on the first strike.</summary>
    [Fact]
    public void OnlyTheProviderResolveFailure_IsSafeToReRun()
    {
        Assert.True(FailureMapper.ForReason(ScheduledJobService.NoProviderFailureReason)!.SafeToReRun);
        Assert.True(FailureMapper.ForException(new PreModelLaunchException("no provider")).SafeToReRun);

        Assert.False(FailureMapper.ForReason(HeadlessRunLauncher.WorkspaceSetupFailure)!.SafeToReRun);
        Assert.False(FailureMapper.ForException(new HttpRequestException("503")).SafeToReRun);
        Assert.False(FailureMapper.ForException(new LlmTimeoutException("p", 100)).SafeToReRun);
        Assert.False(FailureMapper.ForException(new IOException("disk")).SafeToReRun);
    }

    [Fact]
    public void TheMapperKeysOnExceptionType_NotOnItsMessage()
    {
        // Same words, different type: the transport arm must not be reachable by writing a message.
        Assert.Equal(FailureLayer.Endpoint, FailureMapper.ForException(new HttpRequestException("x")).Layer);
        Assert.Equal(
            FailureLayer.Unclassified,
            FailureMapper.ForException(new InvalidOperationException("HttpRequestException: x")).Layer);
    }

    /// <summary>
    /// Found by running it: a refused connection reaches the orchestrator as AggregateException →
    /// ClientResultException → HttpRequestException → SocketException. Matching only the outermost type
    /// classified every real transport failure as Unclassified, so the card named no layer at all.
    /// </summary>
    [Fact]
    public void ATransportFailure_IsFoundThroughItsWrappers()
    {
        var wrapped = new AggregateException(
            "Retry failed after 4 tries.",
            new InvalidOperationException(
                "client result",
                new HttpRequestException(
                    "refused", new System.Net.Sockets.SocketException(10061))));

        var failure = FailureMapper.ForException(wrapped);

        Assert.Equal(FailureLayer.Endpoint, failure.Layer);
        Assert.False(failure.SafeToReRun);
    }

    /// <summary>Outermost still wins, so a wrapper that IS recognised is not skipped for something deeper.</summary>
    [Fact]
    public void TheOutermostRecognisedWrapper_Wins()
    {
        Assert.Equal(
            FailureLayer.Provider,
            FailureMapper.ForException(new LlmTimeoutException("p", 100, "timed out")).Layer);

        // A pre-model throw nested inside a wrapper is still the launcher vouching for it.
        Assert.True(
            FailureMapper.ForException(
                new AggregateException(new PreModelLaunchException("no provider"))).SafeToReRun);
    }

    [Fact]
    public void AnUnrecognisedException_IsUnclassifiedAndNeverSafe()
    {
        var failure = FailureMapper.ForException(new InvalidOperationException("something odd"));

        Assert.Equal(FailureLayer.Unclassified, failure.Layer);
        Assert.Equal(FailureMapper.UnclassifiedCode, failure.Code);
        Assert.False(failure.SafeToReRun);
    }

    /// <summary>A plain InvalidOperationException must not inherit the launcher's pre-model verdict.</summary>
    [Fact]
    public void TheBaseTypeOfThePreModelException_DoesNotInheritItsVerdict()
    {
        Assert.False(FailureMapper.ForException(new InvalidOperationException("no provider")).SafeToReRun);
    }
}

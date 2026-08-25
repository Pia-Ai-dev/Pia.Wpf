using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// The codec lives on the type because a camelCase writer and a default-options reader disagreed once, and
/// the mismatch surfaced as "no layer" rather than as an error. These pin the round trip and the shapes a
/// reader must survive without throwing — <c>FromJson</c> runs inside the panel's projection, which is called
/// on every run change.
/// </summary>
public class PiaFailureCodecTests
{
    [Fact]
    public void ARoundTripPreservesEveryMember()
    {
        var original = new PiaFailure(FailureLayer.Endpoint, "Transport", SafeToReRun: false);

        var restored = PiaFailure.FromJson(original.ToJson());

        Assert.Equal(original, restored);
    }

    /// <summary>Readable from inside the row, the same reason the diagnostics manifest names its enum.</summary>
    [Fact]
    public void TheLayerIsWrittenByName_NotAsAnOrdinal()
    {
        var json = new PiaFailure(FailureLayer.Provider, "Timeout", true).ToJson();

        Assert.Contains("\"Provider\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"layer\": 3", json, StringComparison.Ordinal);
    }

    /// <summary>A row written by a build that knows a layer this one does not must not throw the panel.</summary>
    [Theory]
    [InlineData(@"{""layer"":""Quantum"",""code"":""x"",""safeToReRun"":false}")]
    [InlineData(@"{""layer"":99,""code"":""x"",""safeToReRun"":false}")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnreadableRow_ReturnsNullRatherThanThrowing(string? json)
    {
        var failure = PiaFailure.FromJson(json);

        Assert.True(failure is null || failure.Layer == FailureLayer.Unclassified);
    }
}

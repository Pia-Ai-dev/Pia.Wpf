namespace Pia.Tests.Services;

using System.Text.Json.Nodes;
using Pia.Services;
using Xunit;

public class GuardrailMarkerTests
{
    [Fact]
    public void IsProtected_True_WhenMarkerPresent() =>
        Assert.True(GuardrailMarker.IsProtected(JsonNode.Parse("""{"guardrail":{"protected":true}}""")));

    [Fact]
    public void IsProtected_False_WhenMarkerAbsent() =>
        Assert.False(GuardrailMarker.IsProtected(JsonNode.Parse("""{"message":{"content":"hi"}}""")));

    [Fact]
    public void IsProtected_False_WhenProtectedIsFalse() =>
        Assert.False(GuardrailMarker.IsProtected(JsonNode.Parse("""{"guardrail":{"protected":false}}""")));

    [Fact]
    public void IsProtected_False_OnNull() => Assert.False(GuardrailMarker.IsProtected(null));

    [Fact]
    public void IsProtected_False_WhenProtectedNotABool() =>
        Assert.False(GuardrailMarker.IsProtected(JsonNode.Parse("""{"guardrail":{"protected":"yes"}}""")));
}

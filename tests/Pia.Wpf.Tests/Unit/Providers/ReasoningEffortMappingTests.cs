using Pia.Models;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Wpf.Tests.Unit.Providers;

public class ReasoningEffortMappingTests
{
    [Theory]
    [InlineData(ReasoningEffort.Minimal, "low")]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.XHigh, "high")]
    public void ToOpenAiResponses_MapsConfiguredEffort_RegardlessOfTools(ReasoningEffort effort, string expectedWire)
    {
        // Regression guard: the Responses path must NOT suppress reasoning when tools are
        // present (the assistant always sends a tool schema). If reasoning were tool-gated,
        // OpenAI reasoning would never surface in the assistant.
        var result = ReasoningEffortMapping.ToOpenAiResponses(effort);

        Assert.NotNull(result);
        Assert.Equal(expectedWire, result.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ReasoningEffort.None)]
    public void ToOpenAiResponses_ReturnsNull_WhenReasoningNotConfigured(ReasoningEffort? effort)
    {
        Assert.Null(ReasoningEffortMapping.ToOpenAiResponses(effort));
    }
}

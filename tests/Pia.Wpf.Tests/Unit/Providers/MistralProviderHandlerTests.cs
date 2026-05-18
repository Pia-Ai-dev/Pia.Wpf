using Pia.Models;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Wpf.Tests.Unit.Providers;

public class MistralProviderHandlerTests
{
    // Mistral's API returns HTTP 422 when `reasoning_effort` is sent to a model
    // that doesn't accept it. Only mistral-small-latest and mistral-medium-3.5
    // currently accept the field, and only with values `none` or `high`.

    [Theory]
    [InlineData("mistral-small-latest")]
    [InlineData("mistral-medium-3.5")]
    public void ShouldEmitReasoning_True_ForCapableModels(string model)
    {
        var provider = MakeProvider(model, ReasoningEffort.Medium);

        var (emit, _) = MistralProviderHandler.ShouldEmitReasoning(provider, hasTools: false);

        Assert.True(emit);
    }

    [Theory]
    [InlineData("mistral-large-latest")]
    [InlineData("magistral-small-latest")]
    [InlineData("magistral-medium-latest")]
    [InlineData("codestral-latest")]
    [InlineData("mistral-tiny")]
    public void ShouldEmitReasoning_False_ForNonCapableModels(string model)
    {
        var provider = MakeProvider(model, ReasoningEffort.Medium);

        var (emit, _) = MistralProviderHandler.ShouldEmitReasoning(provider, hasTools: false);

        Assert.False(emit);
    }

    [Fact]
    public void ShouldEmitReasoning_False_WhenEffortIsNone()
    {
        var provider = MakeProvider("mistral-small-latest", ReasoningEffort.None);

        var (emit, _) = MistralProviderHandler.ShouldEmitReasoning(provider, hasTools: false);

        Assert.False(emit);
    }

    [Fact]
    public void ShouldEmitReasoning_False_WhenEffortIsNull()
    {
        var provider = MakeProvider("mistral-small-latest", null);

        var (emit, _) = MistralProviderHandler.ShouldEmitReasoning(provider, hasTools: false);

        Assert.False(emit);
    }

    [Fact]
    public void ShouldEmitReasoning_False_WhenToolsArePresent()
    {
        var provider = MakeProvider("mistral-small-latest", ReasoningEffort.High);

        var (emit, _) = MistralProviderHandler.ShouldEmitReasoning(provider, hasTools: true);

        Assert.False(emit);
    }

    [Theory]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.High)]
    [InlineData(ReasoningEffort.XHigh)]
    public void ShouldEmitReasoning_ClampsToHigh_ForAnyNonNoneValue(ReasoningEffort effort)
    {
        // Mistral only accepts `none` or `high`. The handler clamps everything
        // non-None to High since Mistral rejects `low`/`medium`/`minimal`.
        var provider = MakeProvider("mistral-small-latest", effort);

        var (emit, level) = MistralProviderHandler.ShouldEmitReasoning(provider, hasTools: false);

        Assert.True(emit);
#pragma warning disable OPENAI001
        Assert.Equal(OpenAI.Chat.ChatReasoningEffortLevel.High, level);
#pragma warning restore OPENAI001
    }

    private static AiProvider MakeProvider(string modelName, ReasoningEffort? effort)
    {
        return new AiProvider
        {
            Name = "test",
            Endpoint = "https://api.mistral.ai/v1",
            ProviderType = AiProviderType.Mistral,
            ModelName = modelName,
            ReasoningEffort = effort,
        };
    }
}

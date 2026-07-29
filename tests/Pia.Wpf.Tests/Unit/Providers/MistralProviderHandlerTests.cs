using System.ClientModel.Primitives;
using OpenAI.Chat;
using Pia.Models;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Wpf.Tests.Unit.Providers;

public class MistralProviderHandlerTests
{
    // Mistral's API returns HTTP 422 when `reasoning_effort` is sent to a model
    // that doesn't accept it. The models that accept the field are mistral-small-latest,
    // mistral-medium-latest, mistral-medium-3.5 and both Magistral sizes, and only with
    // values `none` or `high`.
    //
    // magistral-medium-latest used to appear in BOTH theories below — it was listed as capable
    // (matching ReasoningCapableModels) and as non-capable, so the pair could never both pass.
    // The capable half is the correct one: Magistral is Mistral's reasoning family.

    [Theory]
    [InlineData("mistral-small-latest")]
    [InlineData("mistral-medium-latest")]
    [InlineData("magistral-small-latest")]
    [InlineData("magistral-medium-latest")]
    [InlineData("mistral-medium-3.5")]
    public void ShouldEmitReasoning_True_ForCapableModels(string model)
    {
        var provider = MakeProvider(model, ReasoningEffort.Medium);

        var (emit, _) = MistralProviderHandler.ShouldEmitReasoning(provider, hasTools: false);

        Assert.True(emit);
    }

    [Theory]
    [InlineData("mistral-large-latest")]
    [InlineData("codestral-latest")]
    [InlineData("mistral-tiny")]
    public void ShouldEmitReasoning_False_ForNonCapableModels(string model)
    {
        var provider = MakeProvider(model, ReasoningEffort.Medium);

        var (emit, _) = MistralProviderHandler.ShouldEmitReasoning(provider, hasTools: false);

        Assert.False(emit);
    }

    [Fact]
    public void ShouldEmitReasoning_SendsNone_WhenEffortIsNone()
    {
        // These models reason by default, so "None" must actively send
        // `reasoning_effort: none` to suppress it — omitting the field would
        // leave reasoning on.
        var provider = MakeProvider("mistral-small-latest", ReasoningEffort.None);

        var (emit, level) = MistralProviderHandler.ShouldEmitReasoning(provider, hasTools: false);

        Assert.True(emit);
#pragma warning disable OPENAI001
        Assert.Equal(ChatReasoningEffortLevel.None, level);
#pragma warning restore OPENAI001
    }

    [Fact]
    public void ShouldEmitReasoning_SendsNone_WhenEffortIsNone_EvenWithTools()
    {
        // The assistant always sends tools, so the disable case must fire on
        // tool-using turns too — otherwise picking "None" never takes effect.
        var provider = MakeProvider("mistral-medium-latest", ReasoningEffort.None);

        var (emit, level) = MistralProviderHandler.ShouldEmitReasoning(provider, hasTools: true);

        Assert.True(emit);
#pragma warning disable OPENAI001
        Assert.Equal(ChatReasoningEffortLevel.None, level);
#pragma warning restore OPENAI001
    }

    [Fact]
    public void ShouldEmitReasoning_False_WhenEffortIsNull()
    {
        var provider = MakeProvider("mistral-small-latest", null);

        var (emit, _) = MistralProviderHandler.ShouldEmitReasoning(provider, hasTools: false);

        Assert.False(emit);
    }

    [Fact]
    public void ShouldEmitReasoning_False_WhenEnablingReasoningWithToolsPresent()
    {
        // Turning reasoning ON is still suppressed during tool-using turns. (The
        // disable case is the exception — see the "EvenWithTools" test above.)
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

    [Fact]
    public void CreateChatOptions_SerializesReasoningEffortNone_ForNoneEffortWithTools()
    {
        // End-to-end guard for the linchpin: the request body must carry
        // `reasoning_effort: none`. A value the SDK silently drops as a default
        // would break the disable without failing the tuple-level tests.
        var handler = new MistralProviderHandler();
        var provider = MakeProvider("mistral-medium-latest", ReasoningEffort.None);

        var options = handler.CreateChatOptions(provider, hasTools: true);

        Assert.NotNull(options.RawRepresentationFactory);
#pragma warning disable OPENAI001
        var raw = (ChatCompletionOptions)options.RawRepresentationFactory!(null!)!;
#pragma warning restore OPENAI001
        var json = ModelReaderWriter.Write(raw).ToString();
        Assert.Contains("\"reasoning_effort\":\"none\"", json);
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

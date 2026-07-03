using System.ClientModel.Primitives;
using OpenAI.Chat;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Wpf.Tests.Unit.Providers;

public class ReasoningExtractorTests
{
    [Fact]
    public void FromJson_ReadsReasoningFromStreamingDelta()
    {
        var json = """{"choices":[{"index":0,"delta":{"reasoning":"thinking hard"}}]}""";
        Assert.Equal("thinking hard", ReasoningExtractor.FromJson(json));
    }

    [Fact]
    public void FromJson_ReadsReasoningFromCompleteMessage()
    {
        var json = """{"choices":[{"index":0,"message":{"reasoning":"deliberating"}}]}""";
        Assert.Equal("deliberating", ReasoningExtractor.FromJson(json));
    }

    [Fact]
    public void FromJson_IgnoresReasoningContent_ToAvoidDoubleCapture()
    {
        // reasoning_content is mapped by Microsoft.Extensions.AI to TextReasoningContent
        // already; this extractor must not also surface it.
        var json = """{"choices":[{"index":0,"delta":{"reasoning_content":"adapter handles this"}}]}""";
        Assert.Null(ReasoningExtractor.FromJson(json));
    }

    [Fact]
    public void FromJson_ReturnsNull_WhenNoReasoning()
    {
        var json = """{"choices":[{"index":0,"delta":{"content":"hello"}}]}""";
        Assert.Null(ReasoningExtractor.FromJson(json));
    }

    [Fact]
    public void FromJson_ReturnsNull_WhenReasoningIsNotAString()
    {
        var json = """{"choices":[{"index":0,"delta":{"reasoning":{"effort":"high"}}}]}""";
        Assert.Null(ReasoningExtractor.FromJson(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"choices":[]}""")]
    public void FromJson_ReturnsNull_ForEmptyOrMalformedOrNoChoices(string? json)
    {
        Assert.Null(ReasoningExtractor.FromJson(json));
    }

    [Fact]
    public void FromRawRepresentation_ReturnsNull_ForNull()
    {
        Assert.Null(ReasoningExtractor.FromRawRepresentation(null));
    }

    [Fact]
    public void FromRawRepresentation_ReturnsNull_ForNonModelObject()
    {
        Assert.Null(ReasoningExtractor.FromRawRepresentation("just a string"));
    }

    [Fact]
    public void FromRawRepresentation_RecoversReasoning_FromOpenAiSdkStreamingUpdate()
    {
        // The OpenAI SDK round-trips unknown fields, so `reasoning` survives even though
        // Microsoft.Extensions.AI drops it from the mapped content.
        var json = """{"id":"x","object":"chat.completion.chunk","created":0,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","reasoning":"recovered from raw"}}]}""";
        var update = ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromString(json));

        Assert.Equal("recovered from raw", ReasoningExtractor.FromRawRepresentation(update));
    }
}

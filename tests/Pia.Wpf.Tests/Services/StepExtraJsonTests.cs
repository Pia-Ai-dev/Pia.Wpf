using System.Text.Json.Nodes;
using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public sealed class StepExtraJsonTests
{
    private static AgentStep Step(string? extraJson) => new() { Id = Guid.NewGuid(), ExtraJson = extraJson };

    [Fact]
    public void WithArtifactRef_PreservesEveryOtherMember()
    {
        var merged = StepExtraJson.WithArtifactRef("""{"parallelGroup":2,"future":"x"}""", "out/q3.md");

        var root = Assert.IsType<JsonObject>(JsonNode.Parse(merged));
        Assert.Equal(3, root.Count);
        Assert.Equal(2, root["parallelGroup"]!.GetValue<int>());
        Assert.Equal("x", root["future"]!.GetValue<string>());
        Assert.Equal("out/q3.md", root["artifactRef"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithArtifactRef_CreatesTheDocumentWhenThereIsNone(string? extraJson)
    {
        Assert.Equal("""{"artifactRef":"out/q3.md"}""", StepExtraJson.WithArtifactRef(extraJson, "out/q3.md"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("7")]
    public void WithArtifactRef_ReplacesAnUnparseableOrNonObjectDocument(string extraJson)
    {
        Assert.Equal("""{"artifactRef":"out/q3.md"}""", StepExtraJson.WithArtifactRef(extraJson, "out/q3.md"));
    }

    [Fact]
    public void WithArtifactRef_LastWriteWins()
    {
        var once = StepExtraJson.WithArtifactRef("""{"parallelGroup":1}""", "first.md");
        var twice = StepExtraJson.WithArtifactRef(once, "second.md");

        var root = Assert.IsType<JsonObject>(JsonNode.Parse(twice));
        Assert.Equal(2, root.Count);
        Assert.Equal("second.md", root["artifactRef"]!.GetValue<string>());
        Assert.Equal(1, root["parallelGroup"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"parallelGroup":1}""")]
    [InlineData("""{"artifactRef":null}""")]
    [InlineData("""{"artifactRef":7}""")]
    [InlineData("""{"artifactRef":"   "}""")]
    public void ArtifactRefOf_IsNullForEverythingItCannotRead(string? extraJson)
    {
        Assert.Null(StepExtraJson.ArtifactRefOf(Step(extraJson)));
    }

    [Fact]
    public void ArtifactRefOf_ReadsTheValue()
    {
        Assert.Equal("a.md", StepExtraJson.ArtifactRefOf(Step("""{"artifactRef":"a.md"}""")));
    }

    [Fact]
    public void ArtifactRefOf_FlattensAndCapsWhatItReads()
    {
        var flattened = StepExtraJson.ArtifactRefOf(Step(StepExtraJson.WithArtifactRef(null, "out/\r\na\tb.md")));
        Assert.Equal("out/  a b.md", flattened);

        var capped = StepExtraJson.ArtifactRefOf(Step(StepExtraJson.WithArtifactRef(null, new string('x', 400))));
        Assert.Equal(new string('x', StepExtraJson.MaxArtifactChars) + "…", capped);
    }

    /// <summary>A silent rename would orphan every already-persisted row.</summary>
    [Fact]
    public void TheKeyIsSpelledArtifactRef()
    {
        Assert.Contains("\"artifactRef\"", StepExtraJson.WithArtifactRef(null, "a.md"), StringComparison.Ordinal);
    }
}

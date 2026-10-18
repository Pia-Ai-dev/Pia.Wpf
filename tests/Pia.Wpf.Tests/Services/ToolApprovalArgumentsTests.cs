using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The disclosure renderer. The store keeps the whole call; these caps bound only what a WPF TextBlock inside a
/// virtualized list is asked to format.
/// </summary>
public sealed class ToolApprovalArgumentsTests
{
    [Fact]
    public void DescribeDetail_RendersEveryArgumentOnePerLine_IncludingNonStringValues()
    {
        var detail = ToolApprovalArguments.DescribeDetail("""{"path":"a/b.md","count":42,"flags":[1,2]}""");

        Assert.NotNull(detail);
        Assert.Equal(new[] { "path=a/b.md", "count=42", "flags=[1,2]" }, detail!.Value.Text.Split('\n'));
        Assert.False(detail.Value.Shortened);
    }

    /// <summary>write_file's content usually precedes its path, so a first argument that ate the whole budget
    /// would hide the one term the reader is deciding on.</summary>
    [Fact]
    public void DescribeDetail_CapsOneValueAtHalfTheTotal_SoALaterArgumentSurvives()
    {
        var huge = new string('x', 20_000);
        var detail = ToolApprovalArguments.DescribeDetail($$"""{"content":"{{huge}}","path":"x/y.md"}""");

        Assert.NotNull(detail);
        var lines = detail!.Value.Text.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("content=", lines[0], StringComparison.Ordinal);
        Assert.Equal("content=".Length + ToolApprovalArguments.MaxDetailValueChars + 1, lines[0].Length);
        Assert.EndsWith("…", lines[0], StringComparison.Ordinal);
        Assert.Equal("path=x/y.md", lines[1]);
        Assert.True(detail.Value.Shortened);
    }

    [Fact]
    public void DescribeDetail_NamesEveryArgumentEvenWhenTheTotalCapBites()
    {
        var value = new string('y', ToolApprovalArguments.MaxDetailValueChars);
        var detail = ToolApprovalArguments.DescribeDetail(
            $$"""{"k1":"{{value}}","k2":"{{value}}","k3":"{{value}}"}""");

        Assert.NotNull(detail);
        var lines = detail!.Value.Text.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal($"k1={value}", lines[0]);
        Assert.Equal("k2=…", lines[1]);
        Assert.Equal("k3=…", lines[2]);
        Assert.True(detail.Value.Text.Length < ToolApprovalArguments.MaxDetailTotalChars + 16);
        Assert.True(detail.Value.Shortened);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    public void DescribeDetail_ReadsNullForAbsentMalformedOrNonObjectJson(string? argumentsJson) =>
        Assert.Null(ToolApprovalArguments.DescribeDetail(argumentsJson));

    [Fact]
    public void Heading_JoinsTheDescriptionAndEveryWarningLine()
    {
        Assert.Equal(
            "Add \"Onboarding\" to Handbook — stored unencrypted — used by other groups",
            ToolApprovalArguments.Heading("Add \"Onboarding\" to Handbook", "stored unencrypted\nused by other groups"));
    }

    [Fact]
    public void Heading_WithoutAWarning_IsTheDescription() =>
        Assert.Equal("Remove \"Old\" from Handbook", ToolApprovalArguments.Heading("Remove \"Old\" from Handbook", null));

    [Theory]
    [InlineData(null, "a warning")]
    [InlineData("", null)]
    [InlineData("   ", "a warning")]
    public void Heading_WithoutADescription_IsNull(string? description, string? warning) =>
        Assert.Null(ToolApprovalArguments.Heading(description, warning));

    /// <summary>The rows written before headings existed hold the capped argument line in that column, so the
    /// panel must not mistake it for a heading and print the arguments twice.</summary>
    [Fact]
    public void ParkedHeading_IsNullForTheLegacyArgumentLine()
    {
        var call = new Microsoft.Extensions.AI.FunctionCallContent("c1", "delete_file", new Dictionary<string, object?>
        {
            ["path"] = "a/b.md",
            ["count"] = 3,
            ["note"] = "  ",
            ["reason"] = "tidy",
        });
        var legacyLine = ToolApprovalArguments.Describe(call);
        var json = AgentToolExchangeSerializer.SerializeArguments(call.Arguments);

        Assert.Equal("path=a/b.md reason=tidy", legacyLine);
        Assert.Null(ToolApprovalArguments.ParkedHeading(legacyLine, json));
    }

    [Fact]
    public void ParkedHeading_IsTheDisplayTextWhenItIsNotTheLegacyLine() =>
        Assert.Equal(
            "Add \"X\" to Handbook — stored unencrypted",
            ToolApprovalArguments.ParkedHeading("Add \"X\" to Handbook — stored unencrypted", """{"kb_id":"1","path":"x.md"}"""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ParkedHeading_IsNullWhenThereIsNoDisplayText(string? displayArgs) =>
        Assert.Null(ToolApprovalArguments.ParkedHeading(displayArgs, """{"path":"x.md"}"""));
}

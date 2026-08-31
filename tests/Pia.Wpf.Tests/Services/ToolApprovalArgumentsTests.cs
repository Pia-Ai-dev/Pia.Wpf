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
}

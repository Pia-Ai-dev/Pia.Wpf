using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Covers the widened diff gate in <see cref="ActionCardBuilder.Build"/>: a Memory-classed pending
/// action (update_source) carrying a DiffPreview must render DiffLines like Files does, while
/// remember/forget (no DiffPreview) keep rendering the key/value Details path unchanged.
/// </summary>
public class ActionCardBuilderMemoryDiffTests
{
    private static ActionCardBuilder MakeBuilder()
    {
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]!);
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]!);

        return new ActionCardBuilder(loc, Substitute.For<ITokenMapService>());
    }

    private static PluginToolCall MemoryCall(
        string toolName, IReadOnlyList<DiffLine>? diff, string? details, string? targetPath = null)
        => new(toolName, Guid.Empty, "memory", "Update source: sources/a.txt", details,
            () => Task.FromResult<object?>("ok"), diff, targetPath);

    [Fact]
    public void Build_UpdateSourceWithDiff_PopulatesDiffLines_AndBypassesDetails()
    {
        var diff = new List<DiffLine>
        {
            new(DiffLineKind.Context, "keep"),
            new(DiffLineKind.Removed, "old"),
            new(DiffLineKind.Added, "new"),
        };

        // Details is non-null (would otherwise parse into Label/Value rows) — must be bypassed.
        var card = MakeBuilder().Build(MemoryCall("update_source", diff, "{\"key\":\"value\"}"), detokenize: false);

        Assert.Equal(ActionCardCategory.Memory, card.Category);
        Assert.True(card.HasDiff);
        Assert.Equal(3, card.DiffLines.Count);
        Assert.Equal(DiffLineKind.Removed, card.DiffLines[1].Kind);
        Assert.Empty(card.Details);
        Assert.Equal("ActionCard_Action_Update ActionCard_Category_Memory", card.Title);
    }

    [Theory]
    [InlineData("remember")]
    [InlineData("forget")]
    public void Build_MemoryVerbsWithoutDiff_StillFallBackToDetails(string toolName)
    {
        // remember/forget never set DiffPreview — the widened gate must not change their rendering.
        var card = MakeBuilder().Build(
            MemoryCall(toolName, diff: null, details: "{\"key\":\"value\"}"), detokenize: false);

        Assert.False(card.HasDiff);
        Assert.Empty(card.DiffLines);
        Assert.NotEmpty(card.Details);
    }

    [Fact]
    public void Build_UpdateSourcePlumbsTargetPath()
    {
        var diff = new List<DiffLine> { new(DiffLineKind.Added, "x") };

        var card = MakeBuilder().Build(
            MemoryCall("update_source", diff, null, targetPath: "sources/a.txt"), detokenize: false);

        Assert.Equal("sources/a.txt", card.FilePath);
    }
}

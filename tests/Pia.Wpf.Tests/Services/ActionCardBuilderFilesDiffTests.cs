using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Covers the files-specific branch of <see cref="ActionCardBuilder.Build"/>: a write_file pending
/// action carrying a DiffPreview must populate <see cref="ActionCardInfo.DiffLines"/> and BYPASS the
/// ParseKeyValueText path (so Details stays empty and the card renders the diff, not a char count).
/// </summary>
public class ActionCardBuilderFilesDiffTests
{
    private static ActionCardBuilder MakeBuilder(ITokenMapService? tokenMap = null)
    {
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]!);
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]!);

        tokenMap ??= Substitute.For<ITokenMapService>();
        var permissions = Substitute.For<IToolPermissionService>();
        return new ActionCardBuilder(loc, tokenMap, permissions);
    }

    private static PluginToolCall FilesWrite(IReadOnlyList<DiffLine>? diff, string? details)
        => new("write_file", Guid.Empty, "files", "Update file 'a.txt'", details,
            () => Task.FromResult<object?>("ok"), diff);

    [Fact]
    public void Build_FilesWithDiff_PopulatesDiffLines_AndBypassesDetails()
    {
        var diff = new List<DiffLine>
        {
            new(DiffLineKind.Context, "keep"),
            new(DiffLineKind.Removed, "old"),
            new(DiffLineKind.Added, "new"),
        };
        var builder = MakeBuilder();

        // Details is non-null (would otherwise parse into the Label/Value rows) — must be bypassed.
        var card = builder.Build(FilesWrite(diff, "3 character(s) will be written."), detokenize: false);

        Assert.True(card.HasDiff);
        Assert.Equal(3, card.DiffLines.Count);
        Assert.Equal(DiffLineKind.Removed, card.DiffLines[1].Kind);
        Assert.Equal("old", card.DiffLines[1].Text);
        // Diff bypasses the key/value details path.
        Assert.Empty(card.Details);
        Assert.Equal(ActionCardCategory.Files, card.Category);
    }

    [Fact]
    public void Build_FilesNewFile_AllAddedDiff()
    {
        var diff = new List<DiffLine>
        {
            new(DiffLineKind.Added, "line1"),
            new(DiffLineKind.Added, "line2"),
        };
        var card = MakeBuilder().Build(FilesWrite(diff, null), detokenize: false);

        Assert.True(card.HasDiff);
        Assert.All(card.DiffLines, d => Assert.Equal(DiffLineKind.Added, d.Kind));
    }

    [Fact]
    public void Build_FilesNoDiff_FallsBackToDetails()
    {
        // A files action without a diff (e.g. delete_file) still uses the key/value path.
        var card = MakeBuilder().Build(
            new PluginToolCall("delete_file", Guid.Empty, "files", "Delete file 'a.txt'", null,
                () => Task.FromResult<object?>("ok")),
            detokenize: false);

        Assert.False(card.HasDiff);
        Assert.Empty(card.DiffLines);
    }

    [Fact]
    public void Build_FilesDiff_Detokenizes_WhenEnabled()
    {
        var tokenMap = Substitute.For<ITokenMapService>();
        tokenMap.Detokenize("TOKEN").Returns("secret");
        tokenMap.Detokenize(Arg.Is<string>(s => s != "TOKEN")).Returns(ci => (string)ci[0]!);

        var diff = new List<DiffLine> { new(DiffLineKind.Added, "TOKEN"), new(DiffLineKind.Context, "ctx"), new(DiffLineKind.Removed, "x") };
        var card = MakeBuilder(tokenMap).Build(FilesWrite(diff, null), detokenize: true);

        Assert.Equal("secret", card.DiffLines[0].Text);
    }
}

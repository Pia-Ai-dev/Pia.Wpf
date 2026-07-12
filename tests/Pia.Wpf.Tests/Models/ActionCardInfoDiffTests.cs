using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// Covers the diff-specific behavior on <see cref="ActionCardInfo"/>: the diff body is expanded while
/// pending, auto-collapses when the decision resolves, stays re-expandable afterwards, and exposes the
/// hunk-folded <see cref="ActionCardInfo.DiffRows"/> built lazily from <see cref="ActionCardInfo.DiffLines"/>.
/// </summary>
public class ActionCardInfoDiffTests
{
    private static ActionCardInfo DiffCard(params DiffLine[] lines) => new()
    {
        Title = "t",
        Summary = "s",
        Category = ActionCardCategory.Files,
        ToolName = "write_file",
        DiffLines = new ObservableCollection<DiffLine>(lines),
    };

    [Fact]
    public void IsDiffExpanded_DefaultsTrue()
    {
        Assert.True(DiffCard(new DiffLine(DiffLineKind.Added, "x")).IsDiffExpanded);
    }

    [Fact]
    public void Decline_CollapsesDiff()
    {
        var card = DiffCard(new DiffLine(DiffLineKind.Added, "x"));

        card.DeclineCommand.Execute(null);

        Assert.False(card.IsDiffExpanded);
        Assert.Equal(ActionCardState.Declined, card.State);
    }

    [Fact]
    public void AllowOnce_CollapsesDiff()
    {
        var card = DiffCard(new DiffLine(DiffLineKind.Added, "x"));

        card.AllowOnceCommand.Execute(null);

        Assert.False(card.IsDiffExpanded);
        Assert.Equal(ActionCardState.Accepted, card.State);
    }

    [Fact]
    public void ToggleDiffExpand_WorksAfterResolution()
    {
        var card = DiffCard(new DiffLine(DiffLineKind.Added, "x"));
        card.AllowOnceCommand.Execute(null); // resolved + collapsed
        Assert.False(card.IsDiffExpanded);

        card.ToggleDiffExpandCommand.Execute(null);
        Assert.True(card.IsDiffExpanded); // re-expandable — no IsPending gate

        card.ToggleDiffExpandCommand.Execute(null);
        Assert.False(card.IsDiffExpanded);
    }

    [Fact]
    public void DiffRows_PassThroughSmallDiff_AndIsCached()
    {
        var card = DiffCard(new DiffLine(DiffLineKind.Added, "a"), new DiffLine(DiffLineKind.Context, "c"));

        Assert.Equal(2, card.DiffRows.Count);
        Assert.All(card.DiffRows, r => Assert.IsType<DiffLine>(r));
        Assert.Same(card.DiffRows, card.DiffRows); // lazy, built once
    }

    [Fact]
    public void DiffRows_FoldsLongContextRun()
    {
        var lines = new List<DiffLine> { new(DiffLineKind.Added, "a") };
        lines.AddRange(Enumerable.Range(0, 10).Select(i => new DiffLine(DiffLineKind.Context, $"c{i}", i + 1, i + 1)));
        lines.Add(new DiffLine(DiffLineKind.Added, "b"));

        var card = DiffCard(lines.ToArray());

        Assert.Contains(card.DiffRows, r => r is CollapsedDiffRun);
    }
}

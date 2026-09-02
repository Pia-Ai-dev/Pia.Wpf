using System.Collections.ObjectModel;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// Covers the roll-up header's own arithmetic and status rule: the counts are recomputed from the cards
/// (their own tallies raise no notification), a path written twice still counts as one file, and a set
/// whose cards were approved under different tiers shows no status sentence.
/// </summary>
public class FileChangeSetTests
{
    private static ActionCardInfo AcceptedDiff(
        string path, int added, int removed, bool autoApproved = true, string status = "auto-approved")
    {
        var lines = new ObservableCollection<DiffLine>();
        for (var i = 0; i < added; i++) lines.Add(new DiffLine(DiffLineKind.Added, "+"));
        for (var i = 0; i < removed; i++) lines.Add(new DiffLine(DiffLineKind.Removed, "-"));

        var card = new ActionCardInfo
        {
            Title = "t",
            Summary = "s",
            Category = ActionCardCategory.Files,
            ToolName = "write_file",
            FilePath = path,
            DiffLines = lines,
            IsAutoApproved = autoApproved,
            AutoApprovedStatusText = status,
            AcceptedStatusText = status,
        };
        card.AllowOnceCommand.Execute(null);
        return card;
    }

    [Fact]
    public void EmptySet_HasNoCountsAndNoStatus()
    {
        var set = new FileChangeSet();

        Assert.Equal(0, set.FileCount);
        Assert.Equal(0, set.TotalAdded);
        Assert.Equal(0, set.TotalRemoved);
        Assert.False(set.IsAutoApproved);
        Assert.False(set.HasResolvedStatusText);
    }

    [Fact]
    public void Totals_SumAcrossCards()
    {
        var set = new FileChangeSet();
        set.Cards.Add(AcceptedDiff("a.cs", 12, 3));
        set.Cards.Add(AcceptedDiff("b.cs", 41, 8));

        Assert.Equal(2, set.FileCount);
        Assert.Equal(53, set.TotalAdded);
        Assert.Equal(11, set.TotalRemoved);
    }

    [Fact]
    public void FileCount_CountsDistinctPaths()
    {
        var set = new FileChangeSet();
        set.Cards.Add(AcceptedDiff("a.cs", 1, 0));
        set.Cards.Add(AcceptedDiff("A.CS", 2, 0));

        Assert.Equal(1, set.FileCount);
        Assert.Equal(3, set.TotalAdded);
    }

    [Fact]
    public void AddingACard_RaisesTheComputedProperties()
    {
        var set = new FileChangeSet();
        var raised = new List<string>();
        set.PropertyChanged += (_, e) => { if (e.PropertyName is { } n) raised.Add(n); };

        set.Cards.Add(AcceptedDiff("a.cs", 1, 1));

        Assert.Contains(nameof(FileChangeSet.FileCount), raised);
        Assert.Contains(nameof(FileChangeSet.TotalAdded), raised);
        Assert.Contains(nameof(FileChangeSet.TotalRemoved), raised);
        Assert.Contains(nameof(FileChangeSet.ResolvedStatusText), raised);
    }

    [Fact]
    public void UniformStatus_IsShown()
    {
        var set = new FileChangeSet();
        set.Cards.Add(AcceptedDiff("a.cs", 1, 0, status: "always allowed"));
        set.Cards.Add(AcceptedDiff("b.cs", 1, 0, status: "always allowed"));

        Assert.True(set.IsAutoApproved);
        Assert.True(set.HasResolvedStatusText);
        Assert.Equal("always allowed", set.ResolvedStatusText);
    }

    [Fact]
    public void MixedTiers_ShowNoStatusText()
    {
        var set = new FileChangeSet();
        set.Cards.Add(AcceptedDiff("a.cs", 1, 0, status: "always allowed"));
        set.Cards.Add(AcceptedDiff("b.cs", 1, 0, status: "allowed for this session"));

        Assert.False(set.HasResolvedStatusText);
        Assert.Equal(string.Empty, set.ResolvedStatusText);
    }

    [Fact]
    public void OneManuallyApprovedCard_DropsTheAutoApprovedIcon()
    {
        var set = new FileChangeSet();
        set.Cards.Add(AcceptedDiff("a.cs", 1, 0));
        set.Cards.Add(AcceptedDiff("b.cs", 1, 0, autoApproved: false));

        Assert.False(set.IsAutoApproved);
    }

    [Fact]
    public void IsExpanded_DefaultsFolded_AndToggles()
    {
        var set = new FileChangeSet();

        Assert.False(set.IsExpanded);

        set.ToggleExpandCommand.Execute(null);
        Assert.True(set.IsExpanded);

        set.ToggleExpandCommand.Execute(null);
        Assert.False(set.IsExpanded);
    }
}

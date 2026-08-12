using System.ComponentModel;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The dialog binds its primary button and its target-path line to these members, and WPF binding
/// failures are trace-only — so the contract is pinned here rather than left to the XAML.
/// </summary>
public class MeetingSaveEditModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(2));

    private static MeetingSaveEditModel Create(string title = "Q3 roadmap sync")
        => new(Start, title, "Anna Weber");

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Q3 roadmap sync", true)]
    public void CanSave_RequiresANonBlankTitle(string title, bool expected)
    {
        Assert.Equal(expected, Create(title).CanSave);
    }

    [Fact]
    public void ChangingTheTitle_RaisesCanSaveAndTargetReference()
    {
        var model = Create(string.Empty);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)model).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.Title = "Standup";

        Assert.Contains(nameof(MeetingSaveEditModel.CanSave), raised);
        Assert.Contains(nameof(MeetingSaveEditModel.TargetReference), raised);
        Assert.True(model.CanSave);
    }

    [Fact]
    public void TargetReference_TracksTheTitle()
    {
        var model = Create("Q3 roadmap sync");
        Assert.EndsWith("-q3-roadmap-sync.md", model.TargetReference, StringComparison.Ordinal);

        model.Title = "Standup";
        Assert.EndsWith("-standup.md", model.TargetReference, StringComparison.Ordinal);
    }

    [Fact]
    public void ToMetadata_SplitsTheCommaSeparatedFields_AndTrimsTheTitle()
    {
        var model = Create("  Q3 roadmap sync  ");
        model.Attendees = "Anna Weber, Tom Kraus,";
        model.Tags = " roadmap , planning ";

        var meta = model.ToMetadata(Start.AddMinutes(47), "teams");

        Assert.Equal("Q3 roadmap sync", meta.Title);
        Assert.Equal(["Anna Weber", "Tom Kraus"], meta.Attendees);
        Assert.Equal(["roadmap", "planning"], meta.Tags);
        Assert.Equal(Start, meta.Start);
        Assert.Equal(Start.AddMinutes(47), meta.End);
        Assert.Equal("teams", meta.Source);
    }
}

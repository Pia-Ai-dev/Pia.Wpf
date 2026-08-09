using System.Globalization;
using System.Windows;
using Pia.Converters;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Converters;

// RunProgressState is APPENDABLE and the label converter's fall-through is Run_State_Completed, so a member with no
// arm renders a run that is still working as "Completed" — hence theories over Enum.GetValues, not listed rows.
public sealed class RunProgressConvertersTests
{
    [Fact]
    public void EveryRunProgressState_HasItsOwnLabelKey_AndOnlyCompletedReadsCompleted()
    {
        var members = Enum.GetValues<RunProgressState>();
        Assert.True(members.Length >= 8, "non-vacuity: the enum must actually have members to pin");

        var completedReaders = members.Where(s => RunStateToLabelConverter.LabelKey(s) == "Run_State_Completed").ToList();
        Assert.Equal<IEnumerable<RunProgressState>>(
            [RunProgressState.Completed, RunProgressState.TruncatedCompleted],
            completedReaders.OrderBy(s => (int)s).ToList());

        var keys = members.Select(RunStateToLabelConverter.LabelKey).ToList();
        Assert.All(keys, k => Assert.False(string.IsNullOrWhiteSpace(k)));
        // N members, N-1 distinct keys: the single collision is Completed/TruncatedCompleted above.
        Assert.Equal(members.Length - 1, keys.Distinct().Count());
    }

    // WaitingForChildren spins too: the parent is idle but its children are working, and a still spinner reads as a
    // stalled run and invites the user to cancel a healthy fan-out.
    [Theory]
    [InlineData(RunProgressState.Planning, true)]
    [InlineData(RunProgressState.Running, true)]
    [InlineData(RunProgressState.WaitingForChildren, true)]
    [InlineData(RunProgressState.Completed, false)]
    [InlineData(RunProgressState.TruncatedCompleted, false)]
    [InlineData(RunProgressState.Failed, false)]
    [InlineData(RunProgressState.WaitingForInput, false)]
    [InlineData(RunProgressState.Paused, false)]
    public void TheSpinnerIsLitWheneverWorkIsHappening(RunProgressState state, bool visible)
    {
        var converted = new RunStateToSpinnerVisibilityConverter()
            .Convert(state, typeof(Visibility), null!, CultureInfo.InvariantCulture);

        Assert.Equal(visible ? Visibility.Visible : Visibility.Collapsed, converted);
    }

    // Every member must have a row above, or an appended state would go unasserted rather than red.
    [Fact]
    public void TheSpinnerTheoryCoversEveryState()
        => Assert.Equal(8, Enum.GetValues<RunProgressState>().Length);

    // The KEY, never the resolved Brush, which would need a live Application. Paused and WaitingForInput must share
    // one key because both offer the identical Continue command.
    [Theory]
    [InlineData(RunProgressState.Paused, "PiaAccentBrush")]
    public void PausedSharesTheWaitingForInputAccentBrushKey(RunProgressState state, string expectedKey)
    {
        Assert.Equal(expectedKey, RunStateToBrushConverter.BrushKey(state));
        Assert.Equal(RunStateToBrushConverter.BrushKey(RunProgressState.WaitingForInput),
            RunStateToBrushConverter.BrushKey(state));
    }
}

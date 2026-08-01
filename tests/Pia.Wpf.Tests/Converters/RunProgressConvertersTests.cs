using System.Globalization;
using System.Windows;
using Pia.Converters;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Converters;

/// <summary>
/// Batch 07 G8 — the run-header converters, pinned per <see cref="RunProgressState"/> member.
/// <para>
/// Both facts here exist because <see cref="RunProgressState"/> is an APPENDABLE enum read by
/// <c>switch</c> expressions with fall-through arms, and the label converter's fall-through is
/// <c>Run_State_Completed</c>. So a member with no explicit arm renders a run that is still working as
/// <b>"Completed"</b> — the most expensive lie this panel can tell, and the exact failure appending
/// <see cref="RunProgressState.WaitingForChildren"/> would otherwise have introduced. These are theories over
/// <c>Enum.GetValues</c> rather than hand-listed rows precisely so the NEXT appended member is caught too.
/// </para>
/// No WPF resources are touched: <c>LabelKey</c> is the extracted key mapping and the spinner converter
/// returns a bare <see cref="Visibility"/>, so neither needs an <c>Application</c> (unlike
/// <c>RunStateToBrushConverter</c>, which resolves theme brushes and is therefore not covered here).
/// </summary>
public sealed class RunProgressConvertersTests
{
    /// <summary>
    /// T-CONV-1, <b>REGRESSION</b>. Every member gets its own label key, and <c>Run_State_Completed</c> is
    /// reached by exactly the two members that mean "completed".
    /// <para>
    /// Note the arithmetic: there are N members and N-1 distinct keys, because <c>Completed</c> and
    /// <c>TruncatedCompleted</c> deliberately share one — the truncation is carried by a separate note, not by
    /// the chip. (The spec's T-CONV-1 row says "the key count matches the member count", which is off by that
    /// one deliberate collision; annotated in the spec.)
    /// </para>
    /// Neutralize: remove the <c>WaitingForChildren</c> arm from
    /// <c>RunStateToLabelConverter.LabelKey</c> — it falls to the default and reads "Completed", failing the
    /// distinct-key count and the only-two-members leg together.
    /// </summary>
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

    /// <summary>
    /// T-CONV-2, <b>REGRESSION</b>. The header spinner is lit whenever work is happening — including while the
    /// parent is parked at <see cref="RunProgressState.WaitingForChildren"/>, where the parent itself is idle
    /// but its child runs are working. A still spinner there reads as a stalled run and invites the user to
    /// cancel a healthy fan-out.
    /// <para>Neutralize: drop <c>WaitingForChildren</c> from the converter's positive set — that row reds.</para>
    /// </summary>
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

    /// <summary>
    /// A row-count pin for the theory above: every member must have a row, or an appended state would simply
    /// go unasserted rather than red. Kept separate so the theory stays a pure per-state claim.
    /// </summary>
    [Fact]
    public void TheSpinnerTheoryCoversEveryState()
        => Assert.Equal(8, Enum.GetValues<RunProgressState>().Length);

    /// <summary>
    /// Batch 08 8a. Pins the KEY the state-brush mapping resolves for <see cref="RunProgressState.Paused"/>
    /// against the extracted <see cref="RunStateToBrushConverter.BrushKey"/> — never the resolved
    /// <see cref="System.Windows.Media.Brush"/> itself, which needs a live <see cref="System.Windows.Application"/>
    /// this test suite does not construct (see this file's own class-level note). A paused run now carries the
    /// SAME action-needed accent <see cref="RunProgressState.WaitingForInput"/> does — both offer the identical
    /// Continue command — so the two keys must be equal, not merely both non-null.
    /// </summary>
    [Theory]
    [InlineData(RunProgressState.Paused, "PiaAccentBrush")]
    public void PausedSharesTheWaitingForInputAccentBrushKey(RunProgressState state, string expectedKey)
    {
        Assert.Equal(expectedKey, RunStateToBrushConverter.BrushKey(state));
        Assert.Equal(RunStateToBrushConverter.BrushKey(RunProgressState.WaitingForInput),
            RunStateToBrushConverter.BrushKey(state));
    }
}

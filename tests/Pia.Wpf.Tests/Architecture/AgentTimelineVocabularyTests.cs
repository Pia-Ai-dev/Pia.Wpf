using Pia.Models;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>
/// The 04 ↔ 03 shared vocabulary, mechanized. Batch 04 defined <see cref="ToolClass"/>,
/// <see cref="ToolGateSurface"/> and <see cref="ToolGateDecision"/>; Batch 03 persists all three as ordinals
/// and adds two of its own. These facts exist so an append to 04's enums cannot silently leave 03 with a
/// decision it does not record and does not render.
/// </summary>
public class AgentTimelineVocabularyTests
{
    [Fact]
    public void PersistedTimelineEnumsStartAtUnknownZero_AndNeverCollide()
    {
        AssertAppendOnlyShape<AgentTimelineEventKind>();
        AssertAppendOnlyShape<AgentTimelineOutcome>();

        // 04's three are persisted by this batch too, so their shape is 03's problem as well.
        AssertAppendOnlyShape<ToolClass>();
        AssertAppendOnlyShape<ToolGateSurface>();
        AssertAppendOnlyShape<ToolGateDecision>();
    }

    private static void AssertAppendOnlyShape<TEnum>() where TEnum : struct, Enum
    {
        var values = Enum.GetValues<TEnum>();
        var ordinals = values.Select(v => Convert.ToInt32(v)).ToArray();

        Assert.Equal(0, ordinals.Min());
        Assert.Equal("Unknown", Enum.GetName(values.First(v => Convert.ToInt32(v) == 0))!);
        // A duplicate ordinal means two names share a persisted value: the read side can no longer tell them
        // apart, which is the same defect as a renumber.
        Assert.Equal(ordinals.Length, ordinals.Distinct().Count());
        // Contiguous from 0, so "all ordinals 0..N" in a render test really is exhaustive.
        Assert.Equal(Enumerable.Range(0, ordinals.Length), ordinals.OrderBy(o => o));
    }
}

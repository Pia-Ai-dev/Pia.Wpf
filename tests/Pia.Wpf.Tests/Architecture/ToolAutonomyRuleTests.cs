using Pia.Models;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>
/// Batch 04's two structural rules. T-ARCH-2 mechanizes the append-only guardrail on the three PERSISTED gate
/// enums; the gate-file source scan (T-ARCH-1) lands with the gates themselves.
/// </summary>
public class ToolAutonomyRuleTests
{
    public static TheoryData<Type> PersistedGateEnums =>
        new() { typeof(ToolClass), typeof(ToolGateDecision), typeof(ToolGateSurface) };

    /// <summary>
    /// An ordinal a newer build writes (or an older DB carries) must render as <em>unknown</em> — never throw
    /// and never be re-mapped — which requires a zero member named <c>Unknown</c>. Duplicate values are the
    /// other half: two names sharing an ordinal is how a "rename" silently becomes a reuse.
    /// </summary>
    [Theory]
    [MemberData(nameof(PersistedGateEnums))]
    public void EveryPersistedGateEnumStartsAtUnknownZero(Type enumType)
    {
        var names = Enum.GetNames(enumType);
        Assert.Contains("Unknown", names);
        Assert.Equal(0, Convert.ToInt32(Enum.Parse(enumType, "Unknown")));

        var values = names.Select(n => Convert.ToInt32(Enum.Parse(enumType, n))).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
    }

    /// <summary>
    /// <see cref="ToolGateOutcome"/> is control flow, NOT persisted, so it deliberately does not carry the
    /// rule above. Asserted so nobody "fixes" it into the persisted set.
    /// </summary>
    [Fact]
    public void ToolGateOutcome_IsNotPartOfThePersistedVocabulary()
    {
        Assert.DoesNotContain("Unknown", Enum.GetNames<ToolGateOutcome>());
    }
}

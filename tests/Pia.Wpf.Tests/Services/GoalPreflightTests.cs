using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The refusal predicate is a conjunction (no whitespace AND 8 chars or fewer), so any multi-word goal is accepted unconditionally regardless of length.</summary>
public sealed class GoalPreflightTests
{
    [Theory]
    [InlineData("ggg")]
    [InlineData("g")]
    [InlineData("asdf")]
    [InlineData("12345678")] // exactly 8 chars, no whitespace — still refused (boundary is inclusive)
    [InlineData("  ggg  ")] // leading/trailing whitespace trimmed away before the length check
    public void SingleTokenJunkAtOrUnder8Chars_IsRefused(string goal)
    {
        Assert.True(GoalPreflight.IsRefused(goal));
    }

    [Theory]
    [InlineData("Fix CI")]
    [InlineData("Ship it")]
    [InlineData("Fix the build")]
    [InlineData("Write the release notes for v2")]
    public void ALegitimatelyTerseOrOrdinaryGoal_IsNeverRefused(string goal)
    {
        // A space anywhere in the trimmed goal passes it unconditionally, regardless of length.
        Assert.False(GoalPreflight.IsRefused(goal));
    }

    [Theory]
    [InlineData("123456789")] // 9 chars, no whitespace — one char over the boundary, so no longer refused
    [InlineData("ggggggggg")]
    public void SingleTokenJunkOver8Chars_IsNotRefusedByLayer1(string goal)
    {
        // Once a no-whitespace token is long enough to plausibly be a real word or slug, catching it is left to the planner instead.
        Assert.False(GoalPreflight.IsRefused(goal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyOrWhitespaceOnlyGoal_IsNotThisLayersConcern(string? goal)
    {
        // The composer's own "requires real text" gate already refuses this case for a different reason;
        // GoalPreflight must not double-refuse it and blur two unrelated failure causes into one signal.
        Assert.False(GoalPreflight.IsRefused(goal));
    }
}

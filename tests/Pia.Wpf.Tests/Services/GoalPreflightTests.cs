using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// 18 D1 layer 1, spec §8.5 — the group's own false-positive fact: a layer 1 that refuses a real goal is
/// WORSE than no layer 1 at all, because the user has no recourse (the goal never reaches the model, so
/// there is no question to answer, only a dead button). The predicate is Q2's conjunction — refuse only
/// when the trimmed goal has NO WHITESPACE *and* is 8 characters or fewer — so this file pins both arms:
/// "ggg" refused, and every multi-word goal (however short) accepted unconditionally.
/// </summary>
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
        // The whole point of the conjunction: a space anywhere in the trimmed goal passes it unconditionally,
        // regardless of overall length, because layer 2 (the planner's own decline) is where a genuinely
        // ungroundable multi-word goal gets caught — WITH a question the user can answer.
        Assert.False(GoalPreflight.IsRefused(goal));
    }

    [Theory]
    [InlineData("123456789")] // 9 chars, no whitespace — one char over the boundary, so no longer refused
    [InlineData("ggggggggg")]
    public void SingleTokenJunkOver8Chars_IsNotRefusedByLayer1(string goal)
    {
        // Layer 1 is deliberately narrow (§10.1): once a no-whitespace token is long enough to plausibly be a
        // real word or slug, catching it is left to layer 2. Conflating the two thresholds is exactly the
        // "one layer tuned for both jobs" shape the owner rejected in favour of two layers (18 D1).
        Assert.False(GoalPreflight.IsRefused(goal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyOrWhitespaceOnlyGoal_IsNotThisLayersConcern(string? goal)
    {
        // The composer's own "requires real text" gate (!string.IsNullOrWhiteSpace) already refuses this case
        // for a different, distinct reason — GoalPreflight must not double-refuse it, or a caller that checks
        // GoalPreflight alone (ChatSessionManager.StartBackgroundRunAsync) would show no distinguishable
        // signal for two unrelated failure causes.
        Assert.False(GoalPreflight.IsRefused(goal));
    }
}

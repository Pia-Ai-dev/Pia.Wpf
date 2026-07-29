using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// RunProfile.FromBudget (1.2c) turns user-configured Agent-run budget settings into a run envelope,
/// clamping to safe bounds so a zero/negative value can never terminate a run instantly (never-a-clean-run).
/// </summary>
public sealed class RunProfileTests
{
    [Fact]
    public void FromBudget_WithinBounds_PassesThrough()
    {
        var p = RunProfile.FromBudget(maxSteps: 12, maxReplans: 1, wallClockMinutes: 30);

        Assert.Equal(12, p.MaxSteps);
        Assert.Equal(1, p.MaxReplans);
        Assert.Equal(TimeSpan.FromMinutes(30), p.WallClock);
    }

    [Fact]
    public void FromBudget_ClampsFloors()
    {
        var p = RunProfile.FromBudget(maxSteps: 0, maxReplans: -3, wallClockMinutes: 0);

        Assert.Equal(RunProfile.MinSteps, p.MaxSteps);           // never 0 steps → would end instantly
        Assert.Equal(RunProfile.MinReplans, p.MaxReplans);
        Assert.Equal(TimeSpan.FromMinutes(RunProfile.MinWallClockMinutes), p.WallClock);
    }

    [Fact]
    public void FromBudget_ClampsCeilings()
    {
        var p = RunProfile.FromBudget(maxSteps: 9999, maxReplans: 99, wallClockMinutes: 9999);

        Assert.Equal(RunProfile.MaxStepsCap, p.MaxSteps);
        Assert.Equal(RunProfile.MaxReplansCap, p.MaxReplans);
        Assert.Equal(TimeSpan.FromMinutes(RunProfile.MaxWallClockMinutes), p.WallClock);
    }

    [Fact]
    public void Interactive_DefaultsMatchTheSettingsDefaults()
    {
        // AppSettings defaults (24 / 2 / 20) must reproduce RunProfile.Interactive so an untouched
        // install behaves exactly as before the setting existed.
        var p = RunProfile.FromBudget(24, 2, 20);

        Assert.Equal(RunProfile.Interactive, p);
    }
}

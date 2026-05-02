using Pia.Models;
using Pia.Services.Scheduling;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

public class RecurrenceCalculatorTests
{
    private readonly RecurrenceCalculator _calc = new();

    [Fact]
    public void Once_WithSpecificDate_UsesThatDate()
    {
        var now = new DateTime(2026, 5, 2, 10, 0, 0);
        var result = _calc.ComputeNextFireAt(
            recurrence: RecurrenceType.Once,
            timeOfDay: new TimeOnly(14, 30),
            specificDate: new DateTime(2026, 5, 5),
            dayOfWeek: null, dayOfMonth: null, month: null, now: now);
        Assert.Equal(new DateTime(2026, 5, 5, 14, 30, 0), result);
    }

    [Fact]
    public void Daily_TimeAlreadyPassedToday_RollsToTomorrow()
    {
        var now = new DateTime(2026, 5, 2, 15, 0, 0);
        var result = _calc.ComputeNextFireAt(
            RecurrenceType.Daily, new TimeOnly(9, 0),
            null, null, null, null, now);
        Assert.Equal(new DateTime(2026, 5, 3, 9, 0, 0), result);
    }

    [Fact]
    public void Daily_TimeStillToday_StaysToday()
    {
        var now = new DateTime(2026, 5, 2, 7, 0, 0);
        var result = _calc.ComputeNextFireAt(
            RecurrenceType.Daily, new TimeOnly(9, 0),
            null, null, null, null, now);
        Assert.Equal(new DateTime(2026, 5, 2, 9, 0, 0), result);
    }

    [Fact]
    public void Weekly_TargetIsTodayButTimePassed_RollsOneWeek()
    {
        // Saturday 2026-05-02 at 15:00 -> next Saturday at 09:00
        var now = new DateTime(2026, 5, 2, 15, 0, 0);
        var result = _calc.ComputeNextFireAt(
            RecurrenceType.Weekly, new TimeOnly(9, 0),
            null, DayOfWeek.Saturday, null, null, now);
        Assert.Equal(new DateTime(2026, 5, 9, 9, 0, 0), result);
    }

    [Fact]
    public void Monthly_TargetDayPassed_RollsToNextMonth()
    {
        var now = new DateTime(2026, 5, 20, 10, 0, 0);
        var result = _calc.ComputeNextFireAt(
            RecurrenceType.Monthly, new TimeOnly(8, 0),
            null, null, dayOfMonth: 5, null, now);
        Assert.Equal(new DateTime(2026, 6, 5, 8, 0, 0), result);
    }

    [Fact]
    public void Yearly_Feb29InNonLeap_ClampsToFeb28()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0);
        var result = _calc.ComputeNextFireAt(
            RecurrenceType.Yearly, new TimeOnly(0, 0),
            null, null, dayOfMonth: 29, month: 2, now);
        Assert.Equal(new DateTime(2026, 2, 28, 0, 0, 0), result);
    }
}

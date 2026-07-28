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

    /// <summary>
    /// W3 no-clamp pin. A Once occurrence in the PAST must be returned unchanged — the calculator must
    /// never nudge it forward. ScheduledJobService settles a fired one-off by flipping Status instead,
    /// which is what removes it from the due query; a clamp here would turn a diagnosable relaunch loop
    /// into a Once job that quietly behaves like a Daily one.
    ///
    /// The blast radius is wider than the scheduler: this calculator is ONE singleton shared with
    /// reminders, and ReminderService routes Once through it on create and on enable. With the Once arm
    /// clamped, "remind me today at 15:00" typed at 15:05 would silently jump to tomorrow instead of
    /// firing on the next tick.
    ///
    /// The other Once test above uses a FUTURE date, so without this test a clamp lands green.
    /// </summary>
    [Fact]
    public void Once_WithPastSpecificDate_ReturnsThatPastInstantUnchanged()
    {
        var now = new DateTime(2026, 5, 2, 10, 0, 0);
        var result = _calc.ComputeNextFireAt(
            recurrence: RecurrenceType.Once,
            timeOfDay: new TimeOnly(14, 30),
            specificDate: new DateTime(2026, 4, 28),
            dayOfWeek: null, dayOfMonth: null, month: null, now: now);
        Assert.Equal(new DateTime(2026, 4, 28, 14, 30, 0), result);
        Assert.True(result < now, "a past Once occurrence must stay past");
    }

    /// <summary>
    /// The same day, earlier hour — the case ReminderService hits for "remind me today at 15:00" typed
    /// at 15:05. Must stay today, not roll to tomorrow.
    /// </summary>
    [Fact]
    public void Once_WithTodaySpecificDateAndPassedTime_StaysToday()
    {
        var now = new DateTime(2026, 5, 2, 15, 5, 0);
        var result = _calc.ComputeNextFireAt(
            recurrence: RecurrenceType.Once,
            timeOfDay: new TimeOnly(15, 0),
            specificDate: new DateTime(2026, 5, 2),
            dayOfWeek: null, dayOfMonth: null, month: null, now: now);
        Assert.Equal(new DateTime(2026, 5, 2, 15, 0, 0), result);
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

using Pia.Models;

namespace Pia.Services.Scheduling;

public class RecurrenceCalculator : IRecurrenceCalculator
{
    public DateTime ComputeNextFireAt(
        RecurrenceType recurrence,
        TimeOnly timeOfDay,
        DateTime? specificDate,
        DayOfWeek? dayOfWeek,
        int? dayOfMonth,
        int? month,
        DateTime now)
    {
        var todayAtTime = now.Date + timeOfDay.ToTimeSpan();

        return recurrence switch
        {
            RecurrenceType.Once => specificDate.HasValue
                ? specificDate.Value.Date + timeOfDay.ToTimeSpan()
                : todayAtTime > now ? todayAtTime : todayAtTime.AddDays(1),
            RecurrenceType.Daily => todayAtTime > now ? todayAtTime : todayAtTime.AddDays(1),
            RecurrenceType.Weekly => ComputeNextWeekly(now, timeOfDay, dayOfWeek ?? now.DayOfWeek),
            RecurrenceType.Monthly => ComputeNextMonthly(now, timeOfDay, dayOfMonth ?? now.Day),
            RecurrenceType.Yearly => ComputeNextYearly(now, timeOfDay, month ?? now.Month, dayOfMonth ?? now.Day),
            _ => todayAtTime > now ? todayAtTime : todayAtTime.AddDays(1)
        };
    }

    private static DateTime ComputeNextWeekly(DateTime now, TimeOnly timeOfDay, DayOfWeek targetDay)
    {
        var daysUntil = ((int)targetDay - (int)now.DayOfWeek + 7) % 7;
        var candidate = now.Date.AddDays(daysUntil) + timeOfDay.ToTimeSpan();
        if (candidate <= now)
            candidate = candidate.AddDays(7);
        return candidate;
    }

    private static DateTime ComputeNextMonthly(DateTime now, TimeOnly timeOfDay, int targetDay)
    {
        targetDay = Math.Min(targetDay, DateTime.DaysInMonth(now.Year, now.Month));
        var candidate = new DateTime(now.Year, now.Month, targetDay) + timeOfDay.ToTimeSpan();
        if (candidate <= now)
        {
            var next = now.AddMonths(1);
            targetDay = Math.Min(targetDay, DateTime.DaysInMonth(next.Year, next.Month));
            candidate = new DateTime(next.Year, next.Month, targetDay) + timeOfDay.ToTimeSpan();
        }
        return candidate;
    }

    private static DateTime ComputeNextYearly(DateTime now, TimeOnly timeOfDay, int targetMonth, int targetDay)
    {
        targetDay = Math.Min(targetDay, DateTime.DaysInMonth(now.Year, targetMonth));
        var candidate = new DateTime(now.Year, targetMonth, targetDay) + timeOfDay.ToTimeSpan();
        if (candidate <= now)
        {
            var nextYear = now.Year + 1;
            targetDay = Math.Min(targetDay, DateTime.DaysInMonth(nextYear, targetMonth));
            candidate = new DateTime(nextYear, targetMonth, targetDay) + timeOfDay.ToTimeSpan();
        }
        return candidate;
    }
}

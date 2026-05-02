using Pia.Models;

namespace Pia.Services.Scheduling;

public interface IRecurrenceCalculator
{
    DateTime ComputeNextFireAt(
        RecurrenceType recurrence,
        TimeOnly timeOfDay,
        DateTime? specificDate,
        DayOfWeek? dayOfWeek,
        int? dayOfMonth,
        int? month,
        DateTime now);
}

namespace Pia.ViewModels.Models;

/// <summary>
/// Time-relative grouping bucket for reminders shown in the reminders list.
/// </summary>
public enum ReminderBucket
{
    Overdue,
    Today,
    Tomorrow,
    ThisWeek,
    Later,
}

namespace Pia.Models;

public enum RecurrenceType { Once, Daily, Weekly, Monthly, Yearly }
public enum ReminderStatus { Active, Snoozed, Completed, Disabled }

public class Reminder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Description { get; set; }
    public RecurrenceType Recurrence { get; set; }
    public TimeOnly TimeOfDay { get; set; }
    public DayOfWeek? DayOfWeek { get; set; }
    public int? DayOfMonth { get; set; }
    public int? Month { get; set; }
    public DateTime? SpecificDate { get; set; }
    public DateTime NextFireAt { get; set; }
    public ReminderStatus Status { get; set; } = ReminderStatus.Active;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastFiredAt { get; set; }
}

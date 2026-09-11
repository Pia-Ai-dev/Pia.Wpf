namespace Pia.Models;

// Crosses the sync wire as an int and is cast back without validation, so this enum is APPEND-ONLY.
// Manual is a routine that never fires on its own; a reminder may not use it.
public enum RecurrenceType { Once, Daily, Weekly, Monthly, Yearly, Manual }
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

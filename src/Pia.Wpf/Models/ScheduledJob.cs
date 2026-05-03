namespace Pia.Models;

public enum ScheduledJobKind { Research }
public enum ScheduledJobStatus { Active, Disabled, Failed }

public class ScheduledJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Query { get; set; }
    public ScheduledJobKind Kind { get; set; } = ScheduledJobKind.Research;
    public ResearchAnswerLength AnswerLength { get; set; } = ResearchAnswerLength.Balanced;
    public Guid? ProviderId { get; set; }
    public RecurrenceType Recurrence { get; set; }
    public TimeOnly TimeOfDay { get; set; }
    public DayOfWeek? DayOfWeek { get; set; }
    public int? DayOfMonth { get; set; }
    public int? Month { get; set; }
    public DateTime? SpecificDate { get; set; }
    public DateTime NextFireAt { get; set; }
    public ScheduledJobStatus Status { get; set; } = ScheduledJobStatus.Active;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public DateTime? LastFiredAt { get; set; }
    public Guid? LastResultEntryId { get; set; }
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Device that owns the firing schedule. Only the owner device runs the job; other devices
    /// see it in the UI and (after sync) see the resulting history but never trigger a run.
    /// Null on legacy rows created before sync was wired — those stay device-local on whichever
    /// machine they were originally created on.
    /// </summary>
    public Guid? OwnerDeviceId { get; set; }
}

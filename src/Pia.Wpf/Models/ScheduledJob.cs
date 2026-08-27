namespace Pia.Models;

// Persisted as int — append-only, never reorder. AgentTask (1) runs the job's Query as an unattended
// headless Planned agent run via IHeadlessRunLauncher; Research (0) keeps the existing runner.
// MeetingAttendance (2) joins MeetingUrl as the browser attendee; it never crosses the sync wire,
// because a peer on an older build would cast the unknown 2 and fall through to the Research leg,
// running the meeting's title as a research query.
public enum ScheduledJobKind { Research, AgentTask, MeetingAttendance }

// Persisted as TEXT (Enum.Parse in ScheduledJobService.MapJob) but crosses the sync wire as an int
// (SyncMapper.cs:905/:923 -> :953/:974, cast back with no Enum.IsDefined validation), so this enum is
// APPEND-ONLY: never reorder, never remove. A peer on an older build receives the unknown ordinal 3,
// casts it to an undefined ScheduledJobStatus and stores it verbatim as the string "3" — which its
// `Status = 'Active'` queries exclude, i.e. the job is inert there and round-trips unchanged. That is
// the intended meaning, but any future UI must tolerate an unknown status value.
public enum ScheduledJobStatus
{
    /// <summary>Armed: the job fires when NextFireAt comes due on its owner device.</summary>
    Active,

    /// <summary>Switched off by the user. Re-arming via EnableAsync is expected and re-fires the job.</summary>
    Disabled,

    /// <summary>
    /// Retired by failure: five consecutive strikes for a recurring job; for a one-off, its single failure —
    /// except a PRE-MODEL failure (the pinned provider could not be resolved, so nothing ran), which earns
    /// one re-arm and only retires on the second attempt. See <c>MarkRunFailedAsync</c>.
    /// </summary>
    Failed,

    /// <summary>
    /// A one-off job whose single firing has been spent — it has been dispatched, it ran, it parked for
    /// resume, or the user skipped it; it will not fire again. NextFireAt is deliberately left at the past
    /// instant it was meant to fire (an honest record); this Status is what removes the row from the due query,
    /// which is why it is written at DISPATCH and not only at settle (see
    /// <c>IScheduledJobService.MarkOccurrenceDispatchedAsync</c>) — a run whose outcome is still unknown has
    /// nonetheless spent the one firing, and a failed outcome flips the row to <see cref="Failed"/> after.
    /// </summary>
    Completed
}

public class ScheduledJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Query { get; set; }
    public ScheduledJobKind Kind { get; set; } = ScheduledJobKind.Research;

    /// <summary>
    /// Write-tool names this job is allowed to execute when it runs as a background assistant
    /// turn. Reads are always allowed; writes are denied unless listed here (reads default-allow,
    /// writes default-deny). Synced config — the owner's grant travels with the job.
    /// </summary>
    public List<string> GrantedTools { get; set; } = [];

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
    /// T2-18 — QUIET MODE for a monitor job (hermes's <c>[SILENT]</c> analog): suppress the notification a
    /// SUCCESSFUL firing would raise. An hourly "has anything changed?" job that answers "no" every time is
    /// exactly the job whose Flow card and Windows toast are noise.
    /// <para>
    /// SUCCESS ONLY, deliberately. A monitor that stops working silently is worse than one that is noisy, so
    /// <c>IScheduledJobNotificationSurface.NotifyFailure</c> ignores this flag entirely — "do not tell me when it
    /// worked" is a different request from "hide it when it breaks". The run still produces its chat either way;
    /// what is suppressed is the push, not the record.
    /// </para>
    /// <para>
    /// LOCAL-ONLY: absent from <c>SyncScheduledJob</c> and from <c>UpsertFromSyncAsync</c>'s SET list, like
    /// <c>LastFiredAt</c>/<c>ConsecutiveFailures</c>/<c>LastResultEntryId</c> — whether a device pushes a toast is
    /// a property of that DEVICE's notification surface, and adding it to the wire would need the server DTO
    /// anyway. A pull therefore cannot reset it.
    /// </para>
    /// </summary>
    public bool QuietOnSuccess { get; set; }

    /// <summary>
    /// Persona this job's run uses; falls back to the active persona when it no longer resolves.
    /// Device-local: putting it on the wire needs the server's own columns first.
    /// </summary>
    public Guid? PersonaId { get; set; }

    /// <summary>
    /// Reasoning effort this job's run is stamped with; outranks the persona's own. Device-local, like
    /// <see cref="PersonaId"/>.
    /// </summary>
    public ReasoningEffort? ReasoningEffort { get; set; }

    /// <summary>
    /// Which catalog card produced this job; null for a blank start and on every row predating the column.
    /// Read-only provenance — editing a job never changes it. Device-local, like <see cref="PersonaId"/>.
    /// </summary>
    public string? BlueprintKey { get; set; }

    /// <summary>
    /// Device that owns the firing schedule. Only the owner device runs the job; other devices
    /// see it in the UI and (after sync) see the resulting history but never trigger a run.
    /// Null on legacy rows created before sync was wired — those stay device-local on whichever
    /// machine they were originally created on.
    /// </summary>
    public Guid? OwnerDeviceId { get; set; }

    /// <summary>
    /// Teams link a <see cref="ScheduledJobKind.MeetingAttendance"/> job joins. DEVICE-LOCAL and
    /// deliberately absent from <c>SyncScheduledJob</c>: a join link is a bearer token for the meeting,
    /// and <c>Query</c> is plaintext on the wire unless E2EE happens to be on.
    /// </summary>
    public string? MeetingUrl { get; set; }

    /// <summary>
    /// When the user acknowledged that Pia may record this meeting. Null means not acknowledged, and the
    /// job refuses to join — the overlay's consent tick is per-session and in-memory, so an unattended
    /// join has to carry its own. Device-local, like <see cref="MeetingUrl"/>.
    /// </summary>
    public DateTime? MeetingConsentAckAt { get; set; }
}

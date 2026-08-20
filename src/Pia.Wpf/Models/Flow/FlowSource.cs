namespace Pia.Models.Flow;

/// <summary>
/// Where a Flow item originated. Drives the source glyph and the durability rules (design §6, §8).
/// Extensible — new producers add a member.
/// </summary>
public enum FlowSource
{
    Snackbar,
    InAppToast,
    BackgroundChat,
    Reminder,
    ScheduledJob,
    TodoDeadline,
    // Appended (persisted as int, append-only). Terminal agent-run notification.
    AgentRun,
    Assignment,
    Policy,
}

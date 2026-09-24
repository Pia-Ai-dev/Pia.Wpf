using Pia.Models.Flow;

namespace Pia.Services.Flow;

/// <summary>
/// How long a Flow item may sit in the rail, per source and severity. Rationale for each number:
/// docs/flow_retention/.
/// </summary>
public static class FlowRetention
{
    /// <summary>Null means the item never ages out — something else owns its removal.</summary>
    public static TimeSpan? MaxAgeFor(FlowSource source, FlowSeverity severity)
    {
        // Reminder is the one pending decision safe to sweep: DismissAsync updates its row rather than
        // deleting it, so the reminder stays listed in the Reminders view after its card goes.
        if (severity == FlowSeverity.ActionRequired)
            return source == FlowSource.Reminder ? TimeSpan.FromDays(30) : null;

        return source switch
        {
            // The reconcile loop retracts a todo that stopped being due, so a surviving card is still overdue.
            FlowSource.TodoDeadline => null,
            FlowSource.Snackbar or FlowSource.InAppToast => severity is FlowSeverity.Warning or FlowSeverity.Error
                ? TimeSpan.FromDays(1)
                // The one persistent Info these publish is the unattended-screen-capture privacy notice.
                : TimeSpan.FromDays(7),
            FlowSource.Policy => TimeSpan.FromDays(3),
            // A monitor that broke must stay loud far longer than one that merely ran.
            FlowSource.ScheduledJob => severity == FlowSeverity.Error ? TimeSpan.FromDays(30) : TimeSpan.FromDays(7),
            _ => TimeSpan.FromDays(14),
        };
    }
}

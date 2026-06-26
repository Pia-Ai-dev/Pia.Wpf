using Pia.Models.Flow;
using Pia.Services.Interfaces;

namespace Pia.Services.Flow;

/// <summary>
/// Re-implements the in-app toast surface over Flow (design §7), retiring the hand-rolled Border toast.
/// Generic callers (VMs/services that inject <see cref="INotificationService"/>) publish session-only,
/// null-dedup Flow items per the §8 in-app rows. Entity-backed sources publish richer items directly.
/// </summary>
public sealed class FlowNotificationService : INotificationService
{
    private readonly IFlowService _flow;

    public FlowNotificationService(IFlowService flow)
    {
        _flow = flow;
    }

    public void ShowToast(string message, int durationMs = 3000) =>
        Publish(FlowSeverity.Info, message, FlowLifetime.Transient(Duration(durationMs)));

    public void ShowSuccess(string message, int durationMs = 3000) =>
        Publish(FlowSeverity.Success, message, FlowLifetime.Transient(Duration(durationMs)));

    public void ShowError(string message, int durationMs = 5000) =>
        Publish(FlowSeverity.Error, message, FlowLifetime.Persistent);

    private void Publish(FlowSeverity severity, string message, FlowLifetime lifetime) =>
        _flow.Publish(new FlowItemDraft
        {
            Severity = severity,
            Source = FlowSource.InAppToast,
            Title = message,
            DedupKey = null,
            Lifetime = lifetime,
            RequestDurable = false,
        });

    private static TimeSpan Duration(int durationMs) =>
        durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : TimeSpan.FromSeconds(3);
}

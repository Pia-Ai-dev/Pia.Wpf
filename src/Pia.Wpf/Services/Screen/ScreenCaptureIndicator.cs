using System.Collections.Concurrent;
using Pia.Models.Flow;
using Pia.Services.Flow;
using Pia.Services.Interfaces;

namespace Pia.Services.Screen;

/// <summary>A persistent Flow item, not a three-second toast: an unattended capture happens while the user is
/// elsewhere, so a transient notice is gone before they look. Not durable — the audit line is that record.</summary>
public sealed class ScreenCaptureIndicator : IScreenCaptureIndicator
{
    private readonly IFlowService _flow;
    private readonly ILocalizationService _localization;
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public ScreenCaptureIndicator(IFlowService flow, ILocalizationService localization)
    {
        _flow = flow;
        _localization = localization;
    }

    public void NotifyCapture(ScreenCaptureAuditEvent evt)
    {
        if (evt.Surface != ScreenCaptureSurfaces.Unattended)
            return;

        var key = evt.Granter ?? evt.TaskId?.ToString() ?? "unattended";
        var count = _counts.AddOrUpdate(key, 1, (_, previous) => previous + 1);

        _flow.Publish(new FlowItemDraft
        {
            Severity = FlowSeverity.Info,
            Source = FlowSource.InAppToast,
            Title = _localization.Format("Msg_ScreenCapture_UnattendedNotice", TargetLabel(evt), count),
            Body = string.Empty,
            DedupKey = $"screen-capture:{key}",
            Lifetime = FlowLifetime.Persistent,
            RequestDurable = false,
        });
    }

    /// <summary>Never the window title — this text is user-visible but the title is not this notice's job.</summary>
    private string TargetLabel(ScreenCaptureAuditEvent evt)
    {
        if (evt.TargetKind == ScreenCaptureTargetKinds.Monitor)
            return _localization["Msg_ScreenCapture_MonitorLabel"];

        return string.IsNullOrWhiteSpace(evt.ProcessName)
            ? _localization["Msg_ScreenCapture_UnnamedProgramLabel"]
            : evt.ProcessName;
    }
}

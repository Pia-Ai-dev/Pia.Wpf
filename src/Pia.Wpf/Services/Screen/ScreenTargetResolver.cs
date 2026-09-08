using System.Globalization;

namespace Pia.Services.Screen;

public enum ScreenTargetResolution
{
    Resolved,
    UnknownKind,
    MissingMatch,
    NoMatch,
    Ambiguous,
}

public readonly record struct ScreenTargetMatch(
    ScreenTargetResolution Outcome,
    CaptureTarget? Target,
    IReadOnlyList<CaptureTarget> Candidates);

/// <summary>Turns the model's <c>target</c>/<c>match</c> pair into one capture target, or says why it cannot.</summary>
public static class ScreenTargetResolver
{
    public static ScreenTargetMatch Resolve(
        IReadOnlyList<CaptureTarget> targets, string? kind, string? match)
    {
        var wantedKind = (kind ?? string.Empty).Trim();
        var wanted = (match ?? string.Empty).Trim();

        if (wantedKind.Equals("monitor", StringComparison.OrdinalIgnoreCase))
            return ResolveMonitor([.. targets.Where(t => t.Kind == CaptureTargetKind.Monitor)], wanted);

        if (wantedKind.Equals("window", StringComparison.OrdinalIgnoreCase))
            return ResolveWindow([.. targets.Where(t => t.Kind == CaptureTargetKind.Window)], wanted);

        return Failed(ScreenTargetResolution.UnknownKind);
    }

    private static ScreenTargetMatch ResolveMonitor(IReadOnlyList<CaptureTarget> monitors, string match)
    {
        if (monitors.Count == 0) return Failed(ScreenTargetResolution.NoMatch);

        if (match.Length == 0)
        {
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
            return new ScreenTargetMatch(ScreenTargetResolution.Resolved, primary, []);
        }

        if (match.Equals("primary", StringComparison.OrdinalIgnoreCase))
        {
            var primary = monitors.FirstOrDefault(m => m.IsPrimary);
            return primary is null
                ? Failed(ScreenTargetResolution.NoMatch)
                : new ScreenTargetMatch(ScreenTargetResolution.Resolved, primary, []);
        }

        var byIndex = int.TryParse(match, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && index >= 1 && index <= monitors.Count
                ? monitors[index - 1]
                : null;
        if (byIndex is not null)
            return new ScreenTargetMatch(ScreenTargetResolution.Resolved, byIndex, []);

        var named = monitors
            .Where(m => m.MonitorDeviceId.Equals(match, StringComparison.OrdinalIgnoreCase)
                || DeviceTail(m.MonitorDeviceId).Equals(match, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return named.Count == 1
            ? new ScreenTargetMatch(ScreenTargetResolution.Resolved, named[0], [])
            : Failed(named.Count > 1 ? ScreenTargetResolution.Ambiguous : ScreenTargetResolution.NoMatch, named);
    }

    private static ScreenTargetMatch ResolveWindow(IReadOnlyList<CaptureTarget> windows, string match)
    {
        if (match.Length == 0) return Failed(ScreenTargetResolution.MissingMatch);

        // The program name wins over a title fragment: a window merely NAMED "outlook" is not the outlook
        // window the user meant.
        var wantedProcess = ScreenCaptureAllowlistMatcher.NormalizeProcessName(match);
        var byProcess = windows
            .Where(w => wantedProcess.Length > 0
                && ScreenCaptureAllowlistMatcher.NormalizeProcessName(w.ProcessName)
                    .Equals(wantedProcess, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (byProcess.Count == 1) return new ScreenTargetMatch(ScreenTargetResolution.Resolved, byProcess[0], []);
        if (byProcess.Count > 1) return Failed(ScreenTargetResolution.Ambiguous, byProcess);

        var byTitle = windows
            .Where(w => w.Title.Contains(match, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (byTitle.Count == 1) return new ScreenTargetMatch(ScreenTargetResolution.Resolved, byTitle[0], []);
        return Failed(byTitle.Count > 1 ? ScreenTargetResolution.Ambiguous : ScreenTargetResolution.NoMatch, byTitle);
    }

    /// <summary>The <c>DISPLAY1</c> a user reads, out of the <c>\\.\DISPLAY1</c> Windows reports.</summary>
    public static string DeviceTail(string monitorDeviceId)
    {
        var cut = monitorDeviceId.LastIndexOf('\\');
        return cut >= 0 && cut < monitorDeviceId.Length - 1 ? monitorDeviceId[(cut + 1)..] : monitorDeviceId;
    }

    private static ScreenTargetMatch Failed(
        ScreenTargetResolution outcome, IReadOnlyList<CaptureTarget>? candidates = null) =>
        new(outcome, null, candidates ?? []);
}

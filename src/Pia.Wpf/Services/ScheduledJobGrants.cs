using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>The ONE place a routine's stored grant list is turned into what will actually be in force at fire
/// time, so the editor, the detail pane and the approval card cannot disagree.</summary>
public static class ScheduledJobGrants
{
    /// <summary>An AgentTask job with no explicit grant silently receives the launcher's default
    /// (<c>ScheduledJobBackgroundService</c> maps empty to null, which <c>HeadlessRunLauncher</c> substitutes),
    /// so any surface showing its grants must render that default rather than an empty line. A Research job
    /// with no grants genuinely is read-only. Returns the caller's own list when non-empty — treat as read-only.</summary>
    public static IReadOnlyList<string> Effective(IReadOnlyList<string> granted, ScheduledJobKind kind) =>
        granted.Count > 0
            ? granted
            : kind == ScheduledJobKind.AgentTask
                ? HeadlessRunRequest.DefaultGrantedWrites
                : [];
}

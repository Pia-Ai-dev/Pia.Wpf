namespace Pia.Services.Interfaces;

/// <summary>Run-id stream of the audit path's accepted appends — the live half of the run panel's
/// tool-activity read, which coalesces its own reloads off it.</summary>
public interface ITimelineWatcher
{
    event Action<Guid>? TimelineAppended;
}

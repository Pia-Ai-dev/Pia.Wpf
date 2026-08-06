using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>The one registered <see cref="IRunObserver"/>: re-broadcasts each accepted timeline event as a
/// run-id event so render surfaces can coalesce their own reloads without the audit service raising change
/// events itself.</summary>
public sealed class RunTimelineWatcher : IRunObserver, ITimelineWatcher
{
    public event Action<Guid>? TimelineAppended;

    public void OnTimelineEvent(AgentTimelineEvent e) => TimelineAppended?.Invoke(e.RunId);
}

using System.Collections.Concurrent;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>Default <see cref="IVolatileWorkStore"/>.</summary>
public sealed class VolatileWorkStore : IVolatileWorkStore
{
    private readonly ConcurrentDictionary<object, bool> _byOwner = new(ReferenceEqualityComparer.Instance);

    public event EventHandler? Changed;

    public bool HasVolatileWork => _byOwner.Any(entry => entry.Value);

    public void Report(object owner, bool hasWork)
    {
        var before = HasVolatileWork;
        _byOwner[owner] = hasWork;
        RaiseIfFlipped(before);
    }

    public void Forget(object owner)
    {
        var before = HasVolatileWork;
        _byOwner.TryRemove(owner, out _);
        RaiseIfFlipped(before);
    }

    // Every publisher reports from the UI thread, so the read-modify-read cannot lose a flip.
    private void RaiseIfFlipped(bool before)
    {
        if (HasVolatileWork != before)
            Changed?.Invoke(this, EventArgs.Empty);
    }
}

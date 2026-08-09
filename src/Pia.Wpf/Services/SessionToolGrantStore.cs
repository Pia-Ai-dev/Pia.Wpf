using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Default <see cref="ISessionToolGrantStore"/>: a locked map of <c>(PluginId, ToolName)</c> keys to the
/// moment each was granted. Registered as a singleton, so its lifetime IS the
/// session — a new instance is a new session, which is why the "a fresh session inherits nothing" fact is a
/// fact about this type and not about a settings file.
/// <para>
/// Written to from the UI thread (an action card's button) and read from background run threads (the
/// unattended gate), hence the lock. A read is a dictionary probe; there is no I/O on any path here, which is
/// why the interactive gate can afford to consult it per tool call.
/// </para>
/// </summary>
public sealed class SessionToolGrantStore : ISessionToolGrantStore
{
    private readonly object _lock = new();

    // No explicit comparer: the default tuple comparer is ordinal and case-SENSITIVE on the name, matching
    // ToolPermissionService._grantedKeys so this tier is never wider than the persisted one it stands in for.
    private readonly Dictionary<(Guid PluginId, string ToolName), DateTimeOffset> _granted = [];

    public event EventHandler? Changed;

    public bool IsGranted(Guid pluginId, string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        lock (_lock)
        {
            return _granted.ContainsKey((pluginId, toolName));
        }
    }

    public void Grant(Guid pluginId, string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return;

        bool added;
        lock (_lock)
        {
            added = _granted.TryAdd((pluginId, toolName), DateTimeOffset.UtcNow);
        }

        if (added)
            RaiseChanged();
    }

    public IReadOnlyList<ToolGrant> List()
    {
        lock (_lock)
        {
            return _granted
                .Select(entry => new ToolGrant(entry.Key.PluginId, entry.Key.ToolName, entry.Value))
                .ToList();
        }
    }

    public void Revoke(Guid pluginId, string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return;

        bool removed;
        lock (_lock)
        {
            removed = _granted.Remove((pluginId, toolName));
        }

        if (removed)
            RaiseChanged();
    }

    // Outside the lock: a handler is free to call straight back into the store, and it may be doing so from a
    // different thread than the one that minted the grant.
    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

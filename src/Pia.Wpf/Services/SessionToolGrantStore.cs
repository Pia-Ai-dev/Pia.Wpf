using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Default <see cref="ISessionToolGrantStore"/>: a locked <see cref="HashSet{T}"/> of
/// <c>(PluginId, ToolName)</c> keys and nothing else. Registered as a singleton, so its lifetime IS the
/// session — a new instance is a new session, which is why the "a fresh session inherits nothing" fact is a
/// fact about this type and not about a settings file.
/// <para>
/// Written to from the UI thread (an action card's button) and read from background run threads (the
/// unattended gate), hence the lock. A read is a set probe; there is no I/O on any path here, which is why
/// the interactive gate can afford to consult it per tool call.
/// </para>
/// </summary>
public sealed class SessionToolGrantStore : ISessionToolGrantStore
{
    private readonly object _lock = new();

    // Default tuple comparer ⇒ ordinal, case-SENSITIVE on the name. Deliberately the same comparer
    // ToolPermissionService._grantedKeys uses, so this tier is never wider than the persisted one it stands in
    // for. See ISessionToolGrantStore.Grant.
    private readonly HashSet<(Guid PluginId, string ToolName)> _granted = [];

    public bool IsGranted(Guid pluginId, string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        lock (_lock)
        {
            return _granted.Contains((pluginId, toolName));
        }
    }

    public void Grant(Guid pluginId, string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return;

        lock (_lock)
        {
            _granted.Add((pluginId, toolName));
        }
    }
}

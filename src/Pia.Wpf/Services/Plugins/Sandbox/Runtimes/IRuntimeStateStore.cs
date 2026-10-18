namespace Pia.Services.Plugins.Sandbox.Runtimes;

public interface IRuntimeStateStore
{
    void WriteGrantRecord(string containerName, IReadOnlyList<string> roots, bool stateReadOnly = false);
    /// <returns>Null without a record; true only when this container's last start got its state folder read-only.</returns>
    bool? StateGrantedReadOnly(string containerName);
    IReadOnlyList<string> SweepRoots(string prefix);
    /// <returns>False when a start of this prefix is preparing or running; nothing was touched then.</returns>
    bool TryDelete(string prefix, Action? beforeDelete = null);
    IReadOnlyList<string> OrphanPrefixes(IReadOnlySet<string> knownPrefixes);
}

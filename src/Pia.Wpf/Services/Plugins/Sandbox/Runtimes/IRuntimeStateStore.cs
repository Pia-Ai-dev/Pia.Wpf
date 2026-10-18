namespace Pia.Services.Plugins.Sandbox.Runtimes;

public interface IRuntimeStateStore
{
    void WriteGrantRecord(string containerName, IReadOnlyList<string> roots);
    IReadOnlyList<string> SweepRoots(string prefix);
    /// <returns>False when a start of this prefix is preparing or running; nothing was touched then.</returns>
    bool TryDelete(string prefix, Action? beforeDelete = null);
    IReadOnlyList<string> OrphanPrefixes(IReadOnlySet<string> knownPrefixes);
}

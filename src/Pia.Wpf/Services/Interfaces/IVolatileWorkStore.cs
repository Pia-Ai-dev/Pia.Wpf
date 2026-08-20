namespace Pia.Services.Interfaces;

/// <summary>Cross-window record of in-memory work a restart would destroy. The publishers are per-window
/// scoped objects, but every window has to obey all of them, so the answer cannot live in one scope.</summary>
public interface IVolatileWorkStore
{
    /// <summary>True while any owner reports work that would not survive a restart.</summary>
    bool HasVolatileWork { get; }

    event EventHandler? Changed;

    /// <summary>Records <paramref name="owner"/>'s answer. Keyed by reference, so one window's report
    /// can never overwrite another's.</summary>
    void Report(object owner, bool hasWork);

    /// <summary>Drops <paramref name="owner"/>'s answer. A report left behind by a closed window would
    /// defer the restart overlay for the rest of the process.</summary>
    void Forget(object owner);
}

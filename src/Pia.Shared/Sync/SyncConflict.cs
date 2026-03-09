namespace Pia.Shared.Sync;

/// <summary>
/// Represents a conflict detected during push.
/// </summary>
public class SyncConflict
{
    /// <summary>Entity type that conflicted (e.g., "templates", "providers", "memories").</summary>
    public required string Entity { get; set; }

    /// <summary>Id of the conflicting entity.</summary>
    public Guid Id { get; set; }

    /// <summary>The server's version of the entity (as JSON).</summary>
    public object? ServerVersion { get; set; }

    /// <summary>How the conflict was resolved.</summary>
    public required string Resolution { get; set; }
}

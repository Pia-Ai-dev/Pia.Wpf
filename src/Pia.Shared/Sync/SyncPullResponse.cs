namespace Pia.Shared.Sync;

using Pia.Shared.Models;

/// <summary>
/// Response from GET /api/sync/pull?since={timestamp}.
/// Contains all entities changed since the given timestamp.
/// </summary>
public class SyncPullResponse
{
    public DateTime ServerTimestamp { get; set; }

    /// <summary>Settings if modified since last sync, null otherwise.</summary>
    public SyncSettings? Settings { get; set; }

    public SyncEntityChanges<SyncTemplate> Templates { get; set; } = new();
    public SyncEntityChanges<SyncPersona> Personas { get; set; } = new();
    public SyncEntityChanges<SyncProvider> Providers { get; set; } = new();
    public SyncSessionChanges Sessions { get; set; } = new();
    public SyncEntityChanges<SyncMemory> Memories { get; set; } = new();
    public SyncEntityChanges<SyncTodo> Todos { get; set; } = new();
    public SyncEntityChanges<SyncKanbanColumn> KanbanColumns { get; set; } = new();
    public SyncEntityChanges<SyncScheduledJob> ScheduledJobs { get; set; } = new();
    public SyncEntityChanges<SyncResearchSession> ResearchSessions { get; set; } = new();
    public SyncEntityChanges<SyncPlugin> Plugins { get; set; } = new();

    /// <summary>
    /// Admin-authored personas published to the caller's group — a REPLACE-ALL snapshot, not a merge.
    /// Deliberately NOT a <see cref="SyncEntityChanges{T}"/>: see <see cref="SyncManagedPersonaSnapshot"/>.
    /// <para>
    /// Null means the catalog fast-skip fired — and because the server omits nulls app-wide, null is an
    /// ABSENT KEY on the wire, never <c>"managedPersonas": null</c>. An absent key means KEEP THE STORE
    /// UNCHANGED; it never means "empty, delete everything". A present object — including an empty one —
    /// is authoritative.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Must stay nullable with NO <c>= new()</c> initializer: the absent-key contract depends on it
    /// deserializing to null when the server omits the property.
    /// </remarks>
    public SyncManagedPersonaSnapshot? ManagedPersonas { get; set; }

    /// <summary>Current plugin catalog version echoed back for the client's next pull; null when not provided.
    /// Treat it as an OPAQUE token: it is compared for equality only and is NOT monotonic — the server folds
    /// the caller's group into it so a group reassignment self-invalidates. Store it and echo it back
    /// verbatim; never compare two values with &lt; or &gt;.</summary>
    public long? CatalogVersion { get; set; }

    /// <summary>Devices awaiting approval, returned inline so the client can prompt without a follow-up call; null when none/unset.</summary>
    public List<SyncPendingDevice>? PendingDevices { get; set; }

    /// <summary>True when more changes remain beyond this response (paged pull); null when not applicable.</summary>
    public bool? HasMore { get; set; }
}

/// <summary>
/// Changes for entities that support upsert and delete (templates, providers, memories).
/// </summary>
public class SyncEntityChanges<T>
{
    public List<T> Upserted { get; set; } = [];
    public List<Guid> Deleted { get; set; } = [];
}

/// <summary>
/// The managed-persona channel's payload — an authoritative REPLACE-ALL snapshot of every managed persona
/// the caller's group may see.
/// <para>
/// This is a distinct type, and its wire fields are deliberately NOT named <c>upserted</c>/<c>deleted</c>,
/// for one reason: every other channel on <see cref="SyncPullResponse"/> is a
/// <see cref="SyncEntityChanges{T}"/> and MERGES. This one must not. Unassignment — an admin removing the
/// caller's group from a persona — is conveyed ONLY by absence from <see cref="Personas"/> and carries no
/// tombstone at all, so a client that merged this channel would keep a revoked persona forever. Reusing
/// the shared shape would let exactly that mistake compile and pass review, so the shape itself refuses:
/// a snapshot cannot be handed to the merge helper.
/// </para>
/// <para>
/// The correct client apply is one line: replace the entire local managed-persona store with
/// <see cref="Personas"/>. Nothing else, and no diffing.
/// </para>
/// </summary>
public class SyncManagedPersonaSnapshot
{
    /// <summary>
    /// Every managed persona the caller's group may currently see, in full. THE store after this pull —
    /// not a delta against it. Empty means the group has none, which is a real, authoritative answer:
    /// clear the local store.
    /// </summary>
    public List<SyncManagedPersona> Personas { get; set; } = [];

    /// <summary>
    /// Personas soft-deleted or deactivated by an admin while still assigned to the caller's group, for a
    /// bounded retention window. Purely CONFIRMATION — a correct client ignores this entirely, because
    /// replacing the store with <see cref="Personas"/> already drops them. Consume it only to distinguish
    /// "an admin removed this" from "it fell out of scope" in a UI message. Never treat it as the removal
    /// mechanism: it does not, and cannot, cover unassignment.
    /// </summary>
    public List<Guid> RecentlyRemoved { get; set; } = [];
}

/// <summary>
/// Changes for sessions (append-only, no updates).
/// </summary>
public class SyncSessionChanges
{
    public List<SyncSession> Added { get; set; } = [];
    public List<Guid> Deleted { get; set; } = [];
}

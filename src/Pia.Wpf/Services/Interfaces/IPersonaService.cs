using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IPersonaService
{
    event EventHandler? PersonasChanged;

    /// <summary>
    /// Raised by <see cref="ReplaceManagedPersonasAsync"/> when a persona the user had SELECTED for
    /// some window mode is no longer in the snapshot (deleted, deactivated, or the group unassigned).
    /// The dangling selection has already been cleared, so <see cref="ResolveActiveAsync"/> will fall back
    /// to the operating-mode built-in. Raised BEFORE <see cref="PersonasChanged"/> so a subscriber can
    /// stash the name and surface it on the reload that <see cref="PersonasChanged"/> triggers.
    /// </summary>
    event EventHandler<ManagedPersonaWithdrawnEventArgs>? ManagedPersonaWithdrawn;

    /// <summary>
    /// Built-ins ∪ managed ∪ user personas, in that order: built-ins first, then managed, then user
    /// personas. Never empty (built-ins always present).
    /// </summary>
    Task<IReadOnlyList<Persona>> GetPersonasAsync();

    Task<Persona?> GetPersonaAsync(Guid id);

    /// <summary>The managed (admin-published) personas currently delivered to this user.</summary>
    Task<IReadOnlyList<Persona>> GetManagedPersonasAsync();

    /// <summary>
    /// Replaces the ENTIRE managed store with the pull's authoritative snapshot (this channel is
    /// replace-all, unlike every other sync channel). Called only by the sync client.
    /// </summary>
    Task ReplaceManagedPersonasAsync(IReadOnlyList<Persona> personas);

    /// <summary>Persists a user persona (forces <c>IsBuiltIn = false</c>); preserves supplied timestamps.</summary>
    Task<Persona> AddPersonaAsync(Persona persona);

    /// <summary>Updates a user persona (bumps <c>UpdatedAt</c>). Throws if the id is a built-in or managed.</summary>
    Task UpdatePersonaAsync(Persona persona);

    /// <summary>
    /// Deletes a user persona and tracks the deletion as <c>"personas"</c>. No-op for built-ins and for
    /// managed ids — a managed id never reaches the delete tracker (it must not enqueue a push tombstone).
    /// </summary>
    Task DeletePersonaAsync(Guid id);

    /// <summary>
    /// Resolves the active persona for a mode. Falls back to the <see cref="UserOperatingMode"/>-mapped
    /// Pia built-in when nothing is selected or the selection is unknown. Never returns null.
    /// </summary>
    Task<Persona> ResolveActiveAsync(WindowMode mode, UserOperatingMode operatingMode);
}

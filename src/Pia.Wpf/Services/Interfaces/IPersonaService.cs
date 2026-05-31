using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IPersonaService
{
    event EventHandler? PersonasChanged;

    /// <summary>Built-ins ∪ user personas, built-ins first. Never empty (built-ins always present).</summary>
    Task<IReadOnlyList<Persona>> GetPersonasAsync();

    Task<Persona?> GetPersonaAsync(Guid id);

    /// <summary>Persists a user persona (forces <c>IsBuiltIn = false</c>); preserves supplied timestamps.</summary>
    Task<Persona> AddPersonaAsync(Persona persona);

    /// <summary>Updates a user persona (bumps <c>UpdatedAt</c>). Throws if the id is a built-in.</summary>
    Task UpdatePersonaAsync(Persona persona);

    /// <summary>Deletes a user persona and tracks the deletion as <c>"personas"</c>. No-op for built-ins.</summary>
    Task DeletePersonaAsync(Guid id);

    /// <summary>
    /// Resolves the active persona for a mode. Falls back to the <see cref="UserOperatingMode"/>-mapped
    /// Pia built-in when nothing is selected or the selection is unknown. Never returns null.
    /// </summary>
    Task<Persona> ResolveActiveAsync(WindowMode mode, UserOperatingMode operatingMode);
}

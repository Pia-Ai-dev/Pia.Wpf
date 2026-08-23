using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// The one persona ladder and the one effort ladder every unattended run leg resolves a job's pins through.
/// Static so neither dispatch leg gains a constructor dependency.
/// </summary>
internal static class RunPinResolver
{
    /// <summary>An unresolvable pin logs its id and a reason token — never a persona name.</summary>
    public static async Task<Persona> ResolvePersonaAsync(
        IPersonaService personas, Guid? pinnedId, UserOperatingMode mode, ILogger logger)
    {
        if (pinnedId is { } id && id != Guid.Empty)
        {
            try
            {
                // GetPersonasAsync, not GetPersonaAsync: only the former filters BlockedBuiltInPersonas, and a
                // user-chosen pin obeys the same policy gate as a user-chosen per-mode default.
                var available = await personas.GetPersonasAsync().ConfigureAwait(false);
                if (available.FirstOrDefault(p => p.Id == id) is { } pinned)
                    return pinned;

                logger.LogInformation(
                    "Pinned run persona {PersonaId} could not be resolved; using the mode persona ({Reason})",
                    id, "unresolvable-persona");
            }
            catch (Exception ex)
            {
                // Exception TYPE only: a persona store's message can embed a persona name.
                logger.LogWarning(
                    "Pinned run persona {PersonaId} could not be read ({Error}); using the mode persona",
                    id, ex.GetType().Name);
            }
        }

        return await personas.ResolveActiveAsync(WindowMode.Assistant, mode).ConfigureAwait(false);
    }

    /// <summary>The effort ladder alone, for a caller that PERSISTS what a dispatch resolved. Null leaves the
    /// provider's own setting.</summary>
    public static ReasoningEffort? EffectiveEffort(ReasoningEffort? jobPin, ReasoningEffort? personaEffort) =>
        jobPin ?? personaEffort;

    /// <summary>Clones rather than stamping the instance: <see cref="AiProvider"/> objects come out of a shared
    /// store, so mutating one leaks a single run's effort into every other consumer.</summary>
    public static AiProvider ApplyEffort(AiProvider provider, ReasoningEffort? jobPin, ReasoningEffort? personaEffort)
    {
        if (EffectiveEffort(jobPin, personaEffort) is not { } effort)
            return provider;

        var stamped = provider.Clone();
        stamped.ReasoningEffort = effort;
        return stamped;
    }
}

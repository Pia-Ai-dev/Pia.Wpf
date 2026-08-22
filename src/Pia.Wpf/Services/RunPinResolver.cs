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
    /// <summary>
    /// Resolves a USER-authored persona pin. Deliberately not roster-gated, unlike a planner-assigned id: the
    /// roster is the allow-list for what a planner may assign, is empty by default and caps at six, so gating
    /// this on it would ignore every choice the picker offers. A pin that no longer resolves falls back to the
    /// mode persona rather than failing the run, and logs the id and a reason token — never a persona name.
    /// </summary>
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

    /// <summary>
    /// The job's pin outranks the persona's — it is the more specific, more recent, user-authored statement.
    /// Clones rather than mutating: <see cref="AiProvider"/> instances come out of a shared store, so stamping
    /// the instance we were handed leaks one run's effort into every other consumer of that provider.
    /// </summary>
    public static AiProvider ApplyEffort(AiProvider provider, ReasoningEffort? jobPin, ReasoningEffort? personaEffort)
    {
        if ((jobPin ?? personaEffort) is not { } effort)
            return provider;

        var stamped = provider.Clone();
        stamped.ReasoningEffort = effort;
        return stamped;
    }
}

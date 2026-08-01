namespace Pia.Services.Interfaces;

/// <summary>
/// Payload for <see cref="IPersonaService.ManagedPersonaWithdrawn"/>: the managed persona that was
/// selected for some window mode and is no longer in the snapshot. Carries only the WITHDRAWN persona —
/// the subscriber resolves the fallback name itself, because the fallback depends on the operating mode
/// the subscriber is running in, not on anything the service knows.
/// </summary>
public class ManagedPersonaWithdrawnEventArgs : EventArgs
{
    public required Guid PersonaId { get; init; }

    /// <summary>
    /// User-visible name of the withdrawn persona. Admin-authored user content: fine in a snackbar,
    /// never in the log file outside <c>SensitiveDebug</c>.
    /// </summary>
    public required string PersonaName { get; init; }
}

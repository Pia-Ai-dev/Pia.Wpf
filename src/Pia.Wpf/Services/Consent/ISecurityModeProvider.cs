using Pia.Models;

namespace Pia.Services.Consent;

public sealed record SecurityProfileChangedEventArgs(SecurityProfile OldProfile, SecurityProfile NewProfile);

/// <summary>
/// Single source of truth for the currently selected security profile. Subscribes to
/// <c>ISettingsService</c> so changes made in the Settings UI propagate to the
/// orchestrator factory and any other consumers.
/// </summary>
public interface ISecurityModeProvider
{
    SecurityProfile Current { get; }

    event EventHandler<SecurityProfileChangedEventArgs>? ProfileChanged;

    Task SetModeAsync(SecurityMode mode, CancellationToken cancellationToken = default);
}

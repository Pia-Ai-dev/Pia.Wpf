namespace Pia.Shared.Models;

/// <summary>
/// Push-only DTO for client plugin preference changes.
/// Clients can only toggle enable/disable, never modify plugin definitions.
/// </summary>
public class SyncPluginPreference
{
    public Guid PluginId { get; set; }
    public bool IsEnabled { get; set; }
}

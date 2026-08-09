namespace Pia.Models;

/// <summary>
/// A display row for the tool-permissions revocation surface: a standing or session grant
/// resolved against its owning plugin's display name. A plain record (not an
/// ObservableObject) so it stays clear of the Pia.ViewModels MVVM guardrails —
/// it lives in Pia.Models alongside <see cref="ToolGrant"/>.
/// </summary>
public record ToolGrantRow(Guid PluginId, string PluginName, string ToolName, DateTimeOffset GrantedAt);

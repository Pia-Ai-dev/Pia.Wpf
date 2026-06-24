using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// Owns the deny-by-default auto-approve eligibility classification and the
/// persisted per-(PluginId, ToolName) "always allow" grants. Singleton.
/// </summary>
public interface IToolPermissionService
{
    /// <summary>
    /// Deny-by-default: true only for tools in the curated safe/additive
    /// allowlist. Overwrite-class (write_file) and delete_* tools are always false.
    /// Enforced at the gate, never trusted from the card.
    /// </summary>
    bool IsAutoApproveEligible(string toolName);

    /// <summary>True if the user has a standing grant for this exact (plugin, tool).</summary>
    bool IsGranted(Guid pluginId, string toolName);

    Task GrantAsync(Guid pluginId, string toolName);

    Task RevokeAsync(Guid pluginId, string toolName);

    IReadOnlyList<ToolGrant> List();

    /// <summary>Raised after a grant, revoke, or external settings change.</summary>
    event EventHandler? Changed;
}

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

    /// <summary>
    /// hermes #15. True if the user granted this exact (plugin, tool) for the rest of THIS APP SESSION — the
    /// middle tier, held in the process-scoped <see cref="ISessionToolGrantStore"/> and never persisted.
    /// Independent of <see cref="IsGranted"/>: neither implies the other, and a session grant appears in
    /// <see cref="List"/> nowhere because there is no durable row to list.
    /// </summary>
    bool IsGrantedForSession(Guid pluginId, string toolName);

    /// <summary>
    /// hermes #15. Record a session grant for this exact (plugin, tool). Writes NOTHING to
    /// <c>AppSettings.AlwaysAllowedTools</c> — that is the point of the tier — and is therefore synchronous
    /// and cannot fail. The caller must still have checked
    /// <see cref="ToolAutonomy.IsSessionGrantOfferable"/>; the store holds keys and enforces no policy.
    /// </summary>
    void GrantForSession(Guid pluginId, string toolName);

    Task GrantAsync(Guid pluginId, string toolName);

    Task RevokeAsync(Guid pluginId, string toolName);

    IReadOnlyList<ToolGrant> List();

    /// <summary>Raised after a grant, revoke, or external settings change.</summary>
    event EventHandler? Changed;
}
